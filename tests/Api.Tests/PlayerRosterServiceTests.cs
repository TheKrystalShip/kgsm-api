using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Players;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Pure projection coverage (no I/O, no socket) for the live roster (player-presence-contract.md §5) —
/// join upsert, leave remove, the map-miss no-op, the session-key fallback precedence, and the
/// stop/start reset. A real <see cref="StreamHub"/> is used with zero connections registered, so
/// <c>Publish</c> is exercised (no exception) but does no actual send — the WS wire shape itself is
/// proven once elsewhere (<c>ConsoleBridgeTests</c>'s precedent), this file is about the projection's
/// STATE.
/// </summary>
public sealed class PlayerRosterServiceTests
{
    private static PlayerRosterService NewService() => new(new StreamHub(
        Options.Create(new JsonOptions())));

    [Fact]
    public void Join_UpsertsSession_ReadableFromGetRoster()
    {
        var roster = NewService();
        var since = DateTimeOffset.UtcNow;

        roster.Join("factorio-1", sessionKey: "76561198000000000", id: "76561198000000000",
            name: "Heisen", addr: null, since);

        RosterPlayer p = Assert.Single(roster.GetRoster("factorio-1"));
        Assert.Equal("76561198000000000", p.SessionKey);
        Assert.Equal("Heisen", p.Name);
        Assert.Equal("76561198000000000", p.Id);
        Assert.Null(p.Addr);
        Assert.Equal(since, p.Since);
    }

    [Fact]
    public void Join_SameSessionKeyTwice_UpsertsInPlace_NotADuplicate()
    {
        // Mirrors the watchdog's own insert-if-absent semantics being harmless if a doubled join line
        // ever reached here (the map already dedups upstream; this is a defensive re-assertion, not a
        // second entry).
        var roster = NewService();
        roster.Join("valheim-1", "651023867:1", null, "Test", null, DateTimeOffset.UtcNow);
        roster.Join("valheim-1", "651023867:1", null, "Test", null, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Single(roster.GetRoster("valheim-1"));
    }

    [Theory]
    [InlineData(null, "1.2.3.4:9999", "id-1", "name-1", "1.2.3.4:9999")]     // sessionKey missing -> addr
    [InlineData(null, null, "id-1", "name-1", "id-1")]                       // sessionKey+addr missing -> id
    [InlineData(null, null, null, "name-1", "name-1")]                      // only name -> name
    [InlineData("key-1", "1.2.3.4:9999", "id-1", "name-1", "key-1")]         // sessionKey present -> wins
    public void Join_SessionKeyFallback_MatchesContractPrecedence(
        string? sessionKey, string? addr, string? id, string? name, string expectedKey)
    {
        // The contract's derived session_key = key ?? addr ?? id ?? name. SessionKey is nullable only to
        // mirror the kgsm-lib DTO style (contract §3) — the real wire always sends one; this fallback is
        // defensive.
        var roster = NewService();
        roster.Join("core-keeper-1", sessionKey, id, name, addr, DateTimeOffset.UtcNow);

        RosterPlayer p = Assert.Single(roster.GetRoster("core-keeper-1"));
        Assert.Equal(expectedKey, p.SessionKey);
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
        // The honest fallback (player-presence-contract.md §5/plan §"honesty boundaries"): a watchdog- or
        // api-restart-mid-session leave that never had a matching join is a silent no-op, not an error.
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
        Assert.Equal("sess-2", remaining.SessionKey);
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
        Assert.Single(roster.GetRoster("valheim-1")); // untouched — reset is per-server
    }

    [Fact]
    public void Reset_OnRestart_ClearsPhantomSessions_ScopedPerServer()
    {
        // A restart is its own distinct kgsm event (InstanceRestartedData), never a stop+start pair, and
        // the underlying process dies without emitting per-player "left" lines — so KgsmAuditConsumer
        // resets this server's roster from THAT handler too (player-presence-contract.md §5, amended to
        // "stop/start/restart"). Modeled here at the service level exactly as the consumer drives it: a
        // bare Reset call, no accompanying Leave events.
        var roster = NewService();
        roster.Join("factorio-1", "sess-1", "id-1", "A", null, DateTimeOffset.UtcNow);
        roster.Join("factorio-1", "sess-2", "id-2", "B", null, DateTimeOffset.UtcNow);
        roster.Join("valheim-1", "sess-3", "id-3", "C", null, DateTimeOffset.UtcNow);

        roster.Reset("factorio-1"); // the restart handler's call — no matching Leave events precede it

        Assert.Empty(roster.GetRoster("factorio-1")); // no phantom "connected" sessions survive the restart
        Assert.Single(roster.GetRoster("valheim-1"));  // an unrelated server's roster is untouched
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
        Assert.Equal("sess-1", all[0].SessionKey);
        Assert.Equal("sess-2", all[1].SessionKey);
    }
}
