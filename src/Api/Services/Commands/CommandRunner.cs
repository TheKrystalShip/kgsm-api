using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// Executes an admitted command job off the request path (the lifecycle verbs +
/// <c>install</c>/<c>uninstall</c> + Tier-1 ops <c>update</c>/<c>backup_create</c>/<c>backup_restore</c>).
/// The controller returns <c>202</c> immediately; this runner drives the
/// verb to completion on a background task, streaming each state transition as <c>job.patch</c> on the
/// <c>jobs</c> topic and, on settle, a fresh verify patch — <c>server.patch</c> (run-state for the
/// lifecycle verbs; the newly-created instance for <c>install</c>) or <c>server.removed</c> (the tombstone
/// for <c>uninstall</c>).
/// </summary>
/// <remarks>
/// <para><b>Lifetime (load-bearing):</b> a singleton. The <c>202</c> disposes the request's DI scope, but
/// the kgsm-lib services it uses are <em>transient/process-based</em> (<see cref="ILifecycleService"/>) or a
/// conditionally-registered singleton (<see cref="IFirewallService"/>) — so the background task creates its
/// <b>own</b> scope via <see cref="IServiceScopeFactory"/> and resolves them <em>there</em>. Only value data
/// (the <see cref="Job"/>) crosses the async boundary; never a request-scoped service.</para>
/// <para><b>Audit split (the no-double-write contract):</b> a verb that WORKS is the engine's own event.
/// Every verb here stamps <c>actor</c>+<c>origin</c> onto the engine call and the consumer records that
/// echo (<c>server.start/stop/restart</c>, <c>server.install</c>, <c>server.uninstall</c>) — this runner
/// writes no row for a success, and adding one would duplicate a fact it cannot deduplicate against.</para>
/// <para><b>The outcomes with no echo to ride:</b> a verb that fails or is refused exits non-zero and emits
/// nothing, so it exists in no record on the host. Those the runner records itself, to this API's own
/// journal (<see cref="ApiJournal.CommandOutcomeAsync"/>) — the <c>auth.*</c>/<c>file.write</c> case, a
/// producer recording what it observed and nobody else did. ⚠ Two verbs are excluded because the engine
/// reports their failure itself: <c>update</c> and <c>uninstall</c> emit <c>instance_update_failed</c> /
/// <c>instance_uninstall_failed</c>, which already become rows (see
/// <see cref="EngineRecordsItsOwnFailure"/>).</para>
/// <para><b>Always settles:</b> the verb runs inside try/finally so a started job always reaches a terminal
/// state — releasing the registry's in-flight slot even if the verb throws.</para>
/// </remarks>
public sealed class CommandRunner(
    IServiceScopeFactory scopeFactory,
    StreamHub hub,
    ServerAggregator aggregator,
    JobRegistry registry,
    ApiJournal journal,
    ILogger<CommandRunner> logger) : ICommandExecutor
{
    /// <summary>
    /// Fire-and-forget the job's execution. The job is already registered (queued). <paramref name="actor"/>
    /// (the bearer identity, e.g. <c>discord:haru</c>) and <paramref name="origin"/> (the declared surface)
    /// are stamped onto the engine command, so the kgsm event echo carries provenance.
    /// </summary>
    public void Start(Job job, string? actor = null, string? origin = null, bool force = false) =>
        _ = Task.Run(() => ExecuteAsync(job, actor, origin, blueprint: null, backupName: null, force: force));

    /// <summary>
    /// Run a lifecycle verb and <b>await</b> it — the same execution, settle and verify
    /// <see cref="Start"/> performs, with the completion observable.
    /// </summary>
    /// <remarks>
    /// <see cref="BatchWorker"/> needs this: it holds a concurrency window, and a window can only be
    /// released by something that knows when the work finished. Fire-and-forget is right for a single
    /// command, where the caller is a request that has already answered <c>202</c> and nothing is
    /// counting.
    /// </remarks>
    public Task<int?> RunAsync(Job job, string? actor = null, string? origin = null, bool force = false) =>
        ExecuteAsync(job, actor, origin, blueprint: null, backupName: null, force: force);

    /// <summary>
    /// Fire-and-forget an <c>install</c> job (M8·b — <c>POST /servers</c>). <paramref name="blueprint"/> is
    /// the source blueprint; <paramref name="job"/>'s <see cref="Job.ServerId"/> is the backend-assigned
    /// instance id (the controller resolved it via <c>GenerateId</c> and it is passed as the install
    /// <c>--name</c>, which kgsm honors verbatim for an already-unique name). <paramref name="port"/> is the
    /// optional Game Port override from the install form (validated 1-65535 by the controller; null keeps the
    /// blueprint default). No audit row is written here — kgsm's <c>instance_installed</c> echo carries the
    /// stamped provenance (the lifecycle case).
    /// </summary>
    public void StartInstall(Job job, string blueprint, int? port = null, string? actor = null, string? origin = null, bool? autostart = null, string? library = null)
    {
        // Stamp the blueprint onto the job and broadcast immediately so all connected
        // clients can create a phantom card without waiting for the `running` publish.
        Job withBlueprint = registry.Update(job with { Blueprint = blueprint });
        Publish(withBlueprint);
        _ = Task.Run(() => ExecuteAsync(withBlueprint, actor, origin, blueprint, backupName: null, installPort: port, installAutostart: autostart, installLibrary: library));
    }

    /// <summary>
    /// Fire-and-forget an <c>uninstall</c> job (M8·b — <c>DELETE /servers/{id}</c>). Same echo-path
    /// discipline as the lifecycle verbs; the verify pushes a <c>server.removed</c> tombstone once the
    /// instance leaves the roster.
    /// </summary>
    public void StartUninstall(Job job, string? actor = null, string? origin = null) =>
        _ = Task.Run(() => ExecuteAsync(job, actor, origin, blueprint: null, backupName: null));

    /// <summary>
    /// Fire-and-forget a <c>backup_create</c> job (Tier-1 ops — <c>POST /servers/{id}/backups</c>). Same
    /// echo-path discipline as the lifecycle verbs: kgsm's <c>instance_backup_created</c> echo carries the
    /// stamped provenance, so no audit row is written here. The verify pushes a fresh <c>server.patch</c>.
    /// </summary>
    public void StartBackupCreate(Job job, string? actor = null, string? origin = null) =>
        _ = Task.Run(() => ExecuteAsync(job, actor, origin, blueprint: null, backupName: null));

    /// <summary>
    /// Fire-and-forget a <c>backup_restore</c> job (Tier-1 ops — <c>POST /servers/{id}/backups/restore</c>).
    /// <paramref name="backupName"/> is the snapshot to restore (carried into the closure, the
    /// <see cref="StartInstall"/> pattern for a param-bearing job since <see cref="Job"/> has no slot for it).
    /// Echo-path audited via kgsm's <c>instance_backup_restored</c> → <c>backup.restore</c>; no write here.
    /// </summary>
    public void StartBackupRestore(Job job, string backupName, string? actor = null, string? origin = null) =>
        _ = Task.Run(() => ExecuteAsync(job, actor, origin, blueprint: null, backupName));

    /// <summary>
    /// Fire-and-forget a <c>move</c> job (<c>POST /servers/{id}/move</c>) — the instance's files into
    /// another library. <paramref name="fromLibrary"/> is where the roster said it was when the move was
    /// asked for, carried so a refusal can name both disks; <paramref name="toLibrary"/> is the target.
    /// </summary>
    /// <remarks>
    /// Minutes of copying, and it holds the server's in-flight slot for the whole of it — which is what a
    /// surface renders "moving" from. ⚠ The engine starts the instance once on the new path to confirm it
    /// runs there, so an <c>instance_started</c> and an <c>instance_stopped</c> land partway through; a
    /// card reading run-state alone flickers "running" mid-move, and this job's span is the answer.
    /// Echo-path audited via kgsm's <c>instance_moved</c> → <c>server.move</c>; no write here for a
    /// success.
    /// </remarks>
    public void StartMove(
        Job job, string toLibrary, string? fromLibrary = null, string? actor = null, string? origin = null) =>
        _ = Task.Run(() => ExecuteAsync(job, actor, origin, blueprint: null, backupName: null,
            fromLibrary: fromLibrary, toLibrary: toLibrary));

    /// <summary>
    /// Runs the verb, settles the job and verifies. Returns the engine's exit code when it failed, so a
    /// caller that must categorise the failure — <see cref="BatchWorker"/> telling a capacity refusal
    /// from a fault — reads a number the engine defined rather than matching its prose.
    /// </summary>
    private async Task<int?> ExecuteAsync(Job job, string? actor, string? origin, string? blueprint, string? backupName, int? installPort = null, bool? installAutostart = null, bool force = false, string? installLibrary = null, string? fromLibrary = null, string? toLibrary = null)
    {
        bool ok = false;
        string? error = null;
        int? exitCode = null;
        try
        {
            Publish(registry.Update(job with { State = JobState.Running }));
            logger.LogInformation("command job {JobId} running: {Verb} {ServerId}",
                job.Id, job.Verb, job.ServerId);

            // Own scope — the request scope is long gone; the kgsm-lib services are transient/process-based
            // (lifecycle/install) or conditionally-registered (firewall), so resolve them here, never capture them.
            using IServiceScope scope = scopeFactory.CreateScope();
            (ok, error, exitCode) = job.Verb switch
            {
                CommandVerb.Install => RunInstall(scope, job, blueprint!, installPort, actor, origin, installAutostart, installLibrary),
                CommandVerb.Uninstall => RunUninstall(scope, job, actor, origin),
                // update / backup_* live on IInstanceService, NOT ILifecycleService — they get their own
                // cases so they never fall through to RunLifecycle (whose inner switch would fail them as an
                // "unknown verb"). The lifecycle verbs (start/stop/restart) are the only `_` here.
                CommandVerb.Update => RunUpdate(scope, job, actor, origin),
                CommandVerb.BackupCreate => RunBackupCreate(scope, job, actor, origin),
                CommandVerb.BackupRestore => RunBackupRestore(scope, job, backupName!, actor, origin),
                CommandVerb.Move => RunMove(scope, job, toLibrary!, actor, origin),
                _ => RunLifecycle(scope, job, actor, origin, force),
            };
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
            logger.LogError(ex, "command job {JobId} ({Verb} {ServerId}) threw", job.Id, job.Verb, job.ServerId);
        }
        finally
        {
            Job settled = registry.Update(job with
            {
                State = ok ? JobState.Succeeded : JobState.Failed,
                SettledAt = DateTimeOffset.UtcNow,
                Error = error,
            });
            Publish(settled);

            // The job outcome — Information on success, Warning on a (non-throwing) failure carrying
            // the engine's real error detail. This is the formerly-silent path: a kgsm/watchdog refusal
            // returns a failed KgsmResult (ok=false) rather than throwing, so without this it surfaced
            // only as a jobs-WS patch with nothing in the service log.
            if (ok)
                logger.LogInformation("command job {JobId} succeeded: {Verb} {ServerId}",
                    job.Id, job.Verb, job.ServerId);
            else
            {
                logger.LogWarning("command job {JobId} FAILED: {Verb} {ServerId}: {Error}",
                    job.Id, job.Verb, job.ServerId, error ?? "(no detail)");

                // The one fact on this path that nothing else on the host records. Awaited rather than
                // fired off, so the journal's order is the order the outcomes happened — an append is a
                // single write to a file this process owns.
                await RecordOutcomeAsync(job, actor, origin, error, exitCode, fromLibrary, toLibrary)
                    .ConfigureAwait(false);
            }
        }

        // Verify: re-read authoritative state and reflect it. Best-effort — if the read fails, the next
        // poll/diff cycle reconciles instead (coalesced by the same key). uninstall pushes the
        // server.removed tombstone once the instance is gone; the lifecycle verbs and install verify
        // run-state/roster (server.patch — install surfaces the new server).
        try
        {
            switch (job.Verb)
            {
                case CommandVerb.Uninstall:
                    await PublishServerRemovedAsync(job.ServerId).ConfigureAwait(false);
                    break;
                default:
                    await PublishServerPatchAsync(job.ServerId).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "verify after job {JobId} failed; a later poll will reconcile", job.Id);
        }

        return exitCode;
    }

    // The lifecycle verbs (start/stop/restart). Provenance rides the engine call → the kgsm event echo →
    // the M5 audit row. This runner does NOT write an audit row here (no double-write).
    private (bool ok, string? error, int? exitCode) RunLifecycle(IServiceScope scope, Job job, string? actor, string? origin, bool force = false)
    {
        var lifecycle = scope.ServiceProvider.GetService(typeof(ILifecycleService)) as ILifecycleService;
        if (lifecycle is null)
            return (false, "lifecycle service unavailable (engine not provisioned)", null);

        KgsmResult result = job.Verb switch
        {
            CommandVerb.Start => lifecycle.Start(job.ServerId, actor, origin, force),
            CommandVerb.Stop => lifecycle.Stop(job.ServerId, actor, origin),
            CommandVerb.Restart => lifecycle.Restart(job.ServerId, actor, origin),
            _ => new KgsmResult(1, "", $"unknown verb '{job.Verb}'"),
        };
        return result.IsSuccess ? (true, null, null) : (false, Detail(result), result.ExitCode);
    }

    // install (M8·b) — create a new instance from `blueprint`. The job's ServerId is the backend-assigned
    // id the controller resolved via GenerateId; passing it as the install `--name` lands the instance
    // exactly there (kgsm's generate-id echoes an already-unique name verbatim), so the verify target and
    // the instance_installed event's name match. version stays null — reserved/inert per §3·h. NO audit
    // row here: kgsm emits instance_installed → KgsmAuditConsumer writes the server.install echo with the
    // stamped provenance.
    private (bool ok, string? error, int? exitCode) RunInstall(
        IServiceScope scope, Job job, string blueprint, int? port, string? actor, string? origin, bool? autostart = null,
        string? library = null)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned", null);

        // port (the install form's Game Port) overrides the blueprint's primary port when supplied; null
        // keeps the blueprint default. autostart requests a one-shot start after install completes.
        // library names which registered root to place it in; null leaves the engine to resolve its own
        // default, which is what a host with a single library wants and what every caller predating the
        // choice sends. version stays null (the SPA disables version selection).
        KgsmResult result = instances.Install(blueprint, library: library, version: null, name: job.ServerId, actor: actor, origin: origin, port: port, start: autostart);
        return result.IsSuccess ? (true, null, null) : (false, Detail(result), result.ExitCode);
    }

    // uninstall (M8·b) — remove the instance. Same echo-path discipline: kgsm emits instance_uninstalled →
    // KgsmAuditConsumer writes the server.uninstall row. No audit write here.
    private (bool ok, string? error, int? exitCode) RunUninstall(IServiceScope scope, Job job, string? actor, string? origin)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned", null);

        // Best-effort pre-stop (Phase 0 — delete hardening): stop a running instance before removing files so
        // we never orphan a live process. A non-zero result is expected when it's already stopped — log at
        // Debug and proceed; the stop must never block the uninstall. Skipped if lifecycle isn't provisioned.
        var lifecycle = scope.ServiceProvider.GetService(typeof(ILifecycleService)) as ILifecycleService;
        if (lifecycle is not null)
        {
            KgsmResult stop = lifecycle.Stop(job.ServerId, actor, origin);
            if (!stop.IsSuccess)
                logger.LogDebug("pre-uninstall stop of {ServerId} was non-zero (likely already stopped): {Detail}",
                    job.ServerId, Detail(stop));
        }

        KgsmResult result = instances.Uninstall(job.ServerId, actor, origin);
        return result.IsSuccess ? (true, null, null) : (false, Detail(result), result.ExitCode);
    }

    // update (Tier-1 ops) — update the instance to the latest version. On IInstanceService (NOT lifecycle),
    // so it needs its own case. Provenance rides the engine call → kgsm's instance_version_updated echo →
    // the server.update audit row (KgsmAuditConsumer). NO audit row here (the echo path, like install). The
    // controller already 409s an update-on-running synchronously; a subtler engine refusal lands as a failed
    // job + the real stderr.
    private (bool ok, string? error, int? exitCode) RunUpdate(IServiceScope scope, Job job, string? actor, string? origin)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned", null);

        KgsmResult result = instances.Update(job.ServerId, actor, origin);
        // Nothing to clear here: whether an update is available is the engine's own answer, read from
        // the record beside the instance, and an applied update makes it false at the source. The
        // instance_version_updated echo re-reads the roster (KgsmAuditConsumer) for an update driven
        // from anywhere — this API, the CLI, the assistant — so one path keeps it true rather than
        // this one holding a local copy it has to remember to void.
        return result.IsSuccess ? (true, null, null) : (false, Detail(result), result.ExitCode);
    }

    // backup_create (Tier-1 ops) — snapshot the instance. Echo-path discipline: kgsm emits
    // instance_backup_created → KgsmAuditConsumer writes the backup.create row with the stamped provenance.
    // No audit write here.
    private (bool ok, string? error, int? exitCode) RunBackupCreate(IServiceScope scope, Job job, string? actor, string? origin)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned", null);

        KgsmResult result = instances.CreateBackup(job.ServerId, actor, origin);
        return result.IsSuccess ? (true, null, null) : (false, Detail(result), result.ExitCode);
    }

    // backup_restore (Tier-1 ops) — restore from a named snapshot (backupName threaded through the closure;
    // Job has no slot for it). Echo-path: kgsm emits instance_backup_restored → the backup.restore row. No
    // audit write here. An unknown backup name surfaces as kgsm's real stderr on a failed job.
    private (bool ok, string? error, int? exitCode) RunBackupRestore(
        IServiceScope scope, Job job, string backupName, string? actor, string? origin)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned", null);

        KgsmResult result = instances.RestoreBackup(job.ServerId, backupName, actor, origin);
        return result.IsSuccess ? (true, null, null) : (false, Detail(result), result.ExitCode);
    }

    // move — the instance's files into another library. On IInstanceService, so it needs its own case.
    // The controller already refuses the answerable cases synchronously (a library this host does not
    // hold, one whose root is away, the instance's current library, a running instance); a subtler
    // refusal — the target has less free space than the instance occupies, or somebody started the
    // server between the check and the run — is the engine's to make, and lands as a failed job carrying
    // its words. Echo-path audited via instance_moved → server.move; no audit row here.
    //
    // skipSpaceCheck is deliberately NOT exposed: it is the escape hatch for an operator who has looked
    // at the disk, and a panel button that overrides a measurement is how a drive gets filled.
    private (bool ok, string? error, int? exitCode) RunMove(
        IServiceScope scope, Job job, string library, string? actor, string? origin)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned", null);

        KgsmResult result = instances.Move(job.ServerId, library, skipSpaceCheck: false, actor, origin);
        return result.IsSuccess ? (true, null, null) : (false, Detail(result), result.ExitCode);
    }

    /// <summary>
    /// Records a command that ended without doing the thing, to this API's own journal.
    /// </summary>
    /// <remarks>
    /// Best-effort in both directions: <see cref="ApiJournal"/> swallows a write it could not make, and
    /// this swallows anything above it. The command has already ended by the time this runs, and turning
    /// a reported outcome into an exception because writing it down failed would trade a missing line for
    /// a broken command path.
    /// </remarks>
    private async Task RecordOutcomeAsync(
        Job job, string? actor, string? origin, string? error, int? exitCode,
        string? fromLibrary = null, string? toLibrary = null)
    {
        if (EngineRecordsItsOwnFailure(job.Verb)) return;

        try
        {
            await journal.CommandOutcomeAsync(
                OutcomeEvent(exitCode), job.ServerId, job.Verb, job.Id, job.BatchId,
                error, exitCode, actor, AuditMapping.NormalizeOrigin(origin),
                fromLibrary, toLibrary).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "recording the outcome of job {JobId} failed", job.Id);
        }
    }

    /// <summary>
    /// Which fact a non-success is: the node was full, or something went wrong.
    /// </summary>
    /// <remarks>
    /// Keyed on the exit code kgsm defines (<see cref="EngineExit.InsufficientMemory"/>), never on its
    /// message, which is prose written for a person and free to be reworded. A capacity refusal filed as
    /// a failure reads as a fault in the instance — nothing is wrong with it, the node was full — and
    /// invites a retry certain to be refused identically until something else stops. The same
    /// distinction <see cref="BatchWorker"/> makes for a batch member, off the same constant.
    /// </remarks>
    internal static string OutcomeEvent(int? exitCode) =>
        exitCode == EngineExit.InsufficientMemory
            ? ApiJournal.CommandRefusedEvent
            : ApiJournal.CommandFailedEvent;

    /// <summary>
    /// Whether the engine already emits an event for this verb failing.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The no-double-write line.</b> kgsm emits <c>instance_update_failed</c> and
    /// <c>instance_uninstall_failed</c>, and both are mapped to a row carrying the provenance the command
    /// stamped onto the call — so a row written here for the same command would be a second, undedupable
    /// record of one fact. Every other verb fails silently as far as the journals are concerned: a start,
    /// a stop, a restart, an install and both backup verbs exit non-zero and emit nothing.
    /// </remarks>
    internal static bool EngineRecordsItsOwnFailure(string verb) =>
        verb is CommandVerb.Update or CommandVerb.Uninstall;

    // kgsm's real failure detail (stderr), or a bare exit code when it said nothing — never a fabricated
    // success message.
    private static string Detail(KgsmResult r) =>
        string.IsNullOrWhiteSpace(r.Stderr) ? $"exit {r.ExitCode}" : r.Stderr.Trim();

    private void Publish(Job job) =>
        hub.Publish(StreamProtocol.JobsTopic, StreamProtocol.JobEntityKey(job.Id),
            new StreamMessage(StreamProtocol.JobsTopic, StreamProtocol.JobPatch, job));

    private async Task PublishServerPatchAsync(string serverId)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(CancellationToken.None).ConfigureAwait(false);
        Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.Ordinal));
        if (server is not null)
            hub.Publish(StreamProtocol.ServersTopic, StreamProtocol.ServerEntityKey(serverId),
                new StreamMessage(StreamProtocol.ServersTopic, StreamProtocol.ServerPatch, server));
    }

    // The uninstall verify: if the instance has left the roster, push the server.removed tombstone; if it
    // somehow survived (the uninstall failed), push its fresh server.patch instead — honest either way, and
    // both share the ServerEntityKey coalesce slot so the newer correctly supersedes a queued older frame.
    private async Task PublishServerRemovedAsync(string serverId)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(CancellationToken.None).ConfigureAwait(false);
        Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.Ordinal));
        if (server is not null)
        {
            hub.Publish(StreamProtocol.ServersTopic, StreamProtocol.ServerEntityKey(serverId),
                new StreamMessage(StreamProtocol.ServersTopic, StreamProtocol.ServerPatch, server));
            return;
        }
        hub.Publish(StreamProtocol.ServersTopic, StreamProtocol.ServerEntityKey(serverId),
            new StreamMessage(StreamProtocol.ServersTopic, StreamProtocol.ServerRemoved, new ServerRemoved(serverId)));
    }
}
