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
/// The label a server is read by — <c>PUT /servers/{id}/display-name</c> (Operator, rename) and
/// <c>DELETE</c> (Operator, clear, after which the instance reads as its id again). The current label
/// needs no <c>GET</c> of its own: it is <c>name</c> on the <see cref="Server"/> DTO, on the list, the
/// detail and the <c>servers</c> stream alike.
/// </summary>
/// <remarks>
/// <para><b>The id is untouched.</b> Nothing on disk is renamed, no path changes, and every downstream
/// store keyed on the instance id — this API's audit and player history, the monitor's metric series, the
/// watchdog's desired state, the router's port ownership — keeps its history. That is what makes a rename
/// safe on a running server, at any time, as often as somebody likes, and it is why this is a separate
/// route from anything that changes what the server <em>is</em>.</para>
/// <para><b>Engine-owned, not API-local.</b> The label lives in the kgsm instance's own
/// <c>.config.ini</c> as <c>display_name</c> (kgsm-lib's <see cref="InstanceDisplayName"/> owns the
/// normalization), so it travels with the instance, is readable by every surface that speaks kgsm-lib,
/// and dies with the instance on uninstall. This controller holds no state.</para>
/// <para><b>Echo-path audit — no double-write.</b> The write stamps actor+origin onto the engine call and
/// kgsm emits <c>server.renamed</c>, which <c>KgsmAuditConsumer</c> shapes into the
/// <c>server.rename</c> row. A rename typed at the CLI is audited by the identical path, so the trail
/// does not depend on which surface drove it.</para>
/// </remarks>
[ApiController]
[Route("api/v1/servers/{id}/display-name")]
[Authorize(Policy = AuthPolicy.Operator)] // writes only — the label itself is read off the Server DTO
public sealed class ServerDisplayNameController(
    ServerAggregator aggregator,
    InstanceCache cache) : ControllerBase
{
    /// <summary>The longest label this API accepts. Generous — the label is decoration and costs
    /// nothing to store — but bounded, because it is rendered in list rows and channel topics.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Rename the server. The label is stored as <see cref="InstanceDisplayName.Sanitize"/> leaves it
    /// (control characters dropped, surrounding whitespace trimmed — a label is one line by definition),
    /// measured after that and <strong>rejected, never truncated</strong>, when over
    /// <see cref="MaxLength"/>. An empty label is refused — clearing is <c>DELETE</c>.
    /// <list type="bullet">
    /// <item><c>400</c> — missing/empty label, over the cap, or a bad origin.</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>500</c> — the engine refused the write; the response carries its own detail.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — <c>{ serverId, displayName }</c> re-read from the engine.</item>
    /// </list>
    /// </summary>
    [HttpPut]
    public Task<IActionResult> Put(string id, [FromBody] ServerDisplayNameWrite? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.DisplayName))
            return Task.FromResult<IActionResult>(Error(StatusCodes.Status400BadRequest, "bad_request",
                "a display name is required; use DELETE to clear it"));

        // Measure what will actually be stored, not what was typed — stripped control characters and
        // trailing whitespace must not cost the operator characters against the cap.
        string sanitized = InstanceDisplayName.Sanitize(body.DisplayName);
        if (sanitized.Length == 0)
            return Task.FromResult<IActionResult>(Error(StatusCodes.Status400BadRequest, "bad_request",
                "a display name is required; use DELETE to clear it"));

        if (sanitized.Length > MaxLength)
            return Task.FromResult<IActionResult>(Error(StatusCodes.Status400BadRequest, "bad_request",
                $"the display name is {sanitized.Length} characters; the maximum is {MaxLength}"));

        return WriteAsync(id, sanitized, body.Origin, ct);
    }

    /// <summary>
    /// Clear the label, after which the server reads as its id again. Recorded like any other rename —
    /// taking a server's name off it is as visible as giving it one.
    /// <list type="bullet">
    /// <item><c>400</c> — bad origin.</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>500</c> — the engine refused the write.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — <c>{ serverId, displayName }</c>, the label now being the id.</item>
    /// </list>
    /// </summary>
    /// <remarks>The driving surface rides the query string (<c>?origin=ui</c>) as well as an optional
    /// body — a DELETE with a body is awkward for browser clients, the note controller's convention.</remarks>
    [HttpDelete]
    public Task<IActionResult> Delete(
        string id, [FromQuery] string? origin, [FromBody] ServerDisplayNameClear? body, CancellationToken ct)
        => WriteAsync(id, string.Empty, body?.Origin ?? origin, ct);

    // The one write path both verbs take (a clear is the empty label), so the attribution stamp and the
    // cache refresh can never diverge between them.
    private async Task<IActionResult> WriteAsync(string id, string displayName, string? rawOrigin, CancellationToken ct)
    {
        if (!TryResolveOrigin(rawOrigin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        // Honest 404 on an unknown id — the roster is the authority (the command-path discipline). The
        // id is what the route carries and what the engine is asked for: a label is not an identifier
        // here, and resolving one would let two servers sharing a name rename each other.
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct).ConfigureAwait(false);
        if (!servers.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal)))
            return NotFound();

        // actor = the bearer identity, stamped onto the engine call so the server.renamed
        // event — and the server.rename row shaped from it — names who did this.
        string? actor = AuditPrincipal.ActorString(User);

        KgsmResult result = instances.SetDisplayName(id, displayName, actor, origin);
        if (!result.IsSuccess)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorEnvelope(new ErrorBody(
                "engine_refused",
                string.IsNullOrWhiteSpace(result.Stderr)
                    ? $"the engine refused the display name (exit {result.ExitCode})"
                    : result.Stderr.Trim())));
        }

        // The label rides the Server DTO and the `servers` stream; refresh so every open panel converges
        // now rather than on the next reconcile. Best-effort — a refresh already in flight returns false
        // and picks this write up anyway. The engine's own event drives the same refresh for a rename
        // that came from somewhere else, so this only shortens the path for the one it served.
        cache.TryRefresh();

        // Re-read for the authoritative post-write value (what the engine stored, not what we sent) —
        // which is also what makes the cleared case report the id rather than an empty string.
        Instance? fresh = instances.GetInstanceInfo(id);
        return Ok(new ServerDisplayNameView(id, fresh?.DisplayName ?? id));
    }

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
