using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Builds the Services board payload (<c>GET /hosts/{id}/services</c>) by joining two axes for each leaf in
/// the <see cref="LeafCatalog"/>:
/// <list type="bullet">
///   <item><b>Liveness</b> — systemd's own view via <see cref="SystemdReader"/> (universal: every leaf has a
///   unit). This is the spine.</item>
///   <item><b>Deep health</b> — the api's existing capability probes via <see cref="LeafHealthMonitor"/>
///   (monitor/assistant/watchdog) plus the api itself. Layered ON TOP where it exists; honest <c>null</c>
///   where the api has no probe (firewall/bot) — never inferred from liveness.</item>
/// </list>
/// The two are kept distinct on purpose: a unit can be <c>active</c> yet failing its <c>/health</c> (the
/// interesting case the at-a-glance Overview dot can't show). Read-only in this slice — start/stop/restart
/// controls are a later increment (polkit grant + admin gate + audit).
/// </summary>
public sealed class ServicesAggregator(SystemdReader systemd, LeafHealthMonitor health, LeafRegistry registry)
{
    private static readonly IReadOnlyList<LeafDescriptor> Catalog = LeafCatalog.Default;

    public async Task<ServicesSnapshot> SnapshotAsync(CancellationToken ct)
    {
        IReadOnlyList<string> units = Catalog.Select(l => l.Unit).ToList();
        IReadOnlyDictionary<string, UnitState> states = await systemd.ReadAsync(units, ct).ConfigureAwait(false);
        HostCapabilities caps = health.Current;

        var rows = new List<LeafService>(Catalog.Count);
        foreach (LeafDescriptor leaf in Catalog)
        {
            UnitState st = states.TryGetValue(leaf.Unit, out UnitState? s) ? s : UnitState.Unknown;
            rows.Add(new LeafService(
                Id: leaf.Id,
                DisplayName: leaf.DisplayName,
                Role: leaf.Role,
                Unit: leaf.Unit,
                State: st.State,
                OnDemand: leaf.OnDemand,
                // Runtime provisioning from the registry for the four provisionable leaves; null (omitted)
                // for api/bot where it isn't applicable.
                Provisioned: ProvisionableLeaf.IsProvisionable(leaf.Id) ? registry.IsProvisioned(leaf.Id) : null,
                SubState: st.SubState,
                Enabled: st.Enabled,
                Since: st.Since,
                MainPid: st.MainPid,
                MemoryBytes: st.MemoryBytes,
                Health: HealthFor(leaf, caps)));
        }
        return new ServicesSnapshot(rows);
    }

    private static LeafServiceHealth? HealthFor(LeafDescriptor leaf, HostCapabilities caps) => leaf.Health switch
    {
        // We are answering this request, so the api is reachable by definition.
        LeafHealthSource.SelfApi => new LeafServiceHealth(CapabilityStatus.Operational, null),
        LeafHealthSource.Metrics => FromCapability(caps.Metrics),
        LeafHealthSource.Assistant => FromCapability(caps.Assistant),
        LeafHealthSource.Watchdog => FromCapability(caps.Watchdog),
        _ => null,   // None — no probe; systemd liveness is all we honestly have
    };

    // A capability the api probes → a health row, EXCEPT when it isn't provisioned to probe it (absent):
    // then there is no health signal at all (null), which the frontend renders distinctly from a probed
    // 'down'/'unknown'. Never fabricated from liveness.
    private static LeafServiceHealth? FromCapability(Capability c) =>
        c.Provisioned ? new LeafServiceHealth(c.Status, c.Message) : null;
}
