using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Commands;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The <c>/servers</c> resource (architecture §3). <b>Read</b> (M1·b): <c>GET /servers</c> +
/// <c>GET /servers/{id}</c> return this host's kgsm instances, each the honest join of domain +
/// run-state (kgsm-lib) with per-instance metrics (kgsm-monitor) — see <see cref="Server"/> for the
/// frozen shape and its deliberate divergences. <b>Write</b> (M3): <c>POST /servers/{id}/commands</c>
/// is the first mutation path — gate → 202 + job → track on the <c>jobs</c> WS topic → verify.
/// Per-host: every server carries this host's <c>hostId</c>; the SPA fans out across hosts client-side.
/// </summary>
[ApiController]
[Route("api/v1/servers")]
[Authorize(Policy = AuthPolicy.Viewer)] // reads — viewer and up; the write below requires operator (M4·a)
public sealed class ServersController(
    ServerAggregator aggregator,
    JobRegistry jobs,
    CommandRunner runner) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<Server>> GetAll(CancellationToken ct) =>
        await aggregator.GetServersAsync(ct);

    /// <summary>
    /// One server's detail record. From M6·b this is a <em>superset</em> of the list element: the same
    /// domain ⋈ metrics join <b>plus</b> the <c>network</c> block (required ⋈ firewall-open, §3·g) — the
    /// first place the detail view diverges from the list (the list/stream omit <c>network</c> so they
    /// never trigger a per-poll firewall probe). Fuller detail (console, files, players) arrives later.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Server>> GetById(string id, CancellationToken ct)
    {
        Server? server = await aggregator.GetServerDetailAsync(id, ct);

        // Unknown id -> 404 with no body; UseStatusCodePages renders the not_found envelope.
        if (server is null)
            return NotFound();

        return server;
    }

    /// <summary>
    /// Issue a command (M3 lifecycle + M6·b ports, architecture.html §5·d/§3·g). The body is intent only —
    /// <c>{ "verb": "start"|"stop"|"restart"|"open_ports" }</c>, a closed set; <c>open_ports</c> in
    /// particular carries <strong>no port list</strong> (the server derives the target from the instance's
    /// own ports). The verb is admitted (state guards, permissions), a <see cref="Job"/> is created, and the
    /// work runs off-request; the <c>202</c> returns the job and progress arrives on the <c>jobs</c> WS topic.
    /// <list type="bullet">
    /// <item><c>400</c> — unknown/missing verb (the closed set is server-defined).</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>409</c> — an obvious no-op against the real status (start-when-running /
    /// stop-when-stopped; <c>open_ports</c> is always admissible), or a command already in flight.</item>
    /// <item><c>202</c> — accepted: <c>{ job }</c>.</item>
    /// </list>
    /// </summary>
    [HttpPost("{id}/commands")]
    [Authorize(Policy = AuthPolicy.Operator)] // mutation — operator and up (architecture.html §3·e control set)
    public async Task<IActionResult> PostCommand(string id, [FromBody] CommandRequest? body, CancellationToken ct)
    {
        string? verb = body?.Verb?.Trim().ToLowerInvariant();
        if (!CommandVerb.IsKnown(verb))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown or missing verb; expected one of: start, stop, restart, open_ports");

        // Provenance to stamp on the engine command (M5) so the resulting kgsm event — and the audit row
        // the consumer writes from it — records the driving surface. Caller-declared, validated against
        // the closed client set; absent => "api" (literally true). "system" is reserved for autonomous
        // engine actions and is rejected here. Independent of the actor (the bearer identity below).
        string origin = body?.Origin?.Trim().ToLowerInvariant() is { Length: > 0 } o ? o : AuditOrigin.Api;
        if (!AuditOrigin.IsCallerDeclarable(origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        // Resolve the server + its real observed status (honest 404 on an unknown id).
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct);
        Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        if (server is null)
            return NotFound();

        // Gate: reject the obvious no-ops against the observed status (the engine owns everything subtler).
        string? noop = CommandGate.Inadmissible(verb!, server.Status);
        if (noop is not null)
            return Error(StatusCodes.Status409Conflict, "conflict", noop);

        // Gate: one in-flight command per server (atomic claim).
        string jobId = "job_" + Guid.NewGuid().ToString("N")[..8];
        Job? job = jobs.TryStart(jobId, id, verb!, DateTimeOffset.UtcNow);
        if (job is null)
        {
            Job? existing = jobs.InFlightFor(id);
            return Error(StatusCodes.Status409Conflict, "conflict",
                existing is not null
                    ? $"a command is already in flight for this server (job {existing.Id})"
                    : "a command is already in flight for this server");
        }

        // actor = the bearer identity (discord:<username>), or null → kgsm's own OS-user fallback.
        string? actor = AuditPrincipal.ActorString(User);
        runner.Start(job, actor, origin);
        return StatusCode(StatusCodes.Status202Accepted, new CommandAccepted(job));
    }

    // The frozen { error: { code, message } } envelope (architecture.html §6), rendered via the MVC
    // formatters (camelCase) — same shape UseStatusCodePages emits for the message-less 404 above.
    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
