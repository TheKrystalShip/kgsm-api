using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
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
/// <para><b>Reconcile on startup.</b> On API startup, the watchdog's live session map is queried
/// to determine who is currently online. Players in the snapshot are marked online; everyone
/// else is marked offline. This replaces the old "mark unknown" behavior — the watchdog snapshot
/// IS the ground truth. If the watchdog is absent/down, falls back to marking unknown (honest).</para>
/// <para><b>Write pattern.</b> Follows the <see cref="Audit.AuditService"/> pattern: singleton,
/// own DI scope per write, serialized writes via <see cref="SemaphoreSlim"/> (SQLite single-writer),
/// <c>EnsureCreated</c> with double-checked locking.</para>
/// </remarks>
public sealed class PlayerHistoryService(
    IServiceScopeFactory scopeFactory,
    IServiceProvider serviceProvider,
    StreamHub hub,
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

        // Try to get the watchdog's live session map.
        IWatchdogClient? watchdog = serviceProvider.GetService(typeof(IWatchdogClient)) as IWatchdogClient;
        if (watchdog is null)
        {
            logger.LogInformation("Player history: watchdog not provisioned — falling back to unknown on startup");
            await MarkUnknownFallbackAsync(ct).ConfigureAwait(false);
            return;
        }

        IReadOnlyDictionary<string, IReadOnlyList<WatchdogPlayer>>? watchdogSessions;
        try
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probe.Token);
            watchdogSessions = await watchdog.GetAllPlayersAsync(linked.Token).ConfigureAwait(false);
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
        var onlineByInstance = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var kvp in watchdogSessions)
        {
            var online = new HashSet<string>(StringComparer.Ordinal);
            foreach (var session in kvp.Value)
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

        // Phase 2: discover new players from watchdog that aren't in the DB yet.
        foreach (var kvp in watchdogSessions)
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
                    string playerIdentity = session.Id ?? session.Addr ?? session.Name ?? session.SessionKey;
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
        foreach (var kvp in watchdogSessions)
        {
            string serverId = kvp.Key;
            foreach (var session in kvp.Value)
            {
                if (session.SessionKey is null) continue;
                string playerIdentity = session.Id ?? session.Addr ?? session.Name ?? session.SessionKey;

                if (_cache.TryGetValue(serverId, out var roster) && roster.TryGetValue(playerIdentity, out var player) && player.Status == PlayerStatus.online)
                {
                    hub.Publish(StreamProtocol.PlayersTopic, StreamProtocol.PlayerEntityKey(serverId, playerIdentity),
                        new StreamMessage(StreamProtocol.PlayersTopic, StreamProtocol.PlayersJoin,
                            new PlayerTransition(serverId, player)));
                }
            }
        }
    }

    /// <summary>Mark all <c>online</c> players as <c>unknown</c> on API startup. Fallback when
    /// the watchdog is unavailable. Rebuilds the in-memory cache from DB.</summary>
    private async Task MarkUnknownFallbackAsync(CancellationToken ct)
    {
        int count = await db_playerHistory()
            .Where(p => p.Status == PlayerStatus.online)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlayerStatus.unknown), ct)
            .ConfigureAwait(false);

        if (count > 0)
            logger.LogInformation("Player history: marked {Count} online players as unknown on startup", count);

        await RebuildCacheAsync(ct).ConfigureAwait(false);
    }

    private IQueryable<PlayerRecord> db_playerHistory()
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().PlayerHistory;
    }

    /// <summary>Upsert a player on <c>player.join</c>. Sets status to <c>online</c>, updates
    /// <c>LastSeen</c>, and publishes a <c>players.join</c> WS frame. Preserves <c>FirstSeen</c>
    /// if the player already exists in the roster.</summary>
    public void Join(string serverId, string? sessionKey, string? id, string? name, string? addr, DateTimeOffset since)
    {
        if (string.IsNullOrEmpty(serverId)) return;
        string playerIdentity = ResolvePlayerIdentity(id, addr, name, sessionKey);

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
        string playerIdentity = ResolvePlayerIdentity(id, addr, name, sessionKey);

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
    public void Ban(string serverId, string playerIdentity, string? reason, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(playerIdentity)) return;

        // Update in-memory cache.
        if (_cache.TryGetValue(serverId, out var roster) && roster.TryGetValue(playerIdentity, out var existing))
        {
            var banned = existing with { Status = PlayerStatus.banned, BanReason = reason, LastSeen = at };
            roster[playerIdentity] = banned;

            _ = UpsertAsync(serverId, playerIdentity, existing.PlayerId, existing.PlayerName,
                existing.PlayerAddr, PlayerStatus.banned, existing.FirstSeen, at, reason);

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

    /// <summary>Resolve the stable player identity (first non-blank of id, addr, name, sessionKey).
    /// Deliberately different from the session-level sessionKey: the player identity prioritizes
    /// the stable account id.</summary>
    private static string ResolvePlayerIdentity(string? id, string? addr, string? name, string? sessionKey) =>
        !string.IsNullOrEmpty(id) ? id
        : !string.IsNullOrEmpty(addr) ? addr
        : !string.IsNullOrEmpty(name) ? name
        : !string.IsNullOrEmpty(sessionKey) ? sessionKey
        : "unknown";

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
