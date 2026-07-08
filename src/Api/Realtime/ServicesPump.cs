using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The leaf-service state pump: polls systemd every <see cref="ApiOptions.ServicesPollMs"/> for each leaf's
/// unit state and emits <c>service.patch</c> on the <c>hosts/{id}/services</c> topic when a flip is detected.
/// The canonical source for service health and running status — the client hydrates the initial list via REST
/// (<c>GET /hosts/{id}/services</c>) and applies patch frames from here on.
/// </summary>
/// <remarks>
/// <para><b>Gated:</b> ticks only while at least one connection is subscribed to the host's services topic,
/// so an idle host never shells <c>systemctl show</c> for the pump.</para>
/// <para><b>Diff, not flood.</b> Each tick compares the fresh systemd reading against the previous one and
/// emits only for leaves whose state actually changed (State, SubState, Enabled, Since, MainPid, MemoryBytes,
/// or Health status). The first active tick primes the baseline without emitting — the client already
/// hydrated via REST (§3·j), so subscribing must not replay the whole list as patches.</para>
/// <para><b>Honesty.</b> A <c>systemctl show</c> failure maps every unit to <c>UnitState.Unknown</c> — never
/// a fabricated "running"/"stopped". A health probe flip is detected by comparing the cached
/// <see cref="LeafHealthMonitor.Current"/> capability status, so a leaf that is systemd-active yet failing its
/// <c>/health</c> correctly emits a patch with the degraded health.</para>
/// <para><b>Operator-gated at the socket</b> (see <see cref="StreamProtocol.RequiresOperator"/>), matching the
/// REST endpoint's <c>AuthPolicy.Operator</c> gate.</para>
/// </remarks>
public sealed class ServicesPump(
    StreamHub hub,
    SystemdReader systemd,
    LeafHealthMonitor health,
    LeafRegistry registry,
    ApiOptions options,
    ILogger<ServicesPump> logger) : BackgroundService
{
    private readonly IReadOnlyList<LeafDescriptor> _catalog = LeafCatalog.Default;

    // Previous tick's systemd readings, keyed by unit name.
    private Dictionary<string, UnitState> _last = new(StringComparer.Ordinal);
    // Previous tick's health status per leaf id (the capability probe result).
    private Dictionary<string, string?> _lastHealth = new(StringComparer.Ordinal);
    private bool _primed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string topic = StreamProtocol.HostServicesTopic(options.HostId);
        logger.LogInformation("services pump: started (interval={IntervalMs}ms — systemd poll, subscriber-gated)",
            options.ServicesPollMs);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.ServicesPollMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    // Subscriber gate: idle when nobody watches. Reset baseline so a reconnect primes fresh.
                    if (!hub.HasSubscribers(topic))
                    {
                        _last.Clear();
                        _lastHealth.Clear();
                        _primed = false;
                        continue;
                    }

                    IReadOnlyList<string> units = _catalog.Select(l => l.Unit).ToList();
                    IReadOnlyDictionary<string, UnitState> states =
                        await systemd.ReadAsync(units, stoppingToken).ConfigureAwait(false);
                    HostCapabilities caps = health.Current;

                    // First active tick: prime baseline without emitting (client hydrated via REST).
                    if (!_primed)
                    {
                        _last = new Dictionary<string, UnitState>(states, StringComparer.Ordinal);
                        _lastHealth = BuildHealthIndex(_catalog, caps);
                        _primed = true;
                        continue;
                    }

                    // Diff each leaf against the previous tick.
                    foreach (LeafDescriptor leaf in _catalog)
                    {
                        states.TryGetValue(leaf.Unit, out UnitState? next);
                        _last.TryGetValue(leaf.Unit, out UnitState? prev);
                        next ??= UnitState.Unknown;
                        prev ??= UnitState.Unknown;

                        string? nextHealth = HealthStatus(leaf, caps);
                        _lastHealth.TryGetValue(leaf.Id, out string? prevHealth);

                        if (UnitStateEqual(prev, next) && nextHealth == prevHealth)
                            continue; // no change → no emit

                        LeafService svc = BuildLeafService(leaf, next, caps, registry);
                        hub.Publish(topic, StreamProtocol.ServiceEntityKey(leaf.Id),
                            new StreamMessage(topic, StreamProtocol.ServicePatch, svc));
                    }

                    _last = new Dictionary<string, UnitState>(states, StringComparer.Ordinal);
                    _lastHealth = BuildHealthIndex(_catalog, caps);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "services pump tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    /// <summary>Build a full <see cref="LeafService"/> for a leaf — the same shape the REST endpoint returns.</summary>
    internal static LeafService BuildLeafService(
        LeafDescriptor leaf, UnitState st, HostCapabilities caps, LeafRegistry registry)
    {
        return BuildLeafService(leaf, st, caps, registry is null ? null : id => registry.IsProvisioned(id));
    }

    /// <summary>Build a full <see cref="LeafService"/> for a leaf — the same shape the REST endpoint returns.
    /// Accepts a provisioned-resolver function for testability (avoids needing a real <see cref="LeafRegistry"/>
    /// in unit tests).</summary>
    internal static LeafService BuildLeafService(
        LeafDescriptor leaf, UnitState st, HostCapabilities caps, Func<string, bool>? isProvisioned)
    {
        return new LeafService(
            Id: leaf.Id,
            DisplayName: leaf.DisplayName,
            Role: leaf.Role,
            Unit: leaf.Unit,
            State: st.State,
            OnDemand: leaf.OnDemand,
            Provisioned: ProvisionableLeaf.IsProvisionable(leaf.Id) ? isProvisioned?.Invoke(leaf.Id) : null,
            SubState: st.SubState,
            Enabled: st.Enabled,
            Since: st.Since,
            MainPid: st.MainPid,
            MemoryBytes: st.MemoryBytes,
            Health: HealthFor(leaf, caps));
    }

    private static bool UnitStateEqual(UnitState a, UnitState b) =>
        a.State == b.State
        && a.SubState == b.SubState
        && a.Enabled == b.Enabled
        && a.Since == b.Since
        && a.MainPid == b.MainPid
        && a.MemoryBytes == b.MemoryBytes;

    private static Dictionary<string, string?> BuildHealthIndex(
        IReadOnlyList<LeafDescriptor> catalog, HostCapabilities caps)
    {
        var idx = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (LeafDescriptor leaf in catalog)
            idx[leaf.Id] = HealthStatus(leaf, caps);
        return idx;
    }

    private static string? HealthStatus(LeafDescriptor leaf, HostCapabilities caps) => leaf.Health switch
    {
        LeafHealthSource.SelfApi => CapabilityStatus.Operational,
        LeafHealthSource.Metrics => caps.Metrics.Status,
        LeafHealthSource.Assistant => caps.Assistant.Status,
        LeafHealthSource.Watchdog => caps.Watchdog.Status,
        LeafHealthSource.Scheduler => caps.Scheduler.Status,
        _ => null,
    };

    private static LeafServiceHealth? HealthFor(LeafDescriptor leaf, HostCapabilities caps) => leaf.Health switch
    {
        LeafHealthSource.SelfApi => new LeafServiceHealth(CapabilityStatus.Operational, null),
        LeafHealthSource.Metrics => FromCapability(caps.Metrics),
        LeafHealthSource.Assistant => FromCapability(caps.Assistant),
        LeafHealthSource.Watchdog => FromCapability(caps.Watchdog),
        LeafHealthSource.Scheduler => FromCapability(caps.Scheduler),
        _ => null,
    };

    private static LeafServiceHealth? FromCapability(Capability c) =>
        c.Provisioned ? new LeafServiceHealth(c.Status, c.Message) : null;
}
