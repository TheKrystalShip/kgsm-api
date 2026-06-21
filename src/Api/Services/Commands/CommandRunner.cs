using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// Executes an admitted command job off the request path (M3 lifecycle + M6·b <c>open_ports</c> + M8·b
/// <c>install</c>/<c>uninstall</c> + Tier-1 ops <c>update</c>/<c>backup_create</c>/<c>backup_restore</c>).
/// The controller returns <c>202</c> immediately; this runner drives the
/// verb to completion on a background task, streaming each state transition as <c>job.patch</c> on the
/// <c>jobs</c> topic and, on settle, a fresh verify patch — <c>server.patch</c> (run-state for the
/// lifecycle verbs; the newly-created instance for <c>install</c>), <c>server.removed</c> (the tombstone
/// for <c>uninstall</c>), or <c>network.patch</c> on <c>servers/{id}/network</c> (the firewall re-probe,
/// <c>open_ports</c>).
/// </summary>
/// <remarks>
/// <para><b>Lifetime (load-bearing):</b> a singleton. The <c>202</c> disposes the request's DI scope, but
/// the kgsm-lib services it uses are <em>transient/process-based</em> (<see cref="ILifecycleService"/>) or a
/// conditionally-registered singleton (<see cref="IFirewallService"/>) — so the background task creates its
/// <b>own</b> scope via <see cref="IServiceScopeFactory"/> and resolves them <em>there</em>. Only value data
/// (the <see cref="Job"/>) crosses the async boundary; never a request-scoped service.</para>
/// <para><b>Audit split (the no-double-write contract):</b> the lifecycle verbs <em>and</em>
/// <c>install</c>/<c>uninstall</c> stamp <c>actor</c>+<c>origin</c> onto the engine call and the M5 consumer
/// records the event echo (<c>server.start/stop/restart</c>, <c>server.install</c>, <c>server.uninstall</c>)
/// — this runner writes NO audit row for them. <c>open_ports</c> goes through <see cref="IFirewallService"/>,
/// which emits no event, so it is the <c>auth.*</c> case: this runner writes the <c>network.ports.open</c>
/// row <b>directly</b>. Disjoint by construction — kgsm never echoes an api firewall call.</para>
/// <para><b>Always settles:</b> the verb runs inside try/finally so a started job always reaches a terminal
/// state — releasing the registry's in-flight slot even if the verb throws.</para>
/// </remarks>
public sealed class CommandRunner(
    IServiceScopeFactory scopeFactory,
    StreamHub hub,
    ServerAggregator aggregator,
    JobRegistry registry,
    AuditService audit,
    ApiOptions options,
    ILogger<CommandRunner> logger)
{
    // The open_ports firewall mutation can be slower than a detail-view probe (ufw serialized behind a
    // global lock), so it gets a more generous bound than NetworkAggregator's 2s read probe.
    private static readonly TimeSpan OpenPortsTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fire-and-forget the job's execution. The job is already registered (queued). <paramref name="actor"/>
    /// (the bearer identity, e.g. <c>discord:haru</c>) and <paramref name="origin"/> (the declared surface)
    /// are stamped onto a lifecycle engine command (so the kgsm event echo carries provenance) or, for
    /// <c>open_ports</c>, onto the direct audit row this runner writes.
    /// </summary>
    public void Start(Job job, string? actor = null, string? origin = null) =>
        _ = Task.Run(() => ExecuteAsync(job, actor, origin, blueprint: null, backupName: null));

    /// <summary>
    /// Fire-and-forget an <c>install</c> job (M8·b — <c>POST /servers</c>). <paramref name="blueprint"/> is
    /// the source blueprint; <paramref name="job"/>'s <see cref="Job.ServerId"/> is the backend-assigned
    /// instance id (the controller resolved it via <c>GenerateId</c> and it is passed as the install
    /// <c>--name</c>, which kgsm honors verbatim for an already-unique name). No audit row is written here —
    /// kgsm's <c>instance_installed</c> echo carries the stamped provenance (the lifecycle case).
    /// </summary>
    public void StartInstall(Job job, string blueprint, string? actor = null, string? origin = null) =>
        _ = Task.Run(() => ExecuteAsync(job, actor, origin, blueprint, backupName: null));

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

    private async Task ExecuteAsync(Job job, string? actor, string? origin, string? blueprint, string? backupName)
    {
        bool ok = false;
        string? error = null;
        try
        {
            Publish(registry.Update(job with { State = JobState.Running }));

            // Own scope — the request scope is long gone; the kgsm-lib services are transient/process-based
            // (lifecycle/install) or conditionally-registered (firewall), so resolve them here, never capture them.
            using IServiceScope scope = scopeFactory.CreateScope();
            (ok, error) = job.Verb switch
            {
                CommandVerb.OpenPorts => await RunOpenPortsAsync(scope, job, actor, origin).ConfigureAwait(false),
                CommandVerb.Install => RunInstall(scope, job, blueprint!, actor, origin),
                CommandVerb.Uninstall => RunUninstall(scope, job, actor, origin),
                // update / backup_* live on IInstanceService, NOT ILifecycleService — they get their own
                // cases so they never fall through to RunLifecycle (whose inner switch would fail them as an
                // "unknown verb"). The lifecycle verbs (start/stop/restart) are the only `_` here.
                CommandVerb.Update => RunUpdate(scope, job, actor, origin),
                CommandVerb.BackupCreate => RunBackupCreate(scope, job, actor, origin),
                CommandVerb.BackupRestore => RunBackupRestore(scope, job, backupName!, actor, origin),
                _ => RunLifecycle(scope, job, actor, origin),
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
        }

        // Verify: re-read authoritative state and reflect it. Best-effort — if the read fails, the next
        // poll/diff cycle reconciles instead (coalesced by the same key). open_ports verifies the firewall
        // (network.patch); uninstall pushes the server.removed tombstone once the instance is gone; the
        // lifecycle verbs and install verify run-state/roster (server.patch — install surfaces the new server).
        try
        {
            switch (job.Verb)
            {
                case CommandVerb.OpenPorts:
                    await PublishNetworkPatchAsync(job.ServerId).ConfigureAwait(false);
                    break;
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
    }

    // The lifecycle verbs (start/stop/restart). Provenance rides the engine call → the kgsm event echo →
    // the M5 audit row. This runner does NOT write an audit row here (no double-write).
    private (bool ok, string? error) RunLifecycle(IServiceScope scope, Job job, string? actor, string? origin)
    {
        var lifecycle = scope.ServiceProvider.GetService(typeof(ILifecycleService)) as ILifecycleService;
        if (lifecycle is null)
            return (false, "lifecycle service unavailable (engine not provisioned)");

        KgsmResult result = job.Verb switch
        {
            CommandVerb.Start => lifecycle.Start(job.ServerId, actor, origin),
            CommandVerb.Stop => lifecycle.Stop(job.ServerId, actor, origin),
            CommandVerb.Restart => lifecycle.Restart(job.ServerId, actor, origin),
            _ => new KgsmResult(1, "", $"unknown verb '{job.Verb}'"),
        };
        return result.IsSuccess ? (true, null) : (false, Detail(result));
    }

    // install (M8·b) — create a new instance from `blueprint`. The job's ServerId is the backend-assigned
    // id the controller resolved via GenerateId; passing it as the install `--name` lands the instance
    // exactly there (kgsm's generate-id echoes an already-unique name verbatim), so the verify target and
    // the instance_installed event's name match. installDir/version stay null — reserved/inert per §3·h
    // (only blueprint + name are honored today). NO audit row here: kgsm emits instance_installed →
    // KgsmAuditConsumer writes the server.install echo with the stamped provenance (the lifecycle case, NOT
    // the open_ports direct write).
    private (bool ok, string? error) RunInstall(
        IServiceScope scope, Job job, string blueprint, string? actor, string? origin)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned");

        KgsmResult result = instances.Install(blueprint, installDir: null, version: null, name: job.ServerId, actor, origin);
        return result.IsSuccess ? (true, null) : (false, Detail(result));
    }

    // uninstall (M8·b) — remove the instance. Same echo-path discipline: kgsm emits instance_uninstalled →
    // KgsmAuditConsumer writes the server.uninstall row. No audit write here.
    private (bool ok, string? error) RunUninstall(IServiceScope scope, Job job, string? actor, string? origin)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned");

        KgsmResult result = instances.Uninstall(job.ServerId, actor, origin);
        return result.IsSuccess ? (true, null) : (false, Detail(result));
    }

    // update (Tier-1 ops) — update the instance to the latest version. On IInstanceService (NOT lifecycle),
    // so it needs its own case. Provenance rides the engine call → kgsm's instance_version_updated echo →
    // the server.update audit row (KgsmAuditConsumer). NO audit row here (the echo path, like install). The
    // controller already 409s an update-on-running synchronously; a subtler engine refusal lands as a failed
    // job + the real stderr.
    private (bool ok, string? error) RunUpdate(IServiceScope scope, Job job, string? actor, string? origin)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned");

        KgsmResult result = instances.Update(job.ServerId, actor, origin);
        return result.IsSuccess ? (true, null) : (false, Detail(result));
    }

    // backup_create (Tier-1 ops) — snapshot the instance. Echo-path discipline: kgsm emits
    // instance_backup_created → KgsmAuditConsumer writes the backup.create row with the stamped provenance.
    // No audit write here.
    private (bool ok, string? error) RunBackupCreate(IServiceScope scope, Job job, string? actor, string? origin)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned");

        KgsmResult result = instances.CreateBackup(job.ServerId, actor, origin);
        return result.IsSuccess ? (true, null) : (false, Detail(result));
    }

    // backup_restore (Tier-1 ops) — restore from a named snapshot (backupName threaded through the closure;
    // Job has no slot for it). Echo-path: kgsm emits instance_backup_restored → the backup.restore row. No
    // audit write here. An unknown backup name surfaces as kgsm's real stderr on a failed job.
    private (bool ok, string? error) RunBackupRestore(
        IServiceScope scope, Job job, string backupName, string? actor, string? origin)
    {
        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned");

        KgsmResult result = instances.RestoreBackup(job.ServerId, backupName, actor, origin);
        return result.IsSuccess ? (true, null) : (false, Detail(result));
    }

    // kgsm's real failure detail (stderr), or a bare exit code when it said nothing — never a fabricated
    // success message.
    private static string Detail(KgsmResult r) =>
        string.IsNullOrWhiteSpace(r.Stderr) ? $"exit {r.ExitCode}" : r.Stderr.Trim();

    // open_ports (M6·b) — intent only: the target ports are SERVER-DERIVED from the instance's own
    // Instance.Ports (never a client list). Opens through IFirewallService (no kgsm event → a DIRECT audit
    // write), audited only on a real change (Applied), mirroring the CLI echo path which emits only on a
    // confirmed open. A NoOp (desired state already held) succeeds without an audit row — recording "opened"
    // when nothing changed would fabricate a change.
    private async Task<(bool ok, string? error)> RunOpenPortsAsync(
        IServiceScope scope, Job job, string? actor, string? origin)
    {
        var firewall = scope.ServiceProvider.GetService(typeof(IFirewallService)) as IFirewallService;
        if (firewall is null)
            return (false, "firewall authority not provisioned");

        var instances = scope.ServiceProvider.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return (false, "engine not provisioned");

        // `instances info <name> --json` — a single-instance spawn (cheaper than the full GetAll roster);
        // verified to carry the SAME structured `ports` block as `list --detailed` (the freeze's fact-check),
        // so the server-derived target is sound and matches the verify-probe's required set.
        Instance? instance = instances.GetInstanceInfo(job.ServerId);
        if (instance is null)
            return (false, $"unknown server '{job.ServerId}'");

        IReadOnlyList<PortMapping> ports = instance.Ports;
        if (ports.Count == 0)
            return (true, null); // nothing required to open — vacuous success, no firewall call, no audit row

        using var timeout = new CancellationTokenSource(OpenPortsTimeout);
        // EnsureOpenAsync is declarative: it makes the firewall own exactly these ports for the instance.
        FirewallActionResult result = await firewall
            .EnsureOpenAsync(job.ServerId, ports, timeout.Token).ConfigureAwait(false);

        if (!result.Ok)
            return (false, string.IsNullOrWhiteSpace(result.Detail)
                ? $"firewall {result.Outcome.ToString().ToLowerInvariant()}"
                : result.Detail);

        // Direct audit write when a rule actually changed — Applied (enforced) OR AppliedInactive (staged on
        // an inactive firewall). Both are real config changes; the audit summary distinguishes "opened" from
        // "staged" via `enforced`. A NoOp (desired state already held) writes nothing — recording "opened"
        // when nothing changed would fabricate a change (symmetric with the CLI echo, which fires only on a
        // confirmed change).
        if (result.Outcome is FirewallOutcome.Applied or FirewallOutcome.AppliedInactive)
            await audit.AppendAsync(AuditMapping.FromPortsOpenedCommand(
                    job.ServerId, ports, actor, origin, options.HostId, job.Id,
                    enforced: result.Outcome == FirewallOutcome.Applied))
                .ConfigureAwait(false);

        return (true, null);
    }

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

    // The open_ports verify: re-probe the firewall and push the fresh network block on servers/{id}/network.
    // Reuses the detail build so the patch is byte-identical to a subsequent GET /servers/{id} network field.
    private async Task PublishNetworkPatchAsync(string serverId)
    {
        Server? server = await aggregator.GetServerDetailAsync(serverId, CancellationToken.None).ConfigureAwait(false);
        if (server?.Network is not null)
            hub.Publish(StreamProtocol.ServerNetworkTopic(serverId), StreamProtocol.ServerNetworkEntityKey(serverId),
                new StreamMessage(StreamProtocol.ServerNetworkTopic(serverId), StreamProtocol.NetworkPatch, server.Network));
    }
}
