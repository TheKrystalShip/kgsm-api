using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Players;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Pure projection coverage (no I/O, no socket) for the in-memory session-level roster
/// (PlayerRosterService) — join upsert, leave evict, session-key fallback precedence, and the
/// stop/start reset. The history service (PlayerHistoryService) is the authority; this service
/// is the fast session-level dedup cache.
/// </summary>
public sealed class PlayerRosterServiceTests
{
    private static PlayerRosterService NewService() => new();

    [Fact]
    public void Join_UpsertsSession_ReadableFromGetRoster()
    {
        var roster = NewService();
        var since = DateTimeOffset.UtcNow;

        string? identity = roster.Join("factorio-1", sessionKey: "76561198000000000", id: "76561198000000000",
            name: "Heisen", addr: null, since);

        Assert.Equal("76561198000000000", identity);

        RosterPlayer p = Assert.Single(roster.GetRoster("factorio-1"));
        Assert.Equal("76561198000000000", p.PlayerIdentity);
        Assert.Equal("Heisen", p.PlayerName);
        Assert.Equal("76561198000000000", p.PlayerId);
        Assert.Null(p.PlayerAddr);
        Assert.Equal(PlayerStatus.online, p.Status);
        Assert.Equal(since, p.FirstSeen);
    }

    [Fact]
    public void Join_SameSessionKeyTwice_UpsertsInPlace_NotADuplicate()
    {
        var roster = NewService();
        roster.Join("valheim-1", "651023867:1", null, "Test", null, DateTimeOffset.UtcNow);
        roster.Join("valheim-1", "651023867:1", null, "Test", null, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Single(roster.GetRoster("valheim-1"));
    }

    [Theory]
    [InlineData(null, "1.2.3.4:9999", "id-1", "name-1", "id-1")]       // sessionKey missing -> identity from id
    [InlineData(null, null, "id-1", "name-1", "id-1")]                  // sessionKey+addr missing -> id
    [InlineData(null, null, null, "name-1", "name-1")]                  // only name -> name
    [InlineData("key-1", "1.2.3.4:9999", "id-1", "name-1", "id-1")]    // sessionKey present -> identity from id (player-level)
    public void Join_PlayerIdentity_UsesStableId(
        string? sessionKey, string? addr, string? id, string? name, string expectedIdentity)
    {
        // PlayerIdentity resolves as: id ?? addr ?? name ?? sessionKey — deliberately different
        // from the session-level sessionKey (key ?? addr ?? id ?? name).
        var roster = NewService();
        roster.Join("core-keeper-1", sessionKey, id, name, addr, DateTimeOffset.UtcNow);

        RosterPlayer p = Assert.Single(roster.GetRoster("core-keeper-1"));
        Assert.Equal(expectedIdentity, p.PlayerIdentity);
    }

    [Fact]
    public void Join_NoIdentityAtAll_Dropped_NeverFabricatesAKey()
    {
        var roster = NewService();
        roster.Join("romestead-1", sessionKey: null, id: null, name: null, addr: null, DateTimeOffset.UtcNow);

        Assert.Empty(roster.GetRoster("romestead-1"));
    }

    [Fact]
    public void Leave_RemovesSession()
    {
        var roster = NewService();
        roster.Join("factorio-1", "sess-1", "id-1", "Heisen", null, DateTimeOffset.UtcNow);
        Assert.Single(roster.GetRoster("factorio-1"));

        roster.Leave("factorio-1", "sess-1", "id-1", "Heisen", null, DateTimeOffset.UtcNow);

        Assert.Empty(roster.GetRoster("factorio-1"));
    }

    [Fact]
    public void Leave_MapMiss_NoThrow_RemainsEmpty()
    {
        var roster = NewService();
        roster.Leave("factorio-1", "never-joined", "id-9", "Ghost", null, DateTimeOffset.UtcNow);

        Assert.Empty(roster.GetRoster("factorio-1"));
    }

    [Fact]
    public void Leave_UnrelatedSessionUnaffected()
    {
        var roster = NewService();
        roster.Join("factorio-1", "sess-1", "id-1", "A", null, DateTimeOffset.UtcNow);
        roster.Join("factorio-1", "sess-2", "id-2", "B", null, DateTimeOffset.UtcNow);

        roster.Leave("factorio-1", "sess-1", "id-1", "A", null, DateTimeOffset.UtcNow);

        RosterPlayer remaining = Assert.Single(roster.GetRoster("factorio-1"));
        Assert.Equal("id-2", remaining.PlayerIdentity);
    }

    [Fact]
    public void Reset_ClearsWholeRoster_OtherServersUnaffected()
    {
        var roster = NewService();
        roster.Join("factorio-1", "sess-1", "id-1", "A", null, DateTimeOffset.UtcNow);
        roster.Join("factorio-1", "sess-2", "id-2", "B", null, DateTimeOffset.UtcNow);
        roster.Join("valheim-1", "sess-3", "id-3", "C", null, DateTimeOffset.UtcNow);

        roster.Reset("factorio-1");

        Assert.Empty(roster.GetRoster("factorio-1"));
        Assert.Single(roster.GetRoster("valheim-1"));
    }

    [Fact]
    public void Reset_OnRestart_ClearsPhantomSessions_ScopedPerServer()
    {
        var roster = NewService();
        roster.Join("factorio-1", "sess-1", "id-1", "A", null, DateTimeOffset.UtcNow);
        roster.Join("factorio-1", "sess-2", "id-2", "B", null, DateTimeOffset.UtcNow);
        roster.Join("valheim-1", "sess-3", "id-3", "C", null, DateTimeOffset.UtcNow);

        roster.Reset("factorio-1");

        Assert.Empty(roster.GetRoster("factorio-1"));
        Assert.Single(roster.GetRoster("valheim-1"));
    }

    [Fact]
    public void GetRoster_UnknownServer_ReturnsEmpty_NeverNull()
    {
        var roster = NewService();
        Assert.NotNull(roster.GetRoster("never-heard-of-it"));
        Assert.Empty(roster.GetRoster("never-heard-of-it"));
    }

    [Fact]
    public void GetRoster_OrdersOldestSessionFirst()
    {
        var roster = NewService();
        var t0 = DateTimeOffset.UtcNow;
        roster.Join("factorio-1", "sess-2", null, "Second", null, t0.AddSeconds(5));
        roster.Join("factorio-1", "sess-1", null, "First", null, t0);

        RosterPlayer[] all = roster.GetRoster("factorio-1").ToArray();
        Assert.Equal(2, all.Length);
        Assert.Equal("First", all[0].PlayerIdentity);
        Assert.Equal("Second", all[1].PlayerIdentity);
    }
}
