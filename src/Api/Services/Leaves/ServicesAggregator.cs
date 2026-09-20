using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Engine;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Builds the Services board payload (<c>GET /hosts/{id}/services</c>) by joining two axes for each leaf
/// this node runs (<see cref="Catalog"/>):
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
public sealed class ServicesAggregator(
    SystemdReader systemd,
    LeafHealthMonitor health,
    LeafRegistry registry,
    LeafDescriptorStore descriptors,
    AnchorDescriptorStore anchors,
    EngineInfoService engine,
    ApiOptions options)
{
    public async Task<ServicesSnapshot> SnapshotAsync(CancellationToken ct)
    {
        IReadOnlyList<LeafDescriptor> catalog = Catalog();
        IReadOnlyList<string> units = catalog.Select(l => l.Unit).ToList();
        IReadOnlyDictionary<string, UnitState> states = await systemd.ReadAsync(units, ct).ConfigureAwait(false);
        HostCapabilities caps = health.Current;

        var rows = new List<ComponentService>(catalog.Count + 1) { await EngineRowAsync(ct).ConfigureAwait(false) };
        foreach (LeafDescriptor leaf in catalog)
        {
            UnitState st = states.TryGetValue(leaf.Unit, out UnitState? s) ? s : UnitState.Unknown;
            rows.Add(new ComponentService(
                Id: leaf.Id,
                DisplayName: leaf.DisplayName,
                Role: leaf.Role,
                Unit: leaf.Unit,
                State: st.State,
                OnDemand: leaf.OnDemand,
                // The link this API holds to the leaf, from the runtime registry. Null — omitted, and drawn
                // as "not applicable" rather than "disconnected" — for a leaf where there is no such link
                // to arm: api/bot, which this API holds no client to, and speech, whose presence is read
                // off its socket file rather than stored here. See ProvisionableLeaf.
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

    /// <summary>
    /// The engine's pseudo-leaf row, first on the board: kgsm itself is an ecosystem component and belongs
    /// with the rest, but it is a stateless CLI, not a unit — so its row carries none of the systemd fields
    /// and its state is its own vocabulary: <c>available</c> (the identity probe answered — a real
    /// invocation, not an inference), <c>unavailable</c> (configured but would not answer), or
    /// <c>not-installed</c> (no engine configured on this host). Deliberately NOT in the catalog:
    /// <see cref="Knows"/> stays false for it, so no per-leaf endpoint (config, restart, commands) ever
    /// treats it as a unit-backed leaf.
    /// </summary>
    private async Task<ComponentService> EngineRowAsync(CancellationToken ct)
    {
        string state = "not-installed";
        if (options.KgsmProvisioned)
        {
            EngineInfo? info;
            try { info = await engine.GetAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { info = null; }
            state = info is null ? "unavailable" : "available";
        }
        return new ComponentService(
            Id: "kgsm",
            DisplayName: "KGSM",
            Role: "The game-server engine — blueprints, instances, libraries, config & events",
            Unit: "",
            State: state,
            OnDemand: false,
            Provisioned: null,
            SubState: null,
            Enabled: null,
            Since: null,
            MainPid: null,
            MemoryBytes: null,
            Health: null);
    }

    /// <summary>
    /// Whether this host has a leaf by that id — the identity check a caller addressing one leaf needs,
    /// answered from the in-memory catalog + descriptor scan with no <c>systemctl</c> spawn. Membership is
    /// the same set <see cref="SnapshotAsync"/> reports on, so a leaf visible on the board is addressable
    /// and one that is not, is not.
    /// </summary>
    public bool Knows(string leafId) =>
        Catalog().Any(l => string.Equals(l.Id, leafId, StringComparison.Ordinal));

    /// <summary>
    /// The leaves this node runs: the built-in catalog, less anything this host describes as an anchor,
    /// plus any leaf that shipped a config descriptor this API has never heard of. A host that runs a leaf
    /// added after this API was built still shows it on the board — with its systemd liveness, which is
    /// universal, and no deep health, which is honest: this API has no probe for a leaf it does not know.
    /// Known leaves keep the catalog as their identity authority.
    /// <para>
    /// <b>An anchor is subtracted by name.</b> Leaf-or-anchor is a deployment choice, so the same component
    /// is a leaf on one host and an anchor on another, and only the descriptor it installed here says
    /// which. An anchor serves one capability to the whole cluster and is a peer of this node rather than
    /// something it hosts: it is reached as the member it is, and a node that happens to share a machine
    /// with one says nothing about it.
    /// </para>
    /// </summary>
    public IReadOnlyList<LeafDescriptor> Catalog()
    {
        IReadOnlySet<string> anchored = anchors.Ids;
        IReadOnlyCollection<LeafConfigDescriptor> installed = descriptors.All;

        return
        [
            .. LeafCatalog.Default.Where(l => !anchored.Contains(l.Id)),
            .. installed
                .Where(d => !LeafCatalog.Default.Any(l => string.Equals(l.Id, d.Id, StringComparison.Ordinal)))
                .Where(d => !anchored.Contains(d.Id))
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .Select(d => new LeafDescriptor(d.Id, d.Unit, d.DisplayName, d.Role, d.OnDemand, LeafHealthSource.None)),
        ];
    }

    private static ComponentServiceHealth? HealthFor(LeafDescriptor leaf, HostCapabilities caps) => leaf.Health switch
    {
        // We are answering this request, so the api is reachable by definition.
        LeafHealthSource.SelfApi => new ComponentServiceHealth(CapabilityStatus.Operational, null),
        LeafHealthSource.Metrics => FromCapability(caps.Metrics),
        LeafHealthSource.Assistant => FromCapability(caps.Assistant),
        LeafHealthSource.Watchdog => FromCapability(caps.Watchdog),
        LeafHealthSource.Scheduler => FromCapability(caps.Scheduler),
        LeafHealthSource.Reactor => FromCapability(caps.Reactor),
        _ => null,   // None — no probe; systemd liveness is all we honestly have
    };

    // A capability the api probes → a health row, EXCEPT when it isn't provisioned to probe it (absent):
    // then there is no health signal at all (null), which the frontend renders distinctly from a probed
    // 'down'/'unknown'. Never fabricated from liveness.
    private static ComponentServiceHealth? FromCapability(Capability c) =>
        c.Provisioned ? new ComponentServiceHealth(c.Status, c.Message) : null;
}
