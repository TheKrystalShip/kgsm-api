using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Exceptions;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M6·b cross-reference coverage for <see cref="NetworkAggregator"/> — the firewall honesty surface with a
/// fake <see cref="IFirewallService"/>. The load-bearing invariants: per-row <c>open</c> is <c>null</c>
/// (never <c>false</c>) whenever the firewall can't answer; the honest <c>Unknown</c> ≠ an <c>Ok</c>-but-empty
/// set (one is null/null-open, the other is a real grid); <c>reachable</c> is reserved (always null); and
/// <c>required</c> is always present (domain truth) even when the firewall is absent. (The host <c>app</c>
/// join from the roster is a trivial dict lookup verified live, so these tests run with no engine → app null.)
/// </summary>
public sealed class NetworkAggregatorTests
{
    // --- server network: the required ⋈ open cross-reference ---------------------------------------

    [Fact]
    public async Task ServerNetwork_FirewallAbsent_RequiredPresentOpenNull()
    {
        NetworkAggregator agg = Aggregator(provisioned: false, firewall: null);

        ServerNetwork net = await agg.BuildServerNetworkAsync(
            "valheim", [Port(2456, "udp")], CancellationToken.None);

        Assert.Equal(FirewallAvailability.Absent, net.Firewall);
        Assert.Null(net.Reachable);                       // reserved — no upstream prober
        RequiredPort row = Assert.Single(net.Required);
        Assert.Equal(2456, row.Port);
        Assert.Equal("udp", row.Proto);
        Assert.Null(row.Open);                            // unknowable → null, never fabricated false
    }

    [Fact]
    public async Task ServerNetwork_Operational_OpenTrueForOwnedFalseForNot()
    {
        // Firewall owns 2456/udp only; the server requires 2456/udp + 2457/udp.
        var fw = new FakeFirewall
        {
            OnList = _ => Ok([new FirewallOwnedRule("valheim", [Port(2456, "udp")])]),
        };
        NetworkAggregator agg = Aggregator(provisioned: true, fw);

        ServerNetwork net = await agg.BuildServerNetworkAsync(
            "valheim", [Port(2456, "udp"), Port(2457, "udp")], CancellationToken.None);

        Assert.Equal(FirewallAvailability.Operational, net.Firewall);
        Assert.Null(net.Reachable);
        Assert.Equal(2, net.Required.Count);
        Assert.True(net.Required[0].Open);                // 2456/udp owned
        Assert.False(net.Required[1].Open);               // 2457/udp not owned — a confident, measured false
    }

    [Fact]
    public async Task ServerNetwork_Unknown_OpenIsNullNotFalse()
    {
        // The whole point of ListOwnedAsync's Unknown: the backend CAN'T answer — not "nothing open".
        var fw = new FakeFirewall { OnList = _ => new FirewallListResult { Status = FirewallListStatus.Unknown } };
        NetworkAggregator agg = Aggregator(provisioned: true, fw);

        ServerNetwork net = await agg.BuildServerNetworkAsync(
            "valheim", [Port(2456, "udp")], CancellationToken.None);

        Assert.Equal(FirewallAvailability.Unknown, net.Firewall);
        Assert.Null(net.Required[0].Open);                // honest unknown, NOT a fabricated closed
    }

    [Fact]
    public async Task ServerNetwork_Unreachable_IsDownWithNullOpen()
    {
        var fw = new FakeFirewall { Throw = true };       // FirewallException — daemon unreachable
        NetworkAggregator agg = Aggregator(provisioned: true, fw);

        ServerNetwork net = await agg.BuildServerNetworkAsync(
            "valheim", [Port(2456, "udp")], CancellationToken.None);

        Assert.Equal(FirewallAvailability.Down, net.Firewall);
        Assert.Null(net.Required[0].Open);
    }

    [Fact]
    public async Task ServerNetwork_Unsupported_IsUnsupportedWithNullOpen()
    {
        var fw = new FakeFirewall { OnList = _ => new FirewallListResult { Status = FirewallListStatus.Unsupported } };
        NetworkAggregator agg = Aggregator(provisioned: true, fw);

        ServerNetwork net = await agg.BuildServerNetworkAsync(
            "valheim", [Port(2456, "udp")], CancellationToken.None);

        Assert.Equal(FirewallAvailability.Unsupported, net.Firewall);
        Assert.Null(net.Required[0].Open);
    }

    [Fact]
    public async Task ServerNetwork_RangeExpandedToPerPortRows()
    {
        // A required range 2456-2458/udp expands to three rows; the firewall owns the whole range.
        var fw = new FakeFirewall
        {
            OnList = _ => Ok([new FirewallOwnedRule("valheim", [Range(2456, 2458, "udp")])]),
        };
        NetworkAggregator agg = Aggregator(provisioned: true, fw);

        ServerNetwork net = await agg.BuildServerNetworkAsync(
            "valheim", [Range(2456, 2458, "udp")], CancellationToken.None);

        Assert.Equal(3, net.Required.Count);
        Assert.Equal([2456, 2457, 2458], net.Required.Select(r => r.Port));
        Assert.All(net.Required, r => Assert.True(r.Open));
    }

    // --- host open-ports grid: Unknown ≠ empty -----------------------------------------------------

    [Fact]
    public async Task HostNetwork_Ok_BuildsGridRowsServerFromRuleAppNullWithoutEngine()
    {
        var fw = new FakeFirewall
        {
            // The host grid queries ALL owned rules (instance == null).
            OnList = instance =>
            {
                Assert.Null(instance);
                return Ok([new FirewallOwnedRule("valheim", [Port(2456, "udp"), Port(27015, "tcp")])]);
            },
        };
        NetworkAggregator agg = Aggregator(provisioned: true, fw);

        HostNetwork? host = await agg.BuildHostNetworkAsync(CancellationToken.None);

        Assert.NotNull(host);
        Assert.Equal(2, host!.OpenPorts.Count);
        Assert.All(host.OpenPorts, p => Assert.Equal("valheim", p.Server));
        Assert.All(host.OpenPorts, p => Assert.Null(p.App));   // no engine resolved → app null, never guessed
    }

    [Fact]
    public async Task HostNetwork_OkButEmpty_IsEmptyListNotNull()
    {
        var fw = new FakeFirewall { OnList = _ => Ok([]) };
        NetworkAggregator agg = Aggregator(provisioned: true, fw);

        HostNetwork? host = await agg.BuildHostNetworkAsync(CancellationToken.None);

        Assert.NotNull(host);                               // Ok-but-empty: a measured "nothing open"
        Assert.Empty(host!.OpenPorts);
    }

    [Fact]
    public async Task HostNetwork_Unknown_IsNull()
    {
        var fw = new FakeFirewall { OnList = _ => new FirewallListResult { Status = FirewallListStatus.Unknown } };
        NetworkAggregator agg = Aggregator(provisioned: true, fw);

        HostNetwork? host = await agg.BuildHostNetworkAsync(CancellationToken.None);

        Assert.Null(host);                                  // can't measure → null (distinct from empty)
    }

    [Fact]
    public async Task HostNetwork_FirewallAbsent_IsNull()
    {
        NetworkAggregator agg = Aggregator(provisioned: false, firewall: null);
        Assert.Null(await agg.BuildHostNetworkAsync(CancellationToken.None));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static NetworkAggregator Aggregator(bool provisioned, IFirewallService? firewall) =>
        new(Options(provisioned), new StubProvider { Firewall = firewall }, NullLogger<NetworkAggregator>.Instance);

    private static ApiOptions Options(bool firewallProvisioned)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KGSM_API_FIREWALL_SOCKET"] = firewallProvisioned ? "/run/kgsm-firewall/firewall.sock" : "",
            })
            .Build();
        return ApiOptions.FromConfiguration(config);
    }

    private static PortMapping Port(int port, string proto) => new() { Start = port, End = port, Protocol = proto };
    private static PortMapping Range(int start, int end, string proto) => new() { Start = start, End = end, Protocol = proto };

    private static FirewallListResult Ok(IReadOnlyList<FirewallOwnedRule> rules) =>
        new() { Status = FirewallListStatus.Ok, Rules = rules };

    private sealed class StubProvider : IServiceProvider
    {
        public IFirewallService? Firewall;
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IFirewallService)) return Firewall;
            return null; // IInstanceService unresolved → host app-join degrades to null (tested above)
        }
    }

    // Switch-on-input fake (the project convention) — no mutable per-call state beyond the configured func.
    private sealed class FakeFirewall : IFirewallService
    {
        public Func<string?, FirewallListResult>? OnList;
        public bool Throw;

        public Task<FirewallListResult> ListOwnedAsync(string? instanceName = null, CancellationToken cancellationToken = default)
        {
            if (Throw) throw new FirewallException("unreachable", "/run/kgsm-firewall/firewall.sock");
            return Task.FromResult(OnList!(instanceName));
        }

        public Task<FirewallActionResult> EnsureOpenAsync(string instanceName, IReadOnlyList<PortMapping> ports, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<FirewallActionResult> RemoveAsync(string instanceName, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<FirewallBackendInfo> BackendAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public void Dispose() { }
    }
}
