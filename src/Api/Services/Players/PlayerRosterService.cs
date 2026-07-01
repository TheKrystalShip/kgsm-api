using System.Collections.Concurrent;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;

namespace TheKrystalShip.Api.Services.Players;

/// <summary>
/// The in-memory session-level roster projection — a fast cache for session dedup and the
/// join/leave semantics the watchdog emits. The authority is
/// <see cref="PlayerHistoryService"/> (DB-backed); this service is called FROM
/// <see cref="Audit.KgsmAuditConsumer"/>'s event handlers for session-level dedup, but the
/// controller reads from the history service. Does NOT publish WS frames — the history
/// service publishes all roster WS frames.
/// </summary>
/// <remarks>
/// <para><b>Composed, not independent.</b> This service does NOT register its own
/// <c>IEventService</c> handler — kgsm-lib's <c>EventService</c> keeps only ONE handler per
/// event type (dictionary indexer); a second registration would silently replace the audit
/// consumer's handler. So this service is called <em>from</em> the existing handlers, composed,
/// not registered independently.</para>
/// <para><b>Reset on instance stop/start.</b> A server restart invalidates every prior session.
/// <see cref="Reset"/> is called from the same <c>InstanceStartedData</c>/<c>InstanceStoppedData</c>
/// handlers <see cref="Audit.KgsmAuditConsumer"/> already has.</para>
/// </remarks>
public sealed class PlayerRosterService
{
    // serverId -> (sessionKey -> entry). ConcurrentDictionary at both levels: joins/leaves arrive off the
    // event listener's background task, and GetRoster is read from a request thread — no external lock.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RosterPlayer>> _rosters = new();

    /// <summary>Upsert a session on <c>player.join</c>. Returns the resolved player identity for
    /// downstream consumers (the history service) to key on.</summary>
    public string? Join(string serverId, string? sessionKey, string? id, string? name, string? addr, DateTimeOffset since)
    {
        string? key = ResolveKey(sessionKey, id, name, addr);
        if (string.IsNullOrEmpty(serverId) || key is null) return null;

        string playerIdentity = ResolvePlayerIdentity(id, addr, name, sessionKey);
        var roster = _rosters.GetOrAdd(serverId, static _ => new ConcurrentDictionary<string, RosterPlayer>());
        var player = new RosterPlayer(playerIdentity, id, name, addr, PlayerStatus.online, since, since, null);
        roster[key] = player;

        return playerIdentity;
    }

    /// <summary>Evict a session on <c>player.leave</c>. Returns the resolved player identity and
    /// the player record for downstream consumers.</summary>
    public (string PlayerIdentity, RosterPlayer Player)? Leave(string serverId, string? sessionKey, string? id, string? name, string? addr, DateTimeOffset at)
    {
        string? key = ResolveKey(sessionKey, id, name, addr);
        if (string.IsNullOrEmpty(serverId) || key is null) return null;
        if (!_rosters.TryGetValue(serverId, out var roster)) return null;

        string playerIdentity = ResolvePlayerIdentity(id, addr, name, sessionKey);
        RosterPlayer last = roster.TryRemove(key, out RosterPlayer? existing)
            ? existing
            : new RosterPlayer(playerIdentity, id, name, addr, PlayerStatus.offline, at, at, null);

        return (playerIdentity, last);
    }

    /// <summary>Clear a server's roster wholesale — called on that instance's start, stop, AND restart.</summary>
    public void Reset(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return;
        _rosters.TryRemove(serverId, out _);
    }

    /// <summary>The current roster for one server, oldest-session-first.</summary>
    public IReadOnlyList<RosterPlayer> GetRoster(string serverId)
    {
        if (string.IsNullOrEmpty(serverId) || !_rosters.TryGetValue(serverId, out var roster))
            return [];
        return roster.Values.OrderBy(p => p.FirstSeen).ToArray();
    }

    /// <summary>Resolve the session key (first non-blank of key, addr, id, name) — the same
    /// precedence the watchdog uses.</summary>
    private static string? ResolveKey(string? sessionKey, string? id, string? name, string? addr) =>
        !string.IsNullOrEmpty(sessionKey) ? sessionKey
        : !string.IsNullOrEmpty(addr) ? addr
        : !string.IsNullOrEmpty(id) ? id
        : !string.IsNullOrEmpty(name) ? name
        : null;

    /// <summary>Resolve the stable player identity (first non-blank of id, addr, name, sessionKey).
    /// Deliberately different from ResolveKey: the player identity prioritizes the stable account id.</summary>
    private static string ResolvePlayerIdentity(string? id, string? addr, string? name, string? sessionKey) =>
        !string.IsNullOrEmpty(id) ? id
        : !string.IsNullOrEmpty(addr) ? addr
        : !string.IsNullOrEmpty(name) ? name
        : !string.IsNullOrEmpty(sessionKey) ? sessionKey
        : "unknown";
}
