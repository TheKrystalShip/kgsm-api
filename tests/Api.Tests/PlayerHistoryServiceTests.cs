using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Players;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit tests for the permanent player roster (<see cref="PlayerHistoryService"/>) — the in-memory
/// cache logic: join upsert, leave status change, reset marks offline, ban, status ordering, and
/// the player-identity resolution. DB writes are fire-and-forget (fail silently with the dummy
/// <see cref="IServiceScopeFactory"/>); this file is about the CACHE state and WS publishing.
/// </summary>
public sealed class PlayerHistoryServiceTests
{
    private static PlayerHistoryService NewService()
    {
        // Dummy scope factory — DB writes will fail silently (caught + logged).
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var hub = new StreamHub(Options.Create(new JsonOptions()));
        var logger = new LoggerFactory().CreateLogger<PlayerHistoryService>();
        return new PlayerHistoryService(scopeFactory, hub, logger);
    }

    [Fact]
    public void Join_UpsertsPlayer_ReadableFromGetRoster()
    {
        var history = NewService();
        var since = DateTimeOffset.UtcNow;

        history.Join("factorio-1", sessionKey: "76561198000000000", id: "76561198000000000",
            name: "Heisen", addr: null, since);

        RosterPlayer p = Assert.Single(history.GetRoster("factorio-1"));
        Assert.Equal("76561198000000000", p.PlayerIdentity);
        Assert.Equal("Heisen", p.PlayerName);
        Assert.Equal("76561198000000000", p.PlayerId);
        Assert.Null(p.PlayerAddr);
        Assert.Equal(PlayerStatus.online, p.Status);
        Assert.Equal(since, p.FirstSeen);
    }

    [Fact]
    public void Join_SamePlayerTwice_UpsertsInPlace_NotADuplicate()
    {
        var history = NewService();
        history.Join("valheim-1", "651023867:1", null, "Test", null, DateTimeOffset.UtcNow);
        history.Join("valheim-1", "651023867:1", null, "Test", null, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Single(history.GetRoster("valheim-1"));
    }

    [Theory]
    [InlineData(null, "1.2.3.4:9999", "id-1", "name-1", "id-1")]       // id wins
    [InlineData(null, null, "id-1", "name-1", "id-1")]                  // only id
    [InlineData(null, null, null, "name-1", "name-1")]                  // only name
    [InlineData("key-1", "1.2.3.4:9999", "id-1", "name-1", "id-1")]    // id wins over all
    [InlineData("key-1", "1.2.3.4:9999", null, "name-1", "1.2.3.4:9999")] // addr wins over name
    public void Join_PlayerIdentity_UsesStableId(
        string? sessionKey, string? addr, string? id, string? name, string expectedIdentity)
    {
        var history = NewService();
        history.Join("core-keeper-1", sessionKey, id, name, addr, DateTimeOffset.UtcNow);

        RosterPlayer p = Assert.Single(history.GetRoster("core-keeper-1"));
        Assert.Equal(expectedIdentity, p.PlayerIdentity);
    }

    [Fact]
    public void Join_NoIdentityAtAll_UsesFallback()
    {
        var history = NewService();
        history.Join("romestead-1", sessionKey: null, id: null, name: null, addr: null, DateTimeOffset.UtcNow);

        RosterPlayer p = Assert.Single(history.GetRoster("romestead-1"));
        Assert.Equal("unknown", p.PlayerIdentity);
    }

    [Fact]
    public void Leave_SetsStatusOffline_KeepsRecord()
    {
        var history = NewService();
        var join = DateTimeOffset.UtcNow.AddMinutes(-5);
        var leave = DateTimeOffset.UtcNow;
        history.Join("factorio-1", "sess-1", "id-1", "Heisen", null, join);
        Assert.Single(history.GetRoster("factorio-1"));

        history.Leave("factorio-1", "sess-1", "id-1", "Heisen", null, leave);

        RosterPlayer p = Assert.Single(history.GetRoster("factorio-1"));
        Assert.Equal(PlayerStatus.offline, p.Status);
        Assert.Equal(join, p.FirstSeen);
        Assert.Equal(leave, p.LastSeen);
    }

    [Fact]
    public void Leave_MapMiss_CreatesRecordWithOfflineStatus()
    {
        // In the permanent roster, a leave for a player we haven't seen before creates a record
        // with status "offline" — the player was offline before they were first seen.
        var history = NewService();
        history.Leave("factorio-1", "never-joined", "id-9", "Ghost", null, DateTimeOffset.UtcNow);

        RosterPlayer p = Assert.Single(history.GetRoster("factorio-1"));
        Assert.Equal("id-9", p.PlayerIdentity);
        Assert.Equal(PlayerStatus.offline, p.Status);
    }

    [Fact]
    public void Leave_ThenRejoin_SetsOnline_KeepsFirstSeen()
    {
        var history = NewService();
        var t0 = DateTimeOffset.UtcNow;
        history.Join("factorio-1", "sess-1", "id-1", "Heisen", null, t0);
        history.Leave("factorio-1", "sess-1", "id-1", "Heisen", null, t0.AddMinutes(1));
        history.Join("factorio-1", "sess-1", "id-1", "Heisen", null, t0.AddMinutes(2));

        RosterPlayer p = Assert.Single(history.GetRoster("factorio-1"));
        Assert.Equal(PlayerStatus.online, p.Status);
        Assert.Equal(t0, p.FirstSeen); // FirstSeen preserved from first join
        Assert.Equal(t0.AddMinutes(2), p.LastSeen);
    }

    [Fact]
    public void Reset_SetsAllOffline_OtherServersUnaffected()
    {
        var history = NewService();
        history.Join("factorio-1", "sess-1", "id-1", "A", null, DateTimeOffset.UtcNow);
        history.Join("factorio-1", "sess-2", "id-2", "B", null, DateTimeOffset.UtcNow);
        history.Join("valheim-1", "sess-3", "id-3", "C", null, DateTimeOffset.UtcNow);

        history.Reset("factorio-1");

        RosterPlayer[] factorio = history.GetRoster("factorio-1").ToArray();
        Assert.Equal(2, factorio.Length);
        Assert.All(factorio, p => Assert.Equal(PlayerStatus.offline, p.Status));

        RosterPlayer valheim = Assert.Single(history.GetRoster("valheim-1"));
        Assert.Equal(PlayerStatus.online, valheim.Status);
    }

    [Fact]
    public void Ban_SetsStatusBanned_StoresReason()
    {
        var history = NewService();
        var t0 = DateTimeOffset.UtcNow;
        history.Join("factorio-1", "sess-1", "id-1", "Heisen", null, t0);

        history.Ban("factorio-1", "id-1", "griefing", t0.AddMinutes(5));

        RosterPlayer p = Assert.Single(history.GetRoster("factorio-1"));
        Assert.Equal(PlayerStatus.banned, p.Status);
        Assert.Equal("griefing", p.BanReason);
    }

    [Fact]
    public void GetRoster_OrdersByStatus_ThenByLastSeen()
    {
        var history = NewService();
        var t0 = DateTimeOffset.UtcNow;

        // Online player (most recent)
        history.Join("factorio-1", "sess-1", "id-1", "Online1", null, t0.AddMinutes(3));
        // Offline player (older)
        history.Join("factorio-1", "sess-2", "id-2", "Offline1", null, t0);
        history.Leave("factorio-1", "sess-2", "id-2", "Offline1", null, t0.AddMinutes(1));
        // Online player (less recent)
        history.Join("factorio-1", "sess-3", "id-3", "Online2", null, t0.AddMinutes(1));

        RosterPlayer[] all = history.GetRoster("factorio-1").ToArray();
        Assert.Equal(3, all.Length);
        // Online players first, ordered by lastSeen descending
        Assert.Equal("Online1", all[0].PlayerName);
        Assert.Equal("Online2", all[1].PlayerName);
        // Offline player last
        Assert.Equal("Offline1", all[2].PlayerName);
    }

    [Fact]
    public void GetRoster_UnknownServer_ReturnsEmpty_NeverNull()
    {
        var history = NewService();
        Assert.NotNull(history.GetRoster("never-heard-of-it"));
        Assert.Empty(history.GetRoster("never-heard-of-it"));
    }

    [Fact]
    public void GetStatus_UnknownPlayer_ReturnsUnknown()
    {
        var history = NewService();
        Assert.Equal(PlayerStatus.unknown, history.GetStatus("factorio-1", "nobody"));
    }

    [Fact]
    public void GetStatus_KnownPlayer_ReturnsCurrentStatus()
    {
        var history = NewService();
        history.Join("factorio-1", "sess-1", "id-1", "Heisen", null, DateTimeOffset.UtcNow);

        Assert.Equal(PlayerStatus.online, history.GetStatus("factorio-1", "id-1"));
    }
}
