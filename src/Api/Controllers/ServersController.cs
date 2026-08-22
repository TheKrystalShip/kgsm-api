using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Availability;
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
    CommandRunner runner,
    BatchStore batches,
    BatchWorker worker,
    StreamHub hub,
    ApiOptions options,
    ILogger<ServersController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Server>>> GetAll(CancellationToken ct)
    {
        // Distinguish a FAILED engine read from a genuinely empty roster: serving a transient failure as
        // 200 [] is what wiped the SPA's server list (it replaces its list from any 200). On a failed
        // read return 503 — the client keeps its last-known list and shows a soft "couldn't refresh"
        // rather than dropping every server. A successful read (even of zero servers) is a normal 200.
        ServersRead read = await aggregator.GetServersReadAsync(ct);
        if (!read.EngineRead)
            return Error(StatusCodes.Status503ServiceUnavailable, "engine_unavailable",
                "the game-server engine could not be read; last-known state is preserved");
        return Ok(read.Servers);
    }

    /// <summary>
    /// <c>GET /servers/availability?window=7d</c> — how much of the time each server was up
    /// <em>when something wanted it up</em>, folded from the engine's lifecycle events.
    /// </summary>
    /// <remarks>
    /// <para>A deliberate stop is not downtime, so it lowers the denominator rather than the score; a
    /// server nothing wanted running during the window has no figure at all
    /// (<c>availability: null</c>) rather than a flattering 100%. The report states what the journal
    /// could actually cover — see <see cref="AvailabilityReport.CoverageFrom"/> — so a window reaching
    /// past retention reads as the partial answer it is.</para>
    /// <para>A literal path segment, so it is matched ahead of <c>{id}</c>. An instance actually named
    /// <c>availability</c> would be shadowed here and is still reachable everywhere else; kgsm has no
    /// such instance and the alternative is a second route prefix for one read.</para>
    /// </remarks>
    [HttpGet("availability")]
    public async Task<ActionResult<AvailabilityReport>> GetAvailability(
        [FromQuery] string? window, CancellationToken ct)
    {
        ServersRead read = await aggregator.GetServersReadAsync(ct);
        if (!read.EngineRead)
            return Error(StatusCodes.Status503ServiceUnavailable, "engine_unavailable",
                "the game-server engine could not be read; availability cannot be scoped to a roster");

        // Resolved from the request scope for the same reason AuditController does it: kgsm-lib's
        // services exist only where the engine is provisioned, and a constructor parameter would turn
        // "no engine history" into a 500 on the very endpoint that reports there is none.
        IEventJournalHistory? journal = HttpContext.RequestServices.GetService<IEventJournalHistory>();

        string label = string.IsNullOrWhiteSpace(window) ? "7d" : window.Trim().ToLowerInvariant();
        return await AvailabilityQueries.BuildAsync(
            journal,
            [.. read.Servers.Select(s => s.Id)],
            AvailabilityQueries.ParseWindow(window),
            label,
            DateTimeOffset.UtcNow,
            ct);
    }

    /// <summary>
    /// One server's detail record. From M6·b this is a <em>superset</em> of the list element: the same
    /// domain ⋈ metrics join <b>plus</b> the <c>network</c> block (required ⋈ firewall-open, §3·g) — the
    /// first place the detail view diverges from the list (the list/stream omit <c>network</c> so they
    /// never trigger a per-poll firewall probe). Fuller detail (console, files, players) arrives later.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Server>> GetById(string id, CancellationToken ct)
    {
        // BaseUrl() lets the detail join build absolute, self-hosted cover/hero URLs for the SPA's hero.
        Server? server = await aggregator.GetServerDetailAsync(id, BaseUrl(), ct);

        // Unknown id -> 404 with no body; UseStatusCodePages renders the not_found envelope.
        if (server is null)
            return NotFound();

        return server;
    }

    /// <summary>
    /// Issue a command (architecture.html §5·d). The body is intent only —
    /// <c>{ "verb": "start"|"stop"|"restart"|"update" }</c>, a closed set. The verb is admitted (state
    /// guards, permissions), a <see cref="Job"/> is created, and the work runs off-request; the
    /// <c>202</c> returns the job and progress arrives on the <c>jobs</c> WS topic.
    /// <list type="bullet">
    /// <item><c>400</c> — unknown/missing verb (the closed set is server-defined).</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>409</c> — an obvious no-op/illegal transition against the real status (start-when-running /
    /// stop-when-stopped / update-when-running), or a command already in flight.</item>
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
                "unknown or missing verb; expected one of: start, stop, restart, update");

        // Provenance to stamp on the engine command (M5) so the resulting kgsm event — and the audit row
        // the consumer writes from it — records the driving surface. Caller-declared, validated against
        // the closed client set; absent => "api" (literally true). "system" is reserved for autonomous
        // engine actions and is rejected here. Independent of the actor (the bearer identity below).
        if (!TryResolveOrigin(body?.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        // force overrides the engine's node-capacity check, and only start has one to override. Asking
        // for it on another verb is refused rather than dropped: silently ignoring a safety override a
        // caller deliberately set would leave them believing they had bypassed something. `false` is the
        // default and passes on every verb, so a client that always sends the field is unaffected.
        bool force = body?.Force == true;
        if (force && verb != CommandVerb.Start)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                $"force applies to start only; '{verb}' has no capacity check to override");

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
        // Log the accepted command so the action is visible in the service log even before the engine
        // echo lands an audit row — the job outcome (success/failure) is logged by the CommandRunner.
        logger.LogInformation(
            "command accepted: {Verb} {ServerId} job={JobId} (actor={Actor}, origin={Origin}, force={Force})",
            verb, id, job.Id, actor ?? "(none)", origin, force);
        runner.Start(job, actor, origin, force);
        return StatusCode(StatusCodes.Status202Accepted, new CommandAccepted(job));
    }

    /// <summary>
    /// Run one verb against a set of this host's servers — the batch path.
    /// <para>
    /// The batch is a <b>dispatcher, not a second command vocabulary</b>: every admitted member becomes
    /// an ordinary <see cref="Job"/> on the ordinary runner, with its own engine invocation and its own
    /// audit row, so nothing downstream — audit, the reactor, the bot, push — learns a new event shape.
    /// What the batch adds is the record tying the members together, the pacing between them, and the
    /// fact that it outlives the client: the work is owned by this node from here on, and completes
    /// whether or not anyone is still watching.
    /// </para>
    /// <para>
    /// The response states <b>both halves</b> — what was taken and what was refused, with the reason
    /// each refusal would have carried as a single command's <c>409</c>. A caller asked about a set, so
    /// it is answered about the set rather than discovering the refusals one member at a time.
    /// </para>
    /// <list type="bullet">
    /// <item><c>400</c> — unknown/missing verb, an empty server list, or a bad origin.</item>
    /// <item><c>202</c> — accepted: <c>{ batchId, runId, verb, admitted, refused }</c>. A batch where
    /// every member was refused is still a <c>202</c>: the request was well-formed and the answer is
    /// the refusal list, which is information, not an error.</item>
    /// </list>
    /// </summary>
    [HttpPost("commands")]
    [Authorize(Policy = AuthPolicy.Operator)] // mutation — operator and up, the single-command gate
    public async Task<IActionResult> PostBatchCommand([FromBody] BatchRequest? body, CancellationToken ct)
    {
        string? verb = body?.Verb?.Trim().ToLowerInvariant();
        if (!CommandVerb.IsKnown(verb))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown or missing verb; expected one of: start, stop, restart, update");

        if (!TryResolveOrigin(body?.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        // Refused rather than dropped on a verb with no capacity check, exactly as the single-command
        // path refuses it: silently ignoring a safety override somebody deliberately set would leave
        // them believing they had bypassed something.
        bool force = body?.Force == true;
        if (force && verb != CommandVerb.Start)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                $"force applies to start only; '{verb}' has no capacity check to override");

        // De-duplicated, order preserved: the position a member is given is the order the caller asked
        // in, and asking for the same server twice is one member, not two commands racing each other.
        List<string> requested = (body?.ServerIds ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requested.Count == 0)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "serverIds must name at least one server");

        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct);
        string? actor = AuditPrincipal.ActorString(User);
        string batchId = "batch_" + Guid.NewGuid().ToString("N")[..12];
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var admitted = new List<string>();
        var refused = new List<BatchRefusal>();
        var members = new List<BatchMemberEntity>();
        var queuedJobs = new List<Job>();

        foreach (string id in requested)
        {
            Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (server is null)
            {
                // A server this node does not own is refused, never forwarded. Answering for another
                // node's instance is worse than not answering.
                Refuse(id, "no such server on this host");
                continue;
            }

            string? noop = CommandGate.Inadmissible(verb!, server.Status);
            if (noop is not null) { Refuse(id, noop); continue; }

            // Every admitted member gets its job HERE, queued, rather than when the worker reaches it.
            // A job is how the whole system already talks about pending work — it rides the jobs topic
            // and the server's own activeJob — so creating them up front is what makes eight servers
            // waiting look like eight servers waiting instead of eight servers with nothing happening.
            string jobId = "job_" + Guid.NewGuid().ToString("N")[..8];
            Job? job = jobs.TryStart(jobId, id, verb!, now);
            if (job is null) { Refuse(id, CommandGate.Busy(jobs.InFlightFor(id))); continue; }

            int position = admitted.Count + 1;
            Job queued = jobs.Update(job with { BatchId = batchId, QueuedPosition = position });
            queuedJobs.Add(queued);
            admitted.Add(id);
            members.Add(new BatchMemberEntity
            {
                BatchId = batchId,
                ServerId = id,
                State = BatchMemberState.Pending,
                JobId = jobId,
                Position = position,
            });
        }

        // A batch with nothing to do is settled on arrival — there is no member that could still move,
        // and marking it active would leave the worker a row it can never close.
        var batch = new BatchEntity
        {
            Id = batchId,
            RunId = string.IsNullOrWhiteSpace(body?.RunId) ? null : body!.RunId!.Trim(),
            Verb = verb!,
            State = admitted.Count > 0 ? BatchState.Active : BatchState.Settled,
            Actor = actor,
            Origin = origin,
            Force = force,
            CreatedAt = now,
            SettledAt = admitted.Count > 0 ? null : now,
        };
        await batches.CreateAsync(batch, members, ct);

        // Publish the queued jobs only after the batch is durable. A client told about a job the store
        // does not yet hold could read the batch back and not find it.
        foreach (Job q in queuedJobs) PublishJob(q);
        await worker.PublishBatchAsync(batchId, ct);
        if (admitted.Count > 0) worker.Signal();

        logger.LogInformation(
            "batch accepted: {Verb} × {Admitted} admitted, {Refused} refused (batch={BatchId}, run={RunId}, actor={Actor}, origin={Origin}, force={Force})",
            verb, admitted.Count, refused.Count, batchId, batch.RunId ?? "(none)", actor ?? "(none)", origin, force);

        return StatusCode(StatusCodes.Status202Accepted,
            new BatchAccepted(batchId, batch.RunId, verb!, admitted, refused));

        void Refuse(string id, string reason)
        {
            refused.Add(new BatchRefusal(id, reason));
            members.Add(new BatchMemberEntity
            {
                BatchId = batchId,
                ServerId = id,
                State = BatchMemberState.Refused,
                Error = reason,
                SettledAt = now,
            });
        }
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

        // The optional Game Port override (now honored, §3·h additive→active): a TCP/UDP port is 1-65535.
        // Reject an out-of-range value up front rather than letting kgsm fail the install mid-flight.
        int? port = body?.Port;
        if (port is < 1 or > 65535)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "port must be between 1 and 65535");

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
        runner.StartInstall(job, blueprint, port, actor, origin, autostart: body?.Autostart);
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

    // A batch's queued jobs are announced the same way the runner announces every other transition, so
    // a client learns about waiting work through the channel it already follows.
    private void PublishJob(Job job) =>
        hub.Publish(StreamProtocol.JobsTopic, StreamProtocol.JobEntityKey(job.Id),
            new StreamMessage(StreamProtocol.JobsTopic, StreamProtocol.JobPatch, job));

    // The absolute origin the self-hosted cover/hero serving URLs are built from — the configured public
    // base (reverse-proxy deployments) or the live request's scheme+host. Mirrors LibraryController.BaseUrl().
    private string BaseUrl() =>
        !string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? options.PublicBaseUrl
            : $"{Request.Scheme}://{Request.Host}";
}
