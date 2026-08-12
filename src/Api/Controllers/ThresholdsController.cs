using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The host's metric-threshold policy — <c>GET|PUT|DELETE /hosts/{id}/thresholds</c>, the rules kgsm-monitor
/// watches this machine's numbers against and raises the panel's resource alerts from.
/// </summary>
/// <remarks>
/// <para><b>This API is an editor, not the owner.</b> The policy lives in kgsm-monitor, because the thing
/// that evaluates the rules is the thing that has to answer what it is evaluating. Every verb here relays to
/// the monitor and reports what the monitor said — the same posture as the metrics-history and stats relays.
/// Nothing about a policy is stored on this side, so the panel and the daemon cannot drift apart.</para>
/// <para><b>Validation is not restated here.</b> A refused policy comes back with the monitor's own reason
/// and the rule at fault. Re-implementing those checks would mean maintaining a second copy of rules this
/// API deliberately does not own, and the two copies would eventually disagree about what is valid.</para>
/// <para><b>Gates.</b> Read at <b>operator</b>, matching the rest of the host's configuration surface. Write
/// at <b>admin</b>: a threshold decides what the whole fleet alerts on, and getting it wrong either buries
/// people in noise or silences a real problem.</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{id}/thresholds")]
[Authorize(Policy = AuthPolicy.Operator)]
public sealed class ThresholdsController(
    MonitorClient monitor,
    ApiJournal journal,
    ApiOptions options) : ControllerBase
{
    /// <summary><c>GET /hosts/{id}/thresholds</c> → the monitor's policy document, verbatim.</summary>
    [HttpGet]
    public async Task<IActionResult> GetThresholds(string id, CancellationToken ct)
    {
        if (!IsThisHost(id)) return NotFound();

        string? json = await monitor.GetThresholdsJsonAsync(ct);
        if (json is null)
            return MonitorUnavailable("The threshold policy could not be read.");

        return Content(json, "application/json");
    }

    /// <summary>
    /// <c>PUT /hosts/{id}/thresholds</c> — apply a whole rule set. The body is relayed to the monitor
    /// unchanged, and its answer is returned as-is on success or as the frozen error envelope on refusal.
    /// </summary>
    [HttpPut]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> PutThresholds(string id, CancellationToken ct)
    {
        if (!IsThisHost(id)) return NotFound();

        string body = await ReadBodyAsync(ct);
        LeafRelayResponse relay = await monitor.PutThresholdsAsync(body, ct);
        return await CompleteAsync(relay, "configured", ChangedKeys(body), ct);
    }

    /// <summary>
    /// <c>DELETE /hosts/{id}/thresholds</c> — drop this host's policy and return to the monitor's built-in
    /// defaults. A separate verb rather than a PUT of the defaults, so "this host is on the defaults" stays
    /// a fact about the host rather than a rule set that happens to match today's baseline.
    /// </summary>
    [HttpDelete]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> ResetThresholds(string id, CancellationToken ct)
    {
        if (!IsThisHost(id)) return NotFound();

        LeafRelayResponse relay = await monitor.DeleteThresholdsAsync(ct);
        return await CompleteAsync(relay, "reset", ["(defaults)"], ct);
    }

    // Turn the monitor's answer into this API's, and record what was attempted. The audit row is written for
    // BOTH outcomes: a refused policy change is a thing somebody tried to do to this host, and the refusal
    // exists nowhere else once the response is gone.
    private async Task<IActionResult> CompleteAsync(
        LeafRelayResponse relay, string verb, IReadOnlyList<string> keys, CancellationToken ct)
    {
        if (!relay.Reached)
        {
            await AuditAsync(verb, "unreachable", keys, ct);
            return MonitorUnavailable("The threshold policy could not be applied.");
        }

        if (relay.IsSuccess)
        {
            await AuditAsync(verb, "applied", keys, ct);
            return Content(relay.Body ?? "{}", "application/json");
        }

        await AuditAsync(verb, "rejected", keys, ct);

        // The monitor's refusal, in this API's error envelope. Its message names the rule at fault, which is
        // the part an operator needs; a status it chose that this API does not use is normalised to 400,
        // because from the caller's side a refused body is a refused body.
        string message = ExtractError(relay.Body) ?? "The monitor refused the threshold policy.";
        int status = relay.StatusCode is >= 400 and < 500 ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway;
        return StatusCode(status, new ErrorEnvelope(new ErrorBody(
            status == StatusCodes.Status400BadRequest ? "bad_request" : "leaf_error", message)));
    }

    // A threshold policy change is a configuration change on the monitor leaf, and is recorded as one:
    // the rule keys that moved, and how the monitor answered. The values themselves live in the
    // monitor's own configuration, which is where a reader should go for what a rule is set to now.
    private Task AuditAsync(
        string verb, string outcome, IReadOnlyList<string> keys, CancellationToken ct) =>
        journal.ServiceConfigAsync(
            leaf: "monitor",
            displayName: "Monitor",
            keys: keys,
            outcome: outcome,
            actor: AuditPrincipal.ActorString(User) ?? "",
            origin: AuditOrigin.Api,
            ct: ct);

    private bool IsThisHost(string id) =>
        string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase);

    private IActionResult MonitorUnavailable(string what) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorEnvelope(new ErrorBody(
            "metrics_unavailable", $"{what} The monitor is not connected on this host, or did not answer.")));

    private async Task<string> ReadBodyAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        return await reader.ReadToEndAsync(ct);
    }

    // The rule keys named in a request body, for the audit row's meta. Best-effort by design: this is a
    // description of what was attempted, and a body the monitor is about to reject may not parse at all.
    private static IReadOnlyList<string> ChangedKeys(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("rules", out JsonElement rules) ||
                rules.ValueKind != JsonValueKind.Array)
                return ["(unparsed)"];

            var keys = new List<string>();
            foreach (JsonElement rule in rules.EnumerateArray())
                if (rule.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String)
                    keys.Add(key.GetString()!);

            return keys.Count > 0 ? keys : ["(none)"];
        }
        catch (JsonException)
        {
            return ["(unparsed)"];
        }
    }

    // The monitor's error message out of its own envelope, so the operator reads why rather than that
    // something went wrong.
    private static string? ExtractError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out JsonElement error) &&
                   error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }
}
