using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Players;

/// <summary>
/// The permanent player roster — DB-backed authority for <c>GET /servers/{id}/players</c> and
/// all roster WS frames. Maintains an in-memory cache for fast reads and publishes WS frames
/// on every status change. Once a player connects they are never removed; their
/// <see cref="PlayerStatus"/> toggles between online/offline/banned/unknown as events arrive.
/// </summary>
/// <remarks>
/// <para><b>Composed, not independent.</b> Like <see cref="PlayerRosterService"/>, this service
/// does NOT register its own <c>IEventService</c> handler — it is called FROM
/// <see cref="Audit.KgsmAuditConsumer"/>'s existing handlers for the single-handler-per-type reason.</para>
/// <para><b>Reconcile on startup.</b> On API startup the watchdog's live session map says who is
/// connected, joined against what the engine says is running: players in the snapshot are marked
/// online, everyone else offline, and a snapshot entry for a server the engine reports stopped is
/// treated as ended rather than believed. Presence and run-state are two readings from two
/// authorities, and combining them is the only honest answer — a session map is in-memory
/// bookkeeping, so its word alone cannot establish that someone is connected to a process that is
/// not running. With no snapshot at all (watchdog absent or down), an online player resolves to
/// offline where the server is measurably stopped and to unknown everywhere else.</para>
/// <para><b>Write pattern.</b> Follows the <see cref="Audit.AuditService"/> pattern: singleton,
/// own DI scope per write, serialized writes via <see cref="SemaphoreSlim"/> (SQLite single-writer),
/// <c>EnsureCreated</c> with double-checked locking.</para>
/// </remarks>
public sealed class PlayerHistoryService(
    IServiceScopeFactory scopeFactory,
    IServiceProvider serviceProvider,
    StreamHub hub,
    InstanceCache instances,
    ILogger<PlayerHistoryService> logger)
{
    // serverId -> (playerIdentity -> player). In-memory cache for fast reads + WS coalescing.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RosterPlayer>> _cache = new();

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <summary>Reconcile player statuses from the watchdog's live session map on API startup.
    /// Queries the watchdog for currently connected players, marks them online, and marks
    /// everyone else offline. If the watchdog is absent/down, falls back to marking unknown.</summary>
    public async Task ReconcileFromWatchdogAsync(CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        // One-time re-key: collapse rows minted under the old addr-first identity onto the
        // name-first person key, merging reconnect-duplicates. Runs before the watchdog reconcile
        // so the merged rows are what gets marked online/offline below. Idempotent.
        await MergeDuplicatesAsync(ct).ConfigureAwait(false);

        // Try to get the watchdog's live session map.
        IWatchdogClient? watchdog = serviceProvider.GetService(typeof(IWatchdogClient)) as IWatchdogClient;
        if (watchdog is null)
        {
            logger.LogInformation("Player history: watchdog not provisioned — falling back to unknown on startup");
            await MarkUnknownFallbackAsync(ct).ConfigureAwait(false);
            return;
        }

        IReadOnlyDictionary<string, WatchdogInstancePresence>? watchdogSessions;
        try
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probe.Token);
            watchdogSessions = await watchdog.GetPlayerPresenceAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Player history: watchdog session query failed — falling back to unknown");
            await MarkUnknownFallbackAsync(ct).ConfigureAwait(false);
            return;
        }

        if (watchdogSessions is null)
        {
            logger.LogInformation("Player history: watchdog returned null — falling back to unknown on startup");
            await MarkUnknownFallbackAsync(ct).ConfigureAwait(false);
            return;
        }

        // Build the set of currently-online player identities from the watchdog snapshot.
        // Indexed by instance name → set of all identity fields for cross-matching.
        //
        // A server the engine reports as stopped contributes nothing, whatever its entry says: a
        // process that is not running has no connections, so a session it still names describes
        // something that cannot exist. This is the same status-from-the-authority join every other
        // surface makes (keystone §4) rather than a second opinion about presence — and it matters
        // most here, because this method writes to the PERMANENT roster: an unjoined snapshot is how
        // a stale session became a durable "online" that outlived several restarts.
        var onlineByInstance = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var liveSessions = new Dictionary<string, IReadOnlyList<WatchdogPlayer>>(StringComparer.Ordinal);
        foreach (var kvp in watchdogSessions)
        {
            IReadOnlyList<WatchdogPlayer> sessions = kvp.Value.Players;

            if (IsMeasuredStopped(kvp.Key))
            {
                if (sessions.Count > 0)
                    logger.LogWarning(
                        "Player history: watchdog reports {Count} session(s) for {Server}, which the engine "
                        + "reports as stopped — treating them as ended", sessions.Count, kvp.Key);
                continue;
            }

            liveSessions[kvp.Key] = sessions;
            var online = new HashSet<string>(StringComparer.Ordinal);
            foreach (var session in sessions)
            {
                if (session.SessionKey is not null) online.Add(session.SessionKey);
                if (session.Id is not null) online.Add(session.Id);
                if (session.Name is not null) online.Add(session.Name);
                if (session.Addr is not null) online.Add(session.Addr);
            }
            onlineByInstance[kvp.Key] = online;
        }

        // Load full roster from DB.
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PlayerRecord[] all = await db.PlayerHistory.ToArrayAsync(ct).ConfigureAwait(false);

        int markedOnline = 0;
        int markedOffline = 0;
        int newlyDiscovered = 0;

        // Phase 1: reconcile existing DB records.
        foreach (PlayerRecord record in all)
        {
            // Never override banned — that's an intentional, manual status.
            if (record.Status == PlayerStatus.banned)
                continue;

            if (onlineByInstance.TryGetValue(record.ServerId, out var online))
            {
                bool isOnline = online.Contains(record.PlayerIdentity)
                    || (record.PlayerId is not null && online.Contains(record.PlayerId))
                    || (record.PlayerName is not null && online.Contains(record.PlayerName))
                    || (record.PlayerAddr is not null && online.Contains(record.PlayerAddr));

                if (isOnline && record.Status != PlayerStatus.online)
                {
                    record.Status = PlayerStatus.online;
                    record.LastSeen = DateTimeOffset.UtcNow;
                    markedOnline++;
                }
                else if (!isOnline && record.Status == PlayerStatus.online)
                {
                    record.Status = PlayerStatus.offline;
                    markedOffline++;
                }
            }
            else if (record.Status == PlayerStatus.online)
            {
                // Instance not in watchdog snapshot — mark offline.
                record.Status = PlayerStatus.offline;
                markedOffline++;
            }
        }

        // Phase 2: discover new players from watchdog that aren't in the DB yet. Only from the sessions
        // that survived the run-state join — minting a brand-new online row for a stopped server would
        // be inventing a player, not recovering one.
        foreach (var kvp in liveSessions)
        {
            string serverId = kvp.Key;
            foreach (var session in kvp.Value)
            {
                if (session.SessionKey is null) continue;

                bool exists = all.Any(r => r.ServerId == serverId
                    && (r.PlayerIdentity == session.SessionKey
                        || (session.Id is not null && r.PlayerId == session.Id)
                        || (session.Name is not null && r.PlayerName == session.Name)
                        || (session.Addr is not null && r.PlayerAddr == session.Addr)));

                if (!exists)
                {
                    string playerIdentity = PlayerIdentityResolver.Resolve(session.Id, session.Name, session.Addr, session.SessionKey);
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    db.PlayerHistory.Add(new PlayerRecord
                    {
                        ServerId = serverId,
                        PlayerIdentity = playerIdentity,
                        PlayerId = session.Id,
                        PlayerName = session.Name,
                        PlayerAddr = session.Addr,
                        Status = PlayerStatus.online,
                        FirstSeen = now,
                        LastSeen = now,
                        BanReason = null
                    });
                    newlyDiscovered++;
                }
            }
        }

        if (markedOnline > 0 || markedOffline > 0 || newlyDiscovered > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "Player history: reconciled from watchdog — {Online} marked online, {Offline} marked offline, {New} newly discovered",
                markedOnline, markedOffline, newlyDiscovered);
        }

        // Rebuild the in-memory cache from DB.
        await RebuildCacheAsync(ct).ConfigureAwait(false);

        // Publish WS frames for newly discovered online players so open tabs update.
        foreach (var kvp in liveSessions)
        {
            string serverId = kvp.Key;
            foreach (var session in kvp.Value)
            {
                if (session.SessionKey is null) continue;
                string playerIdentity = PlayerIdentityResolver.Resolve(session.Id, session.Name, session.Addr, session.SessionKey);

                if (_cache.TryGetValue(serverId, out var roster) && roster.TryGetValue(playerIdentity, out var player) && player.Status == PlayerStatus.online)
                {
                    hub.Publish(StreamProtocol.PlayersTopic, StreamProtocol.PlayerEntityKey(serverId, playerIdentity),
                        new StreamMessage(StreamProtocol.PlayersTopic, StreamProtocol.PlayersJoin,
                            new PlayerTransition(serverId, player)));
                }
            }
        }
    }

    /// <summary>
    /// Whether the engine reports this server as stopped — a measured "there is no process", not an
    /// absent or unreadable status. An unmeasured reading answers <see langword="false"/>, so an
    /// engine this API cannot read leaves presence exactly as it found it rather than declaring
    /// everyone gone on a reading it does not have.
    /// </summary>
    private bool IsMeasuredStopped(string serverId) =>
        instances.Statuses.TryGetValue(serverId, out Reading<InstanceRuntimeStatus>? reading)
        && reading is { IsMeasured: true, Value.Status: false };

    /// <summary>Resolve every <c>online</c> player on API startup without a watchdog snapshot:
    /// <c>offline</c> where the engine reports the server stopped (a measurement — nobody can be
    /// connected to it), <c>unknown</c> everywhere else (we missed events while down, and no
    /// reading says otherwise). Rebuilds the in-memory cache from DB.</summary>
    private async Task MarkUnknownFallbackAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            PlayerRecord[] stillOnline = await db.PlayerHistory
                .Where(p => p.Status == PlayerStatus.online)
                .ToArrayAsync(ct)
                .ConfigureAwait(false);

            int offline = 0;
            int unknown = 0;
            foreach (PlayerRecord record in stillOnline)
            {
                bool stopped = IsMeasuredStopped(record.ServerId);
                record.Status = stopped ? PlayerStatus.offline : PlayerStatus.unknown;
                if (stopped) offline++; else unknown++;
            }

            if (stillOnline.Length > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                logger.LogInformation(
                    "Player history: no watchdog snapshot on startup — {Offline} players marked offline "
                    + "(their server is stopped), {Unknown} marked unknown", offline, unknown);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        await RebuildCacheAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One-time re-key of the permanent roster onto the name-first person identity
    /// (<see cref="PlayerIdentityResolver"/>). Every existing row is regrouped by its recomputed
    /// person key; rows that collapse to the same key (a reconnect from a new port/ip that the old
    /// addr-first key split into duplicates) are merged into a single survivor. Preserves history:
    /// <c>FirstSeen</c> = earliest, <c>LastSeen</c> = latest, <c>banned</c> status and its reason are
    /// never lost, and the freshest non-blank name/addr/id are carried forward. Idempotent — after the
    /// first pass every group is a singleton already keyed on its person identity, so it no-ops.
    /// </summary>
    private async Task MergeDuplicatesAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            PlayerRecord[] all = await db.PlayerHistory.ToArrayAsync(ct).ConfigureAwait(false);

            // Group by (server, recomputed person identity). Pass the stored PlayerIdentity as the
            // sessionKey fallback so an id/name/addr-less row keeps its existing key (never collapses
            // distinct such rows onto "unknown").
            var groups = all
                .GroupBy(r => (r.ServerId,
                    Identity: PlayerIdentityResolver.Resolve(r.PlayerId, r.PlayerName, r.PlayerAddr, r.PlayerIdentity)));

            int mergedRows = 0;
            int rekeyedRows = 0;

            foreach (var group in groups)
            {
                string newIdentity = group.Key.Identity;
                // Newest first — used to pick the freshest non-blank name/addr/id.
                PlayerRecord[] rows = group.OrderByDescending(r => r.LastSeen).ToArray();
                PlayerRecord survivor = rows[0];

                if (rows.Length == 1)
                {
                    // No duplicate — but the person key may still differ from the old addr-first key.
                    if (survivor.PlayerIdentity != newIdentity)
                    {
                        survivor.PlayerIdentity = newIdentity;
                        rekeyedRows++;
                    }
                    continue;
                }

                // Merge duplicates onto the survivor.
                survivor.PlayerIdentity = newIdentity;
                survivor.FirstSeen = rows.Min(r => r.FirstSeen);
                survivor.LastSeen = rows.Max(r => r.LastSeen);
                survivor.PlayerId = rows.Select(r => r.PlayerId).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                survivor.PlayerName = rows.Select(r => r.PlayerName).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                survivor.PlayerAddr = rows.Select(r => r.PlayerAddr).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

                // Status precedence: a manual ban is authoritative and never lost; then online > offline > unknown.
                PlayerRecord? banned = rows.FirstOrDefault(r => r.Status == PlayerStatus.banned);
                survivor.Status = banned is not null ? PlayerStatus.banned
                    : rows.Any(r => r.Status == PlayerStatus.online) ? PlayerStatus.online
                    : rows.Any(r => r.Status == PlayerStatus.offline) ? PlayerStatus.offline
                    : PlayerStatus.unknown;
                survivor.BanReason = banned?.BanReason
                    ?? rows.Select(r => r.BanReason).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

                for (int i = 1; i < rows.Length; i++)
                    db.PlayerHistory.Remove(rows[i]);

                mergedRows += rows.Length - 1;
            }

            if (mergedRows > 0 || rekeyedRows > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                logger.LogInformation(
                    "Player history: re-keyed onto name-first identity — {Merged} duplicate rows merged, {Rekeyed} rows re-keyed",
                    mergedRows, rekeyedRows);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Upsert a player on <c>player.join</c>. Sets status to <c>online</c>, updates
    /// <c>LastSeen</c>, and publishes a <c>players.join</c> WS frame. Preserves <c>FirstSeen</c>
    /// if the player already exists in the roster.</summary>
    public void Join(string serverId, string? sessionKey, string? id, string? name, string? addr, DateTimeOffset since)
    {
        if (string.IsNullOrEmpty(serverId)) return;
        string playerIdentity = PlayerIdentityResolver.Resolve(id, name, addr, sessionKey);

        // Preserve FirstSeen from existing record if present.
        DateTimeOffset firstSeen = since;
        if (_cache.TryGetValue(serverId, out var roster) && roster.TryGetValue(playerIdentity, out var existing))
            firstSeen = existing.FirstSeen;

        var player = new RosterPlayer(playerIdentity, id, name, addr, PlayerStatus.online, firstSeen, since, null);

        // Upsert in-memory cache (fast).
        roster ??= _cache.GetOrAdd(serverId, static _ => new ConcurrentDictionary<string, RosterPlayer>());
        roster[playerIdentity] = player;

        // Persist to DB (fire-and-forget, non-blocking).
        _ = UpsertAsync(serverId, playerIdentity, id, name, addr, PlayerStatus.online, firstSeen, since, null);

        // Publish WS frame.
        hub.Publish(StreamProtocol.PlayersTopic, StreamProtocol.PlayerEntityKey(serverId, playerIdentity),
            new StreamMessage(StreamProtocol.PlayersTopic, StreamProtocol.PlayersJoin,
                new PlayerTransition(serverId, player)));
    }

    /// <summary>Set a player to <c>offline</c> on <c>player.leave</c>. Updates <c>LastSeen</c>
    /// and publishes a <c>players.leave</c> WS frame. Never deletes the record.</summary>
    public void Leave(string serverId, string? sessionKey, string? id, string? name, string? addr, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(serverId)) return;
        string playerIdentity = PlayerIdentityResolver.Resolve(id, name, addr, sessionKey);

        // Update in-memory cache: prefer the existing record's FirstSeen.
        DateTimeOffset firstSeen = at;
        if (_cache.TryGetValue(serverId, out var roster) && roster.TryGetValue(playerIdentity, out var existing))
            firstSeen = existing.FirstSeen;

        var player = new RosterPlayer(playerIdentity, id, name, addr, PlayerStatus.offline, firstSeen, at, null);
        roster ??= _cache.GetOrAdd(serverId, static _ => new ConcurrentDictionary<string, RosterPlayer>());
        roster[playerIdentity] = player;

        // Persist to DB (fire-and-forget).
        _ = UpsertAsync(serverId, playerIdentity, id, name, addr, PlayerStatus.offline, firstSeen, at, null);

        // Publish WS frame.
        hub.Publish(StreamProtocol.PlayersTopic, StreamProtocol.PlayerEntityKey(serverId, playerIdentity),
            new StreamMessage(StreamProtocol.PlayersTopic, StreamProtocol.PlayersLeave,
                new PlayerTransition(serverId, player)));
    }

    /// <summary>Set all players for a server to <c>offline</c> on instance stop/start/restart.
    /// Publishes a single <c>players.reset</c> frame. Never deletes records — marks them offline
    /// so the permanent roster preserves the history.</summary>
    public void Reset(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return;
        if (!_cache.TryGetValue(serverId, out var roster) || roster.IsEmpty) return;

        // Mark all players for this server as offline in the in-memory cache.
        foreach (var kvp in roster)
        {
            roster[kvp.Key] = kvp.Value with { Status = PlayerStatus.offline };
        }

        // Persist to DB: set all players for this server to offline (fire-and-forget).
        _ = ResetServerAsync(serverId);

        // Publish WS frame.
        hub.Publish(StreamProtocol.PlayersTopic, StreamProtocol.PlayerResetEntityKey(serverId),
            new StreamMessage(StreamProtocol.PlayersTopic, StreamProtocol.PlayersReset,
                new PlayerReset(serverId)));
    }

    /// <summary>Ban a player. Sets status to <c>banned</c>, stores the reason, and publishes
    /// a <c>players.ban</c> WS frame.</summary>
    /// <remarks>
    /// <see cref="RosterPlayer.LastSeen"/> is deliberately left alone: it means when this player was
    /// last <em>present</em>, and only a join or a leave changes that. Moderating someone who is not
    /// connected does not make them more recently seen — and because the roster sorts on it, writing
    /// the moment of the ban would also jump them to the top of the list for something they did not do.
    /// The same reasoning is why this takes no timestamp at all.
    /// </remarks>
    public void Ban(string serverId, string playerIdentity, string? reason)
    {
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(playerIdentity)) return;

        // Update in-memory cache.
        if (_cache.TryGetValue(serverId, out var roster) && roster.TryGetValue(playerIdentity, out var existing))
        {
            var banned = existing with { Status = PlayerStatus.banned, BanReason = reason };
            roster[playerIdentity] = banned;

            _ = UpsertAsync(serverId, playerIdentity, existing.PlayerId, existing.PlayerName,
                existing.PlayerAddr, PlayerStatus.banned, existing.FirstSeen, existing.LastSeen, reason);

            hub.Publish(StreamProtocol.PlayersTopic, StreamProtocol.PlayerEntityKey(serverId, playerIdentity),
                new StreamMessage(StreamProtocol.PlayersTopic, StreamProtocol.PlayersBan,
                    new PlayerTransition(serverId, banned)));
        }
    }

    /// <summary>The full permanent roster for one server — all players who have ever connected,
    /// ordered by status (online → unknown → offline → banned) then most recently seen first.
    /// Empty (never null) for an unknown or unobserved server.</summary>
    public IReadOnlyList<RosterPlayer> GetRoster(string serverId)
    {
        if (string.IsNullOrEmpty(serverId) || !_cache.TryGetValue(serverId, out var roster))
            return [];

        return roster.Values
            .OrderBy(p => StatusOrder(p.Status))
            .ThenByDescending(p => p.LastSeen)
            .ToArray();
    }

    /// <summary>
    /// Look one player up by the roster's own dedup key. This is how a moderation request resolves
    /// its target: the caller names a <paramref name="playerIdentity"/> and the identity fields come
    /// from <em>this</em> record, never from the request — a client-supplied address or name would
    /// let a caller moderate someone the roster never saw.
    /// </summary>
    /// <returns><see langword="true"/> and the record when this server has seen this player;
    /// otherwise <see langword="false"/>.</returns>
    public bool TryGetPlayer(string serverId, string playerIdentity, out RosterPlayer player)
    {
        player = default!;

        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(playerIdentity))
            return false;

        if (_cache.TryGetValue(serverId, out var roster) && roster.TryGetValue(playerIdentity, out var found))
        {
            player = found;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Find the roster key for the game-facing token a moderation event carries.
    /// </summary>
    /// <remarks>
    /// The engine's event names a player the way the <em>game</em> does — an address, a name, or an
    /// account id, whichever the blueprint's template declared — while the roster is keyed on its own
    /// dedup identity. This matches the token against the identity fields this server has actually
    /// observed, comparing an address without its ephemeral port because that is the form a game
    /// moderates by.
    /// </remarks>
    /// <returns>The matching roster key, or <see langword="null"/> when no player on this server
    /// carries that token — a real case (an address can be banned before it ever connected), and one
    /// that must leave the roster alone rather than invent a member.</returns>
    public string? FindIdentityByTarget(string serverId, string? target)
    {
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrWhiteSpace(target)) return null;
        if (!_cache.TryGetValue(serverId, out var roster)) return null;

        foreach (var (identity, player) in roster)
        {
            if (string.Equals(player.PlayerId, target, StringComparison.Ordinal)
                || string.Equals(player.PlayerName, target, StringComparison.Ordinal)
                || string.Equals(ModerationTargetResolver.AddressOnly(player.PlayerAddr), target, StringComparison.Ordinal))
            {
                return identity;
            }
        }

        return null;
    }

    /// <summary>Lift a ban. Returns the player to <c>offline</c> and clears the reason, publishing a
    /// <c>players.ban</c> frame so a watching client sees the status change.</summary>
    /// <remarks>
    /// <para>The player goes to <c>offline</c>, not <c>online</c>: lifting a block permits a connection,
    /// it does not make one. A real join event is what moves them to <c>online</c>.</para>
    /// <para><see cref="RosterPlayer.LastSeen"/> is left alone for the same reason as
    /// <see cref="Ban"/> — presence is what that field records, and a moderation action is not a
    /// sighting.</para>
    /// </remarks>
    public void Unban(string serverId, string playerIdentity)
    {
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(playerIdentity)) return;

        if (_cache.TryGetValue(serverId, out var roster) && roster.TryGetValue(playerIdentity, out var existing))
        {
            var lifted = existing with { Status = PlayerStatus.offline, BanReason = null };
            roster[playerIdentity] = lifted;

            _ = UpsertAsync(serverId, playerIdentity, existing.PlayerId, existing.PlayerName,
                existing.PlayerAddr, PlayerStatus.offline, existing.FirstSeen, existing.LastSeen, banReason: null);

            hub.Publish(StreamProtocol.PlayersTopic, StreamProtocol.PlayerEntityKey(serverId, playerIdentity),
                new StreamMessage(StreamProtocol.PlayersTopic, StreamProtocol.PlayersBan,
                    new PlayerTransition(serverId, lifted)));
        }
    }

    /// <summary>Get a player's current status from the cache, or <c>unknown</c> if not tracked.</summary>
    public PlayerStatus GetStatus(string serverId, string playerIdentity)
    {
        if (_cache.TryGetValue(serverId, out var roster) && roster.TryGetValue(playerIdentity, out var player))
            return player.Status;
        return PlayerStatus.unknown;
    }

    // --- DB persistence (fire-and-forget, serialized writes) ---

    private async Task UpsertAsync(
        string serverId, string playerIdentity, string? playerId, string? playerName, string? playerAddr,
        PlayerStatus status, DateTimeOffset firstSeen, DateTimeOffset lastSeen, string? banReason)
    {
        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                PlayerRecord? existing = await db.PlayerHistory
                    .FirstOrDefaultAsync(p => p.ServerId == serverId && p.PlayerIdentity == playerIdentity)
                    .ConfigureAwait(false);

                if (existing is null)
                {
                    db.PlayerHistory.Add(new PlayerRecord
                    {
                        ServerId = serverId,
                        PlayerIdentity = playerIdentity,
                        PlayerId = playerId,
                        PlayerName = playerName,
                        PlayerAddr = playerAddr,
                        Status = status,
                        FirstSeen = firstSeen,
                        LastSeen = lastSeen,
                        BanReason = banReason
                    });
                }
                else
                {
                    existing.Status = status;
                    existing.LastSeen = lastSeen;
                    existing.PlayerId = playerId;
                    existing.PlayerName = playerName;
                    existing.PlayerAddr = playerAddr;
                    if (banReason is not null)
                        existing.BanReason = banReason;
                }

                await db.SaveChangesAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Player history: failed to upsert {ServerId}/{PlayerIdentity}", serverId, playerIdentity);
        }
    }

    private async Task ResetServerAsync(string serverId)
    {
        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                await db.PlayerHistory
                    .Where(p => p.ServerId == serverId && p.Status == PlayerStatus.online)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlayerStatus.offline))
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Player history: failed to reset server {ServerId}", serverId);
        }
    }

    private async Task RebuildCacheAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            PlayerRecord[] all = await db.PlayerHistory.ToArrayAsync(ct).ConfigureAwait(false);

            _cache.Clear();
            foreach (PlayerRecord record in all)
            {
                var roster = _cache.GetOrAdd(record.ServerId, static _ => new ConcurrentDictionary<string, RosterPlayer>());
                roster[record.PlayerIdentity] = new RosterPlayer(
                    record.PlayerIdentity, record.PlayerId, record.PlayerName, record.PlayerAddr,
                    record.Status, record.FirstSeen, record.LastSeen, record.BanReason);
            }

            logger.LogInformation("Player history: rebuilt cache from {Count} records", all.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Player history: failed to rebuild cache from DB");
        }
    }

    private async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (_ensured) return;
        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured) return;
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    /// <summary>Status sort order: online=0, unknown=1, offline=2, banned=3.</summary>
    private static int StatusOrder(PlayerStatus status) => status switch
    {
        PlayerStatus.online => 0,
        PlayerStatus.unknown => 1,
        PlayerStatus.offline => 2,
        PlayerStatus.banned => 3,
        _ => 4
    };
}
