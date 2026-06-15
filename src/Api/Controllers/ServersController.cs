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
    /// One server's record. For M1·b this is the same shape as a list element (full detail —
    /// console, files, players — arrives in later milestones), filtered out of the bulk join so the
    /// list and detail views never diverge.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Server>> GetById(string id, CancellationToken ct)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct);
        Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

        // Unknown id -> 404 with no body; UseStatusCodePages renders the not_found envelope.
        if (server is null)
            return NotFound();

        return server;
    }

    /// <summary>
    /// Issue a lifecycle command (M3, architecture.html §5·d). The body is intent only —
    /// <c>{ "verb": "start"|"stop"|"restart" }</c>, a closed set. The verb is admitted (state guards,
    /// later permissions), a <see cref="Job"/> is created, and the work runs off-request; the
    /// <c>202</c> returns the job immediately and progress arrives on the <c>jobs</c> WS topic.
    /// <list type="bullet">
    /// <item><c>400</c> — unknown/missing verb (the closed set is server-defined).</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>409</c> — an obvious no-op against the real status (start-when-running /
    /// stop-when-stopped), or a command already in flight for this server.</item>
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
                "unknown or missing verb; expected one of: start, stop, restart");

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

        runner.Start(job);
        return StatusCode(StatusCodes.Status202Accepted, new CommandAccepted(job));
    }

    // The frozen { error: { code, message } } envelope (architecture.html §6), rendered via the MVC
    // formatters (camelCase) — same shape UseStatusCodePages emits for the message-less 404 above.
    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
