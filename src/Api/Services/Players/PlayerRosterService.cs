using System.Collections.Concurrent;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;

namespace TheKrystalShip.Api.Services.Players;

/// <summary>
/// The live roster of record (player-presence-contract.md §5, §6·4) — an in-memory projection of the
/// <c>player.join</c>/<c>player.leave</c> event stream, keyed on <c>(serverId, sessionKey)</c>. Driven
/// exclusively by <see cref="Audit.KgsmAuditConsumer"/>'s event handlers (never registers its own
/// <c>IEventService</c> handler — kgsm-lib's <c>EventService</c> keeps only ONE handler per event
/// <em>type</em>, keyed by a plain dictionary indexer; a second <c>RegisterHandler&lt;InstancePlayerJoinedData&gt;</c>
/// call from a second consumer would silently REPLACE the audit consumer's handler, not add to it. So this
/// service is called <em>from</em> the existing handlers, composed, not registered independently).
/// </summary>
/// <remarks>
/// <para><b>v1 limitation — honest, not a bug.</b> The roster is purely in-memory and rebuilds from the
/// live stream: after an api restart it reflects only sessions observed since the restart (the durable
/// audit log still has the historical <c>player.join</c>/<c>player.leave</c> rows, but this projection
/// does not replay them). A player who joined before the restart and never left will not appear until
/// they leave and rejoin. Acceptable per the frozen contract (§6 v1 scope) — not silently "fixed" by
/// guessing a stale roster back into existence.</para>
/// <para><b>Reset on instance stop/start.</b> A server restart invalidates every prior session (a fresh
/// log = a fresh <c>EventChannelTail</c> inode on the watchdog side, per the contract's map-reset rule);
/// <see cref="Reset"/> is called from the SAME <c>InstanceStartedData</c>/<c>InstanceStoppedData</c>
/// handlers <see cref="Audit.KgsmAuditConsumer"/> already has, for the identical single-handler-per-type
/// reason above.</para>
/// </remarks>
public sealed class PlayerRosterService(StreamHub hub)
{
    // serverId -> (sessionKey -> entry). ConcurrentDictionary at both levels: joins/leaves arrive off the
    // event listener's background task, and GetRoster is read from a request thread — no external lock.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RosterPlayer>> _rosters = new();

    /// <summary>Upsert a session on <c>player.join</c>. <paramref name="sessionKey"/> is nullable only to
    /// mirror the kgsm-lib DTO's declared style (contract §3) — the real wire always sends a non-empty
    /// value; if it is ever missing this falls back to <paramref name="id"/> then <paramref name="name"/>
    /// (the same <c>key ?? addr ?? id ?? name</c> precedence the watchdog uses), and drops the join (logged
    /// by the caller, not here — this service stays a pure projection) only when NONE identify the
    /// session — never fabricates a key.</summary>
    public void Join(string serverId, string? sessionKey, string? id, string? name, string? addr, DateTimeOffset since)
    {
        string? key = ResolveKey(sessionKey, id, name, addr);
        if (string.IsNullOrEmpty(serverId) || key is null) return;

        var roster = _rosters.GetOrAdd(serverId, static _ => new ConcurrentDictionary<string, RosterPlayer>());
        var player = new RosterPlayer(key, name, id, addr, since);
        roster[key] = player;

        hub.Publish(StreamProtocol.PlayersTopic, StreamProtocol.PlayerEntityKey(serverId, key),
            new StreamMessage(StreamProtocol.PlayersTopic, StreamProtocol.PlayersJoin,
                new PlayerTransition(serverId, player)));
    }

    /// <summary>Evict a session on <c>player.leave</c>. A map miss (e.g. the api itself restarted
    /// mid-session — the v1 limitation above) is a silent no-op: there is nothing to remove, and that is
    /// honest, not an error.</summary>
    public void Leave(string serverId, string? sessionKey, string? id, string? name, string? addr, DateTimeOffset at)
    {
        string? key = ResolveKey(sessionKey, id, name, addr);
        if (string.IsNullOrEmpty(serverId) || key is null) return;
        if (!_rosters.TryGetValue(serverId, out var roster)) return;

        // Prefer the session's own last-known state (name/id/addr as seen at join) for the leave payload;
        // fall back to what THIS leave event carried when the join was never seen (map miss).
        RosterPlayer last = roster.TryRemove(key, out RosterPlayer? existing)
            ? existing
            : new RosterPlayer(key, name, id, addr, at);

        hub.Publish(StreamProtocol.PlayersTopic, StreamProtocol.PlayerEntityKey(serverId, key),
            new StreamMessage(StreamProtocol.PlayersTopic, StreamProtocol.PlayersLeave,
                new PlayerTransition(serverId, last)));
    }

    /// <summary>Clear a server's roster wholesale — called on that instance's start, stop, AND restart (a
    /// fresh server session invalidates every prior one; see the remarks above). Pushes a single
    /// <c>players.reset</c> frame (<c>{ serverId }</c>) rather than a per-session <c>players.leave</c>
    /// burst — those underlying sessions vanish without emitting their own leave lines, so there is
    /// nothing to enumerate; the client just drops everything it holds for this server. Skipped when the
    /// roster was already empty — clearing nothing is not an event worth signaling.</summary>
    public void Reset(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return;
        if (!_rosters.TryRemove(serverId, out var removed) || removed.IsEmpty) return;

        hub.Publish(StreamProtocol.PlayersTopic, StreamProtocol.PlayerResetEntityKey(serverId),
            new StreamMessage(StreamProtocol.PlayersTopic, StreamProtocol.PlayersReset,
                new PlayerReset(serverId)));
    }

    /// <summary>The current roster for one server, oldest-session-first. Empty (never null) for an unknown
    /// or currently-empty server — the caller (<c>ServerPlayersController</c>) is the one that decides
    /// <c>configured</c> vs <c>unknown</c> off the instance's detection regexes, not this projection.</summary>
    public IReadOnlyList<RosterPlayer> GetRoster(string serverId)
    {
        if (string.IsNullOrEmpty(serverId) || !_rosters.TryGetValue(serverId, out var roster))
            return [];
        return roster.Values.OrderBy(p => p.Since).ToArray();
    }

    private static string? ResolveKey(string? sessionKey, string? id, string? name, string? addr) =>
        !string.IsNullOrEmpty(sessionKey) ? sessionKey
        : !string.IsNullOrEmpty(addr) ? addr
        : !string.IsNullOrEmpty(id) ? id
        : !string.IsNullOrEmpty(name) ? name
        : null;
}

/// <summary>The <c>players.join</c>/<c>players.leave</c> WS payload: <c>{ serverId, player }</c>.</summary>
public sealed record PlayerTransition(string ServerId, RosterPlayer Player);

/// <summary>The <c>players.reset</c> WS payload: <c>{ serverId }</c> — no per-session data, the client
/// drops its whole roster for this server.</summary>
public sealed record PlayerReset(string ServerId);
