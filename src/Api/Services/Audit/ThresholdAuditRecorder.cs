using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Writes an audit row for every threshold breach and recovery this host records, transcribed from
/// kgsm-monitor's own durable episodes.
/// </summary>
/// <remarks>
/// <para><b>Sourced from the monitor's record, not from the alert feed.</b> The alert engine holds firing
/// conditions in memory and forgets them on restart, so driving rows from it would lose any episode that
/// spanned one. The monitor persists each episode as it opens and closes; reading from there means a
/// restart, a slow tick and a normal poll are all the same code path, and the gap simply gets picked up on
/// the next pass.</para>
/// <para><b>The rows say who established the fact.</b> The actor is <c>system:monitor</c> — the ecosystem's
/// autonomous-emitter form, the same one kgsm-watchdog stamps as <c>system:watchdog</c>. This API is the
/// writer and not the source, and a log that said a bare <c>system</c> could not tell a threshold breach
/// from any other unattended action. The identity comes from the leaf this client reads through
/// (<see cref="ProvisionableLeaf.Monitor"/>), never from a field in the payload: a claim about identity
/// made inside data is a claim this API cannot check, and the socket it dialled is a fact it established
/// itself.</para>
/// <para><b>Origin stays <c>system</c>.</b> Origin is the surface that drove an action, and none did. The
/// closed origin vocabulary has no per-component value and widening it is the wrong move — producer
/// identity belongs in the actor, which is the field for it.</para>
/// <para><b>At-least-once, deduplicated on the episode id.</b> Each poll re-reads a window that overlaps
/// what it already saw, because the alternative is a boundary where an event lands on the exact millisecond
/// of the last watermark and is skipped. <see cref="_written"/> makes the overlap free, and is seeded at
/// startup from the rows already in the audit log so a restart does not re-announce a month of history.</para>
/// </remarks>
public sealed class ThresholdAuditRecorder(
    MonitorClient monitor,
    AuditService audit,
    IServiceScopeFactory scopeFactory,
    ApiOptions options,
    ILogger<ThresholdAuditRecorder> logger) : BackgroundService
{
    // These are a historical record, not a live feed — the alert stream already tells an open browser what
    // is happening this second. A slower poll costs nothing anyone sees and keeps the socket quiet.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    // How far back a cold start looks. Long enough to cover a deploy or a crash, short enough that a host
    // whose api has been off for a week does not wake up and write a week of history at once.
    private static readonly TimeSpan ColdStartLookback = TimeSpan.FromHours(24);

    // Re-read this much of what we have already seen on every pass. The dedup set makes it free, and it is
    // what removes the boundary case where an episode's timestamp equals the watermark exactly.
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    private const int MaxEpisodesPerPoll = 500;

    /// <summary>The producer identity these rows carry, as the flat <c>provider:name</c> form every
    /// consumer parses. The name is the LEAF ID — the same word the descriptor, the services board and
    /// the capability block all address this daemon by — not the package or unit name.</summary>
    private static readonly string MonitorActor = $"{ActorProvider.System}:{ProvisionableLeaf.Monitor}";

    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private long _sinceMs;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.MetricsProvisioned)
        {
            logger.LogInformation("threshold audit: no monitor on this host — nothing to record.");
            return;
        }

        _sinceMs = DateTimeOffset.UtcNow.Subtract(ColdStartLookback).ToUnixTimeMilliseconds();
        await SeedWrittenAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try { await PollAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "threshold audit: poll failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        string? json = await monitor.GetEpisodesJsonAsync(_sinceMs, MaxEpisodesPerPoll, ct).ConfigureAwait(false);
        if (json is null) return; // monitor down / no episode store: hold, never assume nothing fired.

        List<EpisodeDto> episodes;
        try { episodes = ParseEpisodes(json); }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "threshold audit: could not read the monitor's episodes");
            return;
        }

        long newestSeen = _sinceMs;

        foreach (EpisodeDto ep in episodes)
        {
            newestSeen = Math.Max(newestSeen, Math.Max(ep.OpenedTs, ep.ClosedTs ?? 0));

            await WriteIfNewAsync(ep, closed: false, ct).ConfigureAwait(false);
            if (ep.ClosedTs is not null)
                await WriteIfNewAsync(ep, closed: true, ct).ConfigureAwait(false);
        }

        // Advance, but keep re-reading an overlap so nothing can fall between two polls on a tie.
        _sinceMs = Math.Max(_sinceMs, newestSeen - (long)Overlap.TotalMilliseconds);
    }

    private async Task WriteIfNewAsync(EpisodeDto ep, bool closed, CancellationToken ct)
    {
        string key = DedupKey(ep.EpisodeId, closed);
        if (!_written.Add(key)) return;

        try
        {
            await audit.AppendAsync(BuildWrite(ep, closed), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Let the next poll try again: the overlap window will still carry this episode, and a row
            // silently dropped is worse than one written a minute late.
            _written.Remove(key);
            logger.LogWarning(ex, "threshold audit: could not write the row for {Episode}", ep.EpisodeId);
        }
    }

    private AuditWrite BuildWrite(EpisodeDto ep, bool closed)
    {
        bool hostScope = !string.Equals(ep.Scope, "server", StringComparison.Ordinal);
        string noun = ConditionDisplay.Noun(ep.Metric);
        string subject = hostScope
            ? (string.IsNullOrEmpty(ep.Ref) ? options.HostId : ep.Ref!)
            : (ep.ServerId ?? "server");

        // The row's timestamp is the moment the condition changed, not the moment this loop noticed. A
        // reader scanning the trail has to see the breach where it happened, not where a poll landed.
        DateTimeOffset ts = DateTimeOffset.FromUnixTimeMilliseconds(closed ? ep.ClosedTs!.Value : ep.OpenedTs);

        var meta = new Dictionary<string, string>
        {
            ["episodeId"] = ep.EpisodeId,
            ["ruleKey"] = ep.RuleKey,
            ["metric"] = ep.Metric,
            ["threshold"] = ConditionDisplay.Format(ep.Metric, ep.Threshold),
            ["peak"] = ConditionDisplay.Format(ep.Metric, ep.PeakValue),
        };
        if (!string.IsNullOrEmpty(ep.Ref)) meta["ref"] = ep.Ref!;

        string summary;
        if (closed)
        {
            long heldSec = Math.Max(0, (ep.ClosedTs!.Value - ep.OpenedTs) / 1000);
            meta["heldSec"] = heldSec.ToString(System.Globalization.CultureInfo.InvariantCulture);
            meta["value"] = ConditionDisplay.Format(ep.Metric, ep.CloseValue ?? ep.PeakValue);
            if (!string.IsNullOrEmpty(ep.CloseReason)) meta["reason"] = ep.CloseReason!;

            // An episode that ended because its rule was retuned, disabled or removed did NOT recover: the
            // value was never observed to come down. Saying "back to normal" would be reporting a
            // measurement nobody took, which is the one thing this trail must not do.
            summary = ep.CloseReason switch
            {
                // The rule was retuned, disabled or removed while this was firing.
                "unwatched" => $"{subject} {noun} no longer watched after {ConditionDisplay.Duration(heldSec)} over its threshold",
                // The monitor restarted while this was firing. The condition may well have still been true.
                "interrupted" => $"{subject} {noun} still over its threshold when monitoring stopped, after {ConditionDisplay.Duration(heldSec)}",
                _ => $"{subject} {noun} back to normal after {ConditionDisplay.Duration(heldSec)}",
            };
        }
        else
        {
            meta["value"] = ConditionDisplay.Format(ep.Metric, ep.OpenValue);
            summary = $"{subject} {noun} crossed {ConditionDisplay.Format(ep.Metric, ep.Threshold)}";
        }

        return new AuditWrite(
            Ts: ts,
            Origin: AuditOrigin.System,
            Actor: AuditMapping.ParseActor(MonitorActor),
            Action: closed ? AuditAction.HostThresholdClear : AuditAction.HostThresholdBreach,
            // A recovery is information, never a warning. A breach is as loud as the band it reached.
            Severity: closed
                ? AuditSeverity.Info
                : (string.Equals(ep.PeakBand, "danger", StringComparison.Ordinal) ? AuditSeverity.Danger : AuditSeverity.Warn),
            Target: hostScope
                ? new AuditTarget(AuditTargetKind.Host, options.HostId, options.HostId)
                : new AuditTarget(AuditTargetKind.Server, ep.ServerId ?? "", ep.ServerId),
            ServerId: hostScope ? null : ep.ServerId,
            HostId: options.HostId,
            Summary: summary,
            Meta: meta);
    }

    // Seed the dedup set from what is already in the log, so a restart re-reads its lookback window without
    // re-announcing any of it. Scoped to this API's own local rows: these actions are direct writes, so the
    // local table is the whole record of them.
    private async Task SeedWrittenAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.Subtract(ColdStartLookback).AddHours(-1);

            List<(string Action, string? Meta)> rows = await db.Audit
                .Where(a => a.Ts >= cutoff &&
                            (a.Action == AuditAction.HostThresholdBreach || a.Action == AuditAction.HostThresholdClear))
                .Select(a => new ValueTuple<string, string?>(a.Action, a.Meta))
                .ToListAsync(ct).ConfigureAwait(false);

            foreach ((string action, string? metaJson) in rows)
            {
                string? episodeId = EpisodeIdOf(metaJson);
                if (episodeId is null) continue;
                _written.Add(DedupKey(episodeId, action == AuditAction.HostThresholdClear));
            }

            logger.LogInformation("threshold audit: {Count} episode row(s) already recorded in the last {Hours}h",
                _written.Count, ColdStartLookback.TotalHours);
        }
        catch (Exception ex)
        {
            // An empty seed means the first poll may re-write rows it already wrote. That is a duplicate in
            // the trail, which is bad — but silently recording nothing would be worse, and the next restart
            // recovers. Say so.
            logger.LogWarning(ex, "threshold audit: could not read what has already been recorded; the next poll may duplicate rows");
        }
    }

    private static string? EpisodeIdOf(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(metaJson);
            return doc.RootElement.TryGetProperty("episodeId", out JsonElement id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static string DedupKey(string episodeId, bool closed) => episodeId + (closed ? "|close" : "|open");

    // Read the monitor's episode list. Hand-parsed rather than deserialized into a shared type: the episode
    // shape is the monitor's own daemon-local JSON, not part of the contracts package, so a field it adds
    // must never break this read.
    private static List<EpisodeDto> ParseEpisodes(string json)
    {
        var list = new List<EpisodeDto>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("episodes", out JsonElement episodes) ||
            episodes.ValueKind != JsonValueKind.Array)
            return list;

        foreach (JsonElement e in episodes.EnumerateArray())
        {
            string? id = Str(e, "episodeId");
            if (string.IsNullOrEmpty(id)) continue;

            list.Add(new EpisodeDto(
                EpisodeId: id!,
                RuleKey: Str(e, "ruleKey") ?? "",
                Metric: Str(e, "metric") ?? "",
                Scope: Str(e, "scope") ?? "host",
                Ref: Str(e, "ref"),
                ServerId: Str(e, "serverId"),
                OpenedTs: Num(e, "openedTs") is { } o ? (long)o : 0,
                ClosedTs: Num(e, "closedTs") is { } c ? (long)c : null,
                PeakBand: Str(e, "peakBand") ?? "warn",
                PeakValue: Num(e, "peakValue") ?? 0,
                OpenValue: Num(e, "openValue") ?? 0,
                CloseValue: Num(e, "closeValue"),
                Threshold: Num(e, "threshold") ?? 0,
                CloseReason: Str(e, "closeReason")));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private sealed record EpisodeDto(
        string EpisodeId, string RuleKey, string Metric, string Scope, string? Ref, string? ServerId,
        long OpenedTs, long? ClosedTs, string PeakBand, double PeakValue, double OpenValue,
        double? CloseValue, double Threshold, string? CloseReason);
}
