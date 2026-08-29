using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit tests for <see cref="PeersTableGate"/> — the disable-list gate (<c>PLAN-peers.md §0</c> #8, §2
/// #7/#8; P0 §9 self-validation checklist item 3) — against a REAL <see cref="PeersStore"/> backed by a
/// temp-file SQLite <see cref="AppDbContext"/> (the <see cref="OutboxDrainerTests"/> <c>NewDb</c>
/// pattern), no <c>WebApplicationFactory</c>: the gate is a table lookup plus a boolean flip, so a direct
/// unit test is the cheapest thing that proves it without going through cluster-token auth or HTTP.
/// </summary>
public sealed class PeersTableGateTests
{
    [Fact]
    public async Task IsEnabledAsync_UnknownNode_ReturnsTrue()
    {
        PeersStore store = NewStore();
        var gate = new PeersTableGate(store, NullLogger<PeersTableGate>.Instance);

        // No roster row for this node id at all — absence is NOT rejection (§2 #7): the shared cluster
        // secret alone is the trust boundary until a node is explicitly disabled.
        Assert.True(await gate.IsEnabledAsync("node-never-seen"));
    }

    [Fact]
    public async Task IsEnabledAsync_PresentAndEnabledRow_ReturnsTrue()
    {
        PeersStore store = NewStore();
        await store.UpsertAsync(Peer("node-b", enabled: true), default);
        var gate = new PeersTableGate(store, NullLogger<PeersTableGate>.Instance);

        Assert.True(await gate.IsEnabledAsync("node-b"));
    }

    [Fact]
    public async Task IsEnabledAsync_PresentAndDisabledRow_ReturnsFalse()
    {
        PeersStore store = NewStore();
        await store.UpsertAsync(Peer("node-b", enabled: false), default);
        var gate = new PeersTableGate(store, NullLogger<PeersTableGate>.Instance);

        Assert.False(await gate.IsEnabledAsync("node-b"));
    }

    private static PeerEntity Peer(string nodeId, bool enabled) => new()
    {
        Id = "peer_" + Guid.NewGuid().ToString("N")[..10],
        Url = $"https://{nodeId}.test",
        NodeId = nodeId,
        Status = "unknown",
        ApiVersion = "v1",
        Enabled = enabled,
    };

    private static PeersStore NewStore()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"peersgatetest-{Guid.NewGuid():N}.db");
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .BuildServiceProvider();
        using (IServiceScope scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        return new PeersStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PeersStore>.Instance);
    }
}
