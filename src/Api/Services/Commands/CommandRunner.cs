using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// Executes an admitted command job off the request path (M3 lifecycle + M6·b <c>open_ports</c>). The
/// controller returns <c>202</c> immediately; this runner drives the verb to completion on a background
/// task, streaming each state transition as <c>job.patch</c> on the <c>jobs</c> topic and, on settle, a
/// fresh verify patch — <c>server.patch</c> (run-state, lifecycle verbs) or <c>network.patch</c> on
/// <c>servers/{id}/network</c> (the firewall re-probe, <c>open_ports</c>).
/// </summary>
/// <remarks>
/// <para><b>Lifetime (load-bearing):</b> a singleton. The <c>202</c> disposes the request's DI scope, but
/// the kgsm-lib services it uses are <em>transient/process-based</em> (<see cref="ILifecycleService"/>) or a
/// conditionally-registered singleton (<see cref="IFirewallService"/>) — so the background task creates its
/// <b>own</b> scope via <see cref="IServiceScopeFactory"/> and resolves them <em>there</em>. Only value data
/// (the <see cref="Job"/>) crosses the async boundary; never a request-scoped service.</para>
/// <para><b>Audit split (the no-double-write contract):</b> lifecycle verbs stamp <c>actor</c>+<c>origin</c>
/// onto the engine call and the M5 consumer records the event echo — this runner writes NO audit row for
/// them. <c>open_ports</c> goes through <see cref="IFirewallService"/>, which emits no event, so it is the
/// <c>auth.*</c> case: this runner writes the <c>network.ports.open</c> row <b>directly</b>. Disjoint by
/// construction — kgsm never echoes an api firewall call.</para>
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
        _ = Task.Run(() => ExecuteAsync(job, actor, origin));

    private async Task ExecuteAsync(Job job, string? actor, string? origin)
    {
        bool ok = false;
        string? error = null;
        try
        {
            Publish(registry.Update(job with { State = JobState.Running }));

            // Own scope — the request scope is long gone; the kgsm-lib services are transient/process-based
            // (lifecycle) or conditionally-registered (firewall), so resolve them here, never capture them.
            using IServiceScope scope = scopeFactory.CreateScope();
            (ok, error) = job.Verb == CommandVerb.OpenPorts
                ? await RunOpenPortsAsync(scope, job, actor, origin).ConfigureAwait(false)
                : RunLifecycle(scope, job, actor, origin);
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
        // (network.patch); lifecycle verbs verify run-state (server.patch).
        try
        {
            if (job.Verb == CommandVerb.OpenPorts)
                await PublishNetworkPatchAsync(job.ServerId).ConfigureAwait(false);
            else
                await PublishServerPatchAsync(job.ServerId).ConfigureAwait(false);
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
        if (result.IsSuccess)
            return (true, null);
        return (false, string.IsNullOrWhiteSpace(result.Stderr) ? $"exit {result.ExitCode}" : result.Stderr.Trim());
    }

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

        // Direct audit write only when rules actually changed (Applied) — symmetric with the CLI echo path.
        if (result.Outcome == FirewallOutcome.Applied)
            await audit.AppendAsync(
                AuditMapping.FromPortsOpenedCommand(job.ServerId, ports, actor, origin, options.HostId, job.Id))
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
