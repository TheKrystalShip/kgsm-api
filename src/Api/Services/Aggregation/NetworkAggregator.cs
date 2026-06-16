using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// Builds the M6·b ports views by probing the host-firewall authority (kgsm-firewall) through kgsm-lib's
/// <see cref="IFirewallService"/> and cross-referencing it against domain truth: the per-server
/// <see cref="ServerNetwork"/> block (required ⋈ open) and the host-wide <see cref="HostNetwork"/> grid.
/// <para>
/// <strong>On-demand only.</strong> The firewall is socket-activated and idle-exits, so it is deliberately
/// NOT polled by the <see cref="Leaves.LeafHealthMonitor"/>; every probe here is bounded to
/// <see cref="ProbeTimeout"/> so a hung daemon never stalls a detail view. <strong>Never fabricate.</strong>
/// When the firewall can't answer, per-row <c>open</c> is <see langword="null"/> (never <c>false</c>) and the
/// host grid is <see langword="null"/> (never an empty "nothing open"); the honest
/// <c>ListOwnedAsync</c> <c>Unknown</c> ≠ an <c>Ok</c>-but-empty set, and that distinction is preserved.
/// </para>
/// </summary>
/// <remarks>
/// <see cref="IFirewallService"/> is a kgsm-lib singleton registered ONLY when the firewall is provisioned,
/// so it is resolved optionally from the provider (the same pattern <see cref="ServerAggregator"/> uses for
/// the engine) — an unprovisioned firewall degrades to <c>absent</c>/null rather than a missing-dependency
/// throw. The <c>app</c> join on the host grid resolves <see cref="IInstanceService"/> the same way
/// (best-effort: an absent engine or an unmapped instance leaves <c>app</c> null, never guessed).
/// </remarks>
public sealed class NetworkAggregator(
    ApiOptions options,
    IServiceProvider services,
    ILogger<NetworkAggregator> logger)
{
    // Bound a detail-view probe so a hung daemon degrades fast (matches the other leaf probes' 2s).
    // The open_ports WRITE path bounds itself separately (ufw mutation can be slower) — see CommandRunner.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Build the per-server <see cref="ServerNetwork"/> block. <paramref name="requiredPorts"/> is the
    /// server's own <c>Instance.Ports</c> (domain truth) — the <see cref="ServerNetwork.Required"/> rows are
    /// always present even when the firewall can't answer. <see cref="ServerNetwork.Reachable"/> is reserved
    /// (always null).
    /// </summary>
    public async Task<ServerNetwork> BuildServerNetworkAsync(
        string serverId, IReadOnlyList<PortMapping> requiredPorts, CancellationToken ct)
    {
        IReadOnlyList<(int Port, string Proto)> required = ExpandDistinct(requiredPorts);

        if (!options.FirewallProvisioned)
            return new ServerNetwork(FirewallAvailability.Absent, Unknownify(required), Reachable: null);

        var firewall = services.GetService(typeof(IFirewallService)) as IFirewallService;
        if (firewall is null) // provisioned but unregistered — defensive; treat as absent, never throw.
            return new ServerNetwork(FirewallAvailability.Absent, Unknownify(required), Reachable: null);

        FirewallListResult? probe = await ProbeAsync(firewall, serverId, ct).ConfigureAwait(false);

        // Map the honest outcome to availability + (only when Ok) the set of owned (port, proto).
        string availability;
        HashSet<(int, string)>? owned = null;
        if (probe is null)
            availability = FirewallAvailability.Down; // unreachable / timed out
        else
            switch (probe.Status)
            {
                case FirewallListStatus.Ok:
                    availability = FirewallAvailability.Operational;
                    owned = OwnedSet(probe.Rules);
                    break;
                case FirewallListStatus.Unsupported:
                    availability = FirewallAvailability.Unsupported;
                    break;
                default: // Unknown (and any future value) — honest "can't enumerate", NOT empty.
                    availability = FirewallAvailability.Unknown;
                    break;
            }

        var rows = new List<RequiredPort>(required.Count);
        foreach ((int port, string proto) in required)
            // open is null whenever we lack an authoritative answer — never coerced to false.
            rows.Add(new RequiredPort(port, proto, owned is null ? null : owned.Contains((port, proto))));

        return new ServerNetwork(availability, rows, Reachable: null);
    }

    /// <summary>
    /// Build the host-wide <see cref="HostNetwork"/> grid (the Diagnostics open-ports view). Returns
    /// <see langword="null"/> when the firewall is absent/unreachable/unknown (honest "not measurable now");
    /// an empty <see cref="HostNetwork.OpenPorts"/> means the firewall answered and owns no rules.
    /// </summary>
    public async Task<HostNetwork?> BuildHostNetworkAsync(CancellationToken ct)
    {
        if (!options.FirewallProvisioned)
            return null;

        var firewall = services.GetService(typeof(IFirewallService)) as IFirewallService;
        if (firewall is null)
            return null;

        FirewallListResult? probe = await ProbeAsync(firewall, instance: null, ct).ConfigureAwait(false);
        // Only an Ok result yields a grid; Unknown/Unsupported/unreachable are honest null (never []).
        if (probe is not { Status: FirewallListStatus.Ok })
            return null;

        IReadOnlyDictionary<string, string> appByInstance = AppMap();

        var rows = new List<OpenPort>();
        foreach (FirewallOwnedRule rule in probe.Rules)
        {
            string? app = appByInstance.TryGetValue(rule.Instance, out string? a) ? a : null;
            foreach ((int port, string proto) in ExpandDistinct(rule.Ports))
                rows.Add(new OpenPort(port, proto, app, rule.Instance));
        }

        // Deterministic order so polling/diffing the grid is stable.
        rows.Sort(static (x, y) =>
        {
            int byServer = string.CompareOrdinal(x.Server, y.Server);
            if (byServer != 0) return byServer;
            int byPort = x.Port.CompareTo(y.Port);
            return byPort != 0 ? byPort : string.CompareOrdinal(x.Proto, y.Proto);
        });
        return new HostNetwork(rows);
    }

    // Run ListOwnedAsync bounded to ProbeTimeout. Returns null when the authority is unreachable or the
    // probe times out (→ Down); a reachable backend returns a result whose Status carries the honest
    // Ok/Unknown/Unsupported. A genuine REQUEST abort (the caller's token) propagates, not swallowed.
    private async Task<FirewallListResult?> ProbeAsync(IFirewallService firewall, string? instance, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ProbeTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            return await firewall.ListOwnedAsync(instance, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) // FirewallException (unreachable) or our own probe-timeout cancellation
        {
            logger.LogDebug(ex, "firewall probe failed (instance={Instance}); reporting unreachable.",
                instance ?? "(all)");
            return null;
        }
    }

    // The instance -> blueprint(app) map for the host grid's `app` join, best-effort. An absent/failed
    // engine read leaves the map empty -> every app is null (honest, never guessed).
    private IReadOnlyDictionary<string, string> AppMap()
    {
        var instances = services.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
            return EmptyAppMap;

        try
        {
            Dictionary<string, Instance> roster = instances.GetAll();
            var map = new Dictionary<string, string>(roster.Count, StringComparer.Ordinal);
            foreach ((string id, Instance inst) in roster)
                map[id] = ServerAggregator.CleanBlueprintId(inst);
            return map;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "roster read for the open-ports app join failed; apps will be null.");
            return EmptyAppMap;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyAppMap =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Expand port ranges to one (port, proto) per port, protocol lower-cased, de-duplicated, order-preserved.
    private static IReadOnlyList<(int Port, string Proto)> ExpandDistinct(IEnumerable<PortMapping>? ports)
    {
        if (ports is null) return [];
        var seen = new HashSet<(int, string)>();
        var ordered = new List<(int, string)>();
        foreach ((int port, string protocol) in ports.Expand())
        {
            string proto = (protocol ?? "").ToLowerInvariant();
            if (seen.Add((port, proto)))
                ordered.Add((port, proto));
        }
        return ordered;
    }

    private static HashSet<(int, string)> OwnedSet(IEnumerable<FirewallOwnedRule> rules)
    {
        var set = new HashSet<(int, string)>();
        foreach (FirewallOwnedRule rule in rules)
            foreach ((int port, string protocol) in rule.Ports.Expand())
                set.Add((port, (protocol ?? "").ToLowerInvariant()));
        return set;
    }

    // Required rows with an unknowable open verdict (firewall absent/unregistered): null, never false.
    private static IReadOnlyList<RequiredPort> Unknownify(IReadOnlyList<(int Port, string Proto)> required)
    {
        var rows = new List<RequiredPort>(required.Count);
        foreach ((int port, string proto) in required)
            rows.Add(new RequiredPort(port, proto, Open: null));
        return rows;
    }
}
