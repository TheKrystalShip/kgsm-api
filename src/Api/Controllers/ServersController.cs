using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The <c>/servers</c> resource (architecture §3). <b>Read</b> (M1·b): <c>GET /servers</c> +
/// <c>GET /servers/{id}</c> return this host's kgsm instances, each the honest join of domain +
/// run-state (kgsm-lib) with per-instance metrics (kgsm-monitor) — see <see cref="Server"/> for the
/// frozen shape and its deliberate divergences. <b>Write</b> (M3): <c>POST /servers/{id}/commands</c>
/// is the first mutation path — gate → 202 + job → track on the <c>jobs</c> WS topic → verify.
/// <b>Create/delete</b> (M8·b): <c>POST /servers</c> installs a new server from a blueprint and
/// <c>DELETE /servers/{id}</c> uninstalls one — both async, returning a job (architecture.html §3·h).
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
        if (!TryResolveOrigin(body?.Origin, out string origin))
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

    /// <summary>
    /// Install a new server from a blueprint (M8·b, architecture.html §3·h) — the panel's one
    /// <em>create</em> operation. The client may send the whole install form, but only <c>blueprint</c>
    /// (required), <c>name</c>, and <c>origin</c> are honored today; the rest is accepted-but-inert
    /// (§3·h additive-only). The backend assigns the instance id (kgsm <c>generate-id</c>), creates a job,
    /// and runs the install off-request; the <c>202</c> returns the job and progress arrives on the
    /// <c>jobs</c> WS topic. When it completes the new server appears on <c>/servers</c> with a
    /// <c>server.install</c> audit entry (written from the kgsm event echo — no double-write).
    /// <list type="bullet">
    /// <item><c>400</c> — missing <c>blueprint</c>, an unusable blueprint/name (kgsm rejected it), or a bad origin.</item>
    /// <item><c>409</c> — an install is already in flight for the resolved instance name.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>202</c> — accepted: <c>{ job }</c> (the job's serverId is the backend-assigned instance id).</item>
    /// </list>
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicy.Operator)] // create — operator and up (architecture.html §3·e control set)
    public IActionResult Install([FromBody] InstallRequest? body)
    {
        string? blueprint = body?.Blueprint?.Trim();
        if (string.IsNullOrEmpty(blueprint))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "blueprint is required");

        if (!TryResolveOrigin(body?.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        // Resolved per-request (transient, only registered when the engine is provisioned); degrade to a
        // 503 rather than throwing a missing-dependency when kgsm isn't configured on this host.
        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        // The backend assigns the id (§3·h: "the id is the backend's to assign"). generate-id validates
        // the blueprint and the optional custom name; a failure is a client-input problem (unknown blueprint
        // / an unusable or already-taken name) → 400 with kgsm's real detail. The resolved id is unique now,
        // so the subsequent install --name lands the instance verbatim (kgsm echoes a unique name as-is).
        string? customName = string.IsNullOrWhiteSpace(body?.Name) ? null : body!.Name!.Trim();
        KgsmResult gen = instances.GenerateId(blueprint, customName);
        if (!gen.IsSuccess || string.IsNullOrWhiteSpace(gen.Stdout))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                string.IsNullOrWhiteSpace(gen.Stderr)
                    ? $"could not install from blueprint '{blueprint}'"
                    : gen.Stderr.Trim());

        string assignedId = gen.Stdout.Trim();

        // One in-flight command per (resolved) server name. For a generated id this is effectively unique;
        // for a custom name it guards a double-submit of the same install.
        string jobId = "job_" + Guid.NewGuid().ToString("N")[..8];
        Job? job = jobs.TryStart(jobId, assignedId, CommandVerb.Install, DateTimeOffset.UtcNow);
        if (job is null)
            return Error(StatusCodes.Status409Conflict, "conflict",
                $"an install is already in flight for '{assignedId}'");

        string? actor = AuditPrincipal.ActorString(User);
        runner.StartInstall(job, blueprint, actor, origin);
        return StatusCode(StatusCodes.Status202Accepted, new CommandAccepted(job));
    }

    /// <summary>
    /// Uninstall a server (M8·b, architecture.html §3·h — <c>DELETE /servers/{id}</c>). Async like install:
    /// returns <c>202</c> + a job; the instance is removed off-request, a <c>server.removed</c> tombstone is
    /// pushed on the <c>servers</c> topic when it leaves the roster, and a <c>server.uninstall</c> audit
    /// entry lands (from the kgsm event echo). <c>origin</c> rides the <c>?origin=</c> query (a DELETE has no body).
    /// <list type="bullet">
    /// <item><c>400</c> — a bad origin.</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>409</c> — a command is already in flight for this server.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>202</c> — accepted: <c>{ job }</c>.</item>
    /// </list>
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = AuthPolicy.Operator)] // mutation — operator and up
    public async Task<IActionResult> Uninstall(string id, [FromQuery] string? origin, CancellationToken ct)
    {
        if (!TryResolveOrigin(origin, out string resolvedOrigin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        // Honest 404 on an unknown id — the roster is the authority (the command-path discipline).
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct);
        if (!servers.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal)))
            return NotFound();

        string jobId = "job_" + Guid.NewGuid().ToString("N")[..8];
        Job? job = jobs.TryStart(jobId, id, CommandVerb.Uninstall, DateTimeOffset.UtcNow);
        if (job is null)
        {
            Job? existing = jobs.InFlightFor(id);
            return Error(StatusCodes.Status409Conflict, "conflict",
                existing is not null
                    ? $"a command is already in flight for this server (job {existing.Id})"
                    : "a command is already in flight for this server");
        }

        string? actor = AuditPrincipal.ActorString(User);
        runner.StartUninstall(job, actor, resolvedOrigin);
        return StatusCode(StatusCodes.Status202Accepted, new CommandAccepted(job));
    }

    // Resolve the caller-declared driving surface (M5): ui|assistant|discord|api, default api; an unknown
    // value (or "system", reserved for autonomous engine actions) is rejected so the caller can 400. Kept
    // independent of the actor — the two provenance axes never derive from each other.
    private static bool TryResolveOrigin(string? raw, out string origin)
    {
        origin = raw?.Trim().ToLowerInvariant() is { Length: > 0 } o ? o : AuditOrigin.Api;
        return AuditOrigin.IsCallerDeclarable(origin);
    }

    // The frozen { error: { code, message } } envelope (architecture.html §6), rendered via the MVC
    // formatters (camelCase) — same shape UseStatusCodePages emits for the message-less 404 above.
    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
