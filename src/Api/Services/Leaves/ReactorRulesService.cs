using System.Text.Json;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>What came of storing a set of rules.</summary>
/// <param name="Ok">Whether the file was written and the leaf restarted on it.</param>
/// <param name="ErrorMessage">Why not, when it was not.</param>
/// <param name="IsConflict">
/// True when the request was fine and this host is not wired to deliver it — a <c>409</c>, not a
/// <c>400</c>.
/// </param>
/// <param name="Path">Where the rules were written.</param>
/// <param name="Problems">
/// What the leaf could not honour, read back from it after the restart. <b>Empty is the success case
/// and a non-empty list is not a failure</b>: the file was stored, the rules it could honour are
/// running, and these are the ones it could not.
/// </param>
/// <param name="Live">The ids of the rules the leaf is actually evaluating now.</param>
public sealed record ReactorRulesResult(
    bool Ok,
    string? ErrorMessage,
    bool IsConflict,
    string? Path,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Live)
{
    public static ReactorRulesResult Refused(string message, bool conflict = false) =>
        new(false, message, conflict, null, [], []);
}

/// <summary>
/// Stores the rules this host's reactor runs, and points the leaf at them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The panel composes and writes; the leaf publishes and judges.</b> The reactor's socket is
/// read-only — it says what a rule may be made of and what it would decide, and never accepts one — so
/// storing a rule is this API's half of the arrangement. It writes a file and restarts the unit
/// through the grant it already holds for every other leaf setting, which is why nothing off this host
/// acquires the ability to tell a leaf what to think.
/// </para>
/// <para>
/// ⚠ <b>The file is this API's, and the leaf is told a path.</b> It lives in the override directory
/// beside the env files, and <c>Reactor__RulesPath</c> is set to it through the ordinary config
/// channel. The leaf never learns whose file it is — a host with no panel keeps its own rules in its
/// own state directory and reads them the same way, which is what stops this becoming the first leaf
/// that depends on the API.
/// </para>
/// <para>
/// ⚠ <b>What is stored and what is running are different questions, and both are answered.</b> A rule
/// the leaf refuses is in the file and not in its live list, so an editor built only on the leaf's
/// status would silently drop the rule somebody is halfway through fixing. The file is served back
/// verbatim for editing; <c>problems</c> from the leaf says which of it did not take.
/// </para>
/// </remarks>
public sealed class ReactorRulesService(
    LeafConfigCatalog catalog,
    LeafConfigService config,
    LeafOverrideStore overrides,
    IUnitController units,
    ReactorClient reactor,
    ApiJournal journal,
    ApiOptions options,
    ILogger<ReactorRulesService> logger)
{
    /// <summary>The leaf this writes for, as the config catalog and the override store name it.</summary>
    private const string LeafId = "reactor";

    /// <summary>The settings key that tells the leaf where its rules are.</summary>
    private const string PathKey = "rulesPath";

    /// <summary>How long the leaf is given to come back and report what it made of the file.</summary>
    /// <remarks>
    /// Generous, because the answer is the point: a write that returned before the leaf had re-read the
    /// file would report the previous run's problems, which is worse than reporting none.
    /// </remarks>
    private static readonly TimeSpan ReadBackWithin = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ReadBackEvery = TimeSpan.FromMilliseconds(500);

    // One writer at a time: a second write landing between the file and the restart would be read by
    // neither run.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Where the rules this API manages are written.</summary>
    public string Path => System.IO.Path.Combine(options.LeafOverridesDir, "reactor.rules.json");

    /// <summary>
    /// The rules as stored, verbatim, or null when this API manages none.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary case on a host nobody has edited: the leaf then runs the rules it ships,
    /// which its own status reports. It is not an error and must not be rendered as an empty rule set.
    /// </remarks>
    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        try
        {
            return File.Exists(Path)
                ? await File.ReadAllTextAsync(Path, ct).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "could not read the stored reactor rules at {Path}", Path);
            return null;
        }
    }

    /// <summary>
    /// Store a set of rules and restart the leaf onto them.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>What a rule means is checked by the leaf, not here.</b> This validates that the body is a
    /// rules document and nothing else: the signals, operators and actions a rule may use are the
    /// running build's to define, and a second copy of that judgement here is how the panel and the
    /// leaf come to disagree about which rules are valid. The leaf's verdict is read back and returned.
    /// </remarks>
    public async Task<ReactorRulesResult> WriteAsync(
        string body, string? actor, string? origin, CancellationToken ct)
    {
        LeafConfigIdentity? identity = catalog.Identity(LeafId);
        if (identity is null)
            return ReactorRulesResult.Refused("this host runs no reactor to give rules to");

        // Without the override drop-in the write renders a file nothing reads and the restart then
        // fails — refuse up front, with the fix, rather than half-applying.
        if (!catalog.IsEditable(LeafId, out string? locked))
            return ReactorRulesResult.Refused(locked!, conflict: true);

        if (!IsRulesDocument(body, out string? shapeProblem))
            return ReactorRulesResult.Refused(shapeProblem!);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                Directory.CreateDirectory(options.LeafOverridesDir);

                // Written beside the target and renamed, so a reader never sees half a file — a leaf
                // starting mid-write would refuse every rule and report a parse error at a byte offset
                // nobody wrote.
                string staging = Path + ".new";
                await File.WriteAllTextAsync(staging, body, ct).ConfigureAwait(false);
                File.Move(staging, Path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "could not write the reactor rules to {Path}", Path);
                return ReactorRulesResult.Refused($"the rules could not be written: {ex.Message}");
            }

            bool restarted = await PointAndRestartAsync(identity, actor, origin, ct).ConfigureAwait(false);
            if (!restarted)
            {
                await AuditAsync("failed", actor, origin, ct).ConfigureAwait(false);
                return ReactorRulesResult.Refused(
                    "the rules were stored but the reactor could not be restarted onto them");
            }

            (IReadOnlyList<string> problems, IReadOnlyList<string> live) =
                await ReadBackAsync(ct).ConfigureAwait(false);

            await AuditAsync(problems.Count == 0 ? "applied" : "applied_with_problems", actor, origin, ct)
                .ConfigureAwait(false);

            return new ReactorRulesResult(true, null, false, Path, problems, live);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Make sure the leaf is looking at this file, and get it to re-read it.
    /// </summary>
    /// <remarks>
    /// The path is set through the ordinary config channel, which restarts the unit itself with its own
    /// canary and audit row. When it is already right there is nothing for that channel to do — and a
    /// new file still needs a restart to be read, so one is asked for directly.
    /// </remarks>
    private async Task<bool> PointAndRestartAsync(
        LeafConfigIdentity identity, string? actor, string? origin, CancellationToken ct)
    {
        IReadOnlyList<LeafOverrideRow> stored = await overrides.GetAsync(LeafId, ct).ConfigureAwait(false);
        string? current = stored.FirstOrDefault(r =>
            string.Equals(r.Key, PathKey, StringComparison.Ordinal))?.Value;

        if (!string.Equals(current, Path, StringComparison.Ordinal))
        {
            LeafConfigApplyResponse response = await config.ApplyAsync(
                LeafId,
                new LeafConfigUpdate(new Dictionary<string, string> { [PathKey] = Path }, null),
                actor, origin, ct).ConfigureAwait(false);

            if (response.Result is null)
            {
                logger.LogError(
                    "could not point the reactor at {Path}: {Reason}", Path, response.ErrorMessage);
                return false;
            }

            return true;
        }

        return await units.RestartAsync(identity.Unit, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What the leaf made of the file, once it is answering again.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Polled rather than assumed.</b> A restart returns as soon as systemd has started the
    /// process, which is before the reactor has read anything — reporting at that moment would return
    /// the previous run's verdict on a file it never saw.
    /// </remarks>
    private async Task<(IReadOnlyList<string> Problems, IReadOnlyList<string> Live)> ReadBackAsync(
        CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ReadBackWithin;

        while (DateTimeOffset.UtcNow < deadline)
        {
            string? status = await reactor.GetStatusJsonAsync(ct).ConfigureAwait(false);

            if (status is not null && TryReadStatus(status, out var problems, out var live))
                return (problems, live);

            await Task.Delay(ReadBackEvery, ct).ConfigureAwait(false);
        }

        // Silence is not a clean bill of health. The rules are stored and the leaf is not saying what it
        // made of them, and a caller told "no problems" would read that as success.
        return (["the reactor did not report back what it made of these rules"], []);
    }

    internal static bool TryReadStatus(
        string json, out IReadOnlyList<string> problems, out IReadOnlyList<string> live)
    {
        problems = [];
        live = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("problems", out JsonElement found)
                && found.ValueKind == JsonValueKind.Array)
            {
                problems = [.. found.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)];
            }

            if (root.TryGetProperty("rules", out JsonElement rules)
                && rules.ValueKind == JsonValueKind.Array)
            {
                live = [.. rules.EnumerateArray()
                    .Select(e => e.TryGetProperty("id", out JsonElement id) ? id.GetString() : null)
                    .Where(id => id is not null)
                    .Select(id => id!)];
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the body is a rules document at all.
    /// </summary>
    /// <remarks>
    /// The shallowest possible check, deliberately. Storing something that is not a rules file would
    /// leave the leaf refusing everything with a parse error, so the shape is worth confirming — but
    /// what a rule may contain is the running build's to say, and a second opinion here is how a panel
    /// comes to refuse a rule the leaf would have accepted.
    /// </remarks>
    internal static bool IsRulesDocument(string body, out string? problem)
    {
        problem = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem = "a rules file is an object with a 'rules' array";
                return false;
            }

            if (!document.RootElement.TryGetProperty("rules", out JsonElement rules)
                || rules.ValueKind != JsonValueKind.Array)
            {
                problem = "a rules file needs a 'rules' array";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            problem = $"the rules could not be read at line {ex.LineNumber}, "
                      + $"position {ex.BytePositionInLine}: {ex.Message}";
            return false;
        }
    }

    // The fact that the rules changed, never what they say — a rule's steps are a decision an operator
    // reads on the rule card, and copying them into an append-only log would freeze a draft nobody
    // meant to keep.
    private Task AuditAsync(string outcome, string? actor, string? origin, CancellationToken ct) =>
        journal.ServiceConfigAsync(
            LeafId, "Reactor", ["rules"], outcome,
            actor ?? "", AuditMapping.NormalizeOrigin(origin), ct);
}
