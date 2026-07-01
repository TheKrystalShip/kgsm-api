using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;

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
/// <para><b>Unknown on startup.</b> On API startup, all <c>online</c> entries are set to
/// <c>unknown</c> — we missed events during downtime. They resolve to <c>online</c> or
/// <c>offline</c> only on the next definitive event — no probing, no timeouts. Honest.</para>
/// <para><b>Write pattern.</b> Follows the <see cref="Audit.AuditService"/> pattern: singleton,
/// own DI scope per write, serialized writes via <see cref="SemaphoreSlim"/> (SQLite single-writer),
/// <c>EnsureCreated</c> with double-checked locking.</para>
/// </remarks>
public sealed class PlayerHistoryService(
    IServiceScopeFactory scopeFactory,
    StreamHub hub,
    ILogger<PlayerHistoryService> logger)
{
    // serverId -> (playerIdentity -> player). In-memory cache for fast reads + WS coalescing.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RosterPlayer>> _cache = new();

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <summary>Mark all <c>online</c> players as <c>unknown</c> on API startup. We missed events
    /// during downtime — honest until resolved by a definitive join/leave event. Call once from
    /// <see cref="Audit.KgsmAuditConsumer.StartAsync"/> before registering event handlers.</summary>
    public async Task MarkUnknownOnStartupAsync(CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        int count = await db.PlayerHistory
            .Where(p => p.Status == PlayerStatus.online)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlayerStatus.unknown), ct)
            .ConfigureAwait(false);

        if (count > 0)
            logger.LogInformation("Player history: marked {Count} online players as unknown on startup", count);

        // Rebuild the in-memory cache from DB after marking unknown.
        await RebuildCacheAsync(ct).ConfigureAwait(false);
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
