using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Per-server editable runtime configuration (Tier-1 ops) — <c>GET /servers/{id}/config</c> (read) and
/// <c>PATCH /servers/{id}/config</c> (write one-or-more <c>key=value</c>). The SPA's settings panel renders
/// the form from the GET and saves through the PATCH. Reads are viewer-gated; the write is operator-gated.
/// <para>
/// The engine is the single authority on what is settable: kgsm refuses identity/path/integration keys
/// (<see cref="ServerConfigMapping.IsEditableKey"/> mirrors that protected set). The PATCH validates every
/// key against the editable set BEFORE applying any (a stricter pre-check, never a bypass), then applies them
/// via kgsm-lib's per-key <c>SetInstanceConfigValue</c>; a key the engine still refuses surfaces its real
/// <c>4xx</c> detail. No audit row is written here — kgsm's <c>config-set</c> emits no event today, so an
/// invented <c>config.*</c> action would be a fabrication; this is the honest boundary.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/servers/{id}/config")]
[Authorize(Policy = AuthPolicy.Viewer)] // reads — viewer and up; the PATCH below requires operator
public sealed class ServerConfigController(ServerAggregator aggregator) : ControllerBase
{
    /// <summary>
    /// The instance's editable config (<c>{ serverId, values: { key: value } }</c>). The values map is keyed
    /// by kgsm's snake_case config keys (verbatim, so a PATCH can echo them back) and carries only the
    /// engine-editable keys. Projected from the single-instance info spawn (the <see cref="Instance"/> model
    /// already carries every config field).
    /// <list type="bullet">
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — the editable config.</item>
    /// </list>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        // Honest 404 on an unknown id — the roster is the authority (the command-path discipline).
        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        // `instances info <name>` — the single-instance spawn the Instance model is built from; it carries
        // every config field, so the editable projection needs no second engine call. A null here is the
        // instance vanishing between the roster check and the read — treat as 404, never fabricate a blank.
        Instance? instance = instances.GetInstanceInfo(id);
        if (instance is null)
            return NotFound();

        return Ok(ServerConfigMapping.ToConfig(instance));
    }

    /// <summary>
    /// Apply one-or-more <c>key=value</c> config assignments. Every key is validated against the editable set
    /// first (any protected/unknown key ⇒ <c>400</c>, nothing applied); then each is written via
    /// <c>SetInstanceConfigValue</c>. kgsm <c>config-set</c> is per-key (not transactional), so a mid-apply
    /// engine refusal returns <c>400</c> naming the offending key and reports which keys were already applied
    /// (no feigned atomicity). On full success returns <c>200</c> with the fresh config.
    /// <list type="bullet">
    /// <item><c>400</c> — empty body, a protected/unknown key (pre-check), a bad origin, or an engine refusal.</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — applied: <c>{ applied: [keys], config: { ... } }</c>.</item>
    /// </list>
    /// </summary>
    [HttpPatch]
    [Authorize(Policy = AuthPolicy.Operator)] // mutation — operator and up
    public async Task<IActionResult> Patch(string id, [FromBody] ServerConfigPatch? body, CancellationToken ct)
    {
        if (body?.Values is not { Count: > 0 } values)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "at least one key=value is required");

        if (!TryResolveOrigin(body?.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        // Pre-check: reject any non-editable key BEFORE applying anything (a stricter-than-engine gate, so a
        // partial write can never start on an obviously-protected key). The engine refusal stays the backstop.
        string[] rejected = values.Keys.Where(k => !ServerConfigMapping.IsEditableKey(k)).ToArray();
        if (rejected.Length > 0)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                $"protected or invalid config key(s): {string.Join(", ", rejected)}");

        // actor = the bearer identity (discord:<username>), or null → kgsm's own OS-user fallback.
        string? actor = AuditPrincipal.ActorString(User);

        var applied = new List<string>(values.Count);
        foreach ((string key, string value) in values)
        {
            // A null JSON value is treated as a clear (config-set takes the empty string to clear the key);
            // SetInstanceConfigValue throws on a literal null, so coalesce to "".
            KgsmResult result = instances.SetInstanceConfigValue(id, key, value ?? "", actor, origin);
            if (!result.IsSuccess)
                return Error(StatusCodes.Status400BadRequest, "bad_request",
                    string.IsNullOrWhiteSpace(result.Stderr)
                        ? $"the engine refused '{key}' (exit {result.ExitCode}){AppliedTail(applied)}"
                        : $"{result.Stderr.Trim()}{AppliedTail(applied)}");
            applied.Add(key);
        }

        // Re-read so the client gets the authoritative post-write config without a second round-trip.
        Instance? fresh = instances.GetInstanceInfo(id);
        ServerConfig config = fresh is null
            ? new ServerConfig(id, values) // engine vanished mid-write — echo what we set rather than 500
            : ServerConfigMapping.ToConfig(fresh);

        return Ok(new ServerConfigApplied(applied, config));
    }

    // Whether the instance is in this host's roster (the honest 404 source, like ServersController).
    private async Task<bool> ExistsAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct).ConfigureAwait(false);
        return servers.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    }

    // Append "(applied: a, b)" when a per-key write fails after others succeeded — honest about the
    // non-atomic partial state rather than implying nothing changed.
    private static string AppliedTail(IReadOnlyList<string> applied) =>
        applied.Count == 0 ? "" : $" (already applied: {string.Join(", ", applied)})";

    // Resolve the caller-declared driving surface (ui|assistant|discord|api, default api), the
    // ServersController convention; "system" (autonomous-only) and unknown values are rejected.
    private static bool TryResolveOrigin(string? raw, out string origin)
    {
        origin = raw?.Trim().ToLowerInvariant() is { Length: > 0 } o ? o : AuditOrigin.Api;
        return AuditOrigin.IsCallerDeclarable(origin);
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
