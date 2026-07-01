using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Data;

/// <summary>
/// The permanent player roster record — one row per unique player per server (player-presence-contract.md §5).
/// Once a player connects, they are never removed; their <see cref="Status"/> toggles between
/// <see cref="PlayerStatus.online"/>, <see cref="PlayerStatus.offline"/>, <see cref="PlayerStatus.banned"/>,
/// and <see cref="PlayerStatus.unknown"/> as events arrive. The roster is the authority for the
/// <c>GET /servers/{id}/players</c> endpoint — the in-memory <see cref="Services.Players.PlayerRosterService"/>
/// is a fast session-level projection for WS coalescing only.
/// </summary>
/// <remarks>
/// <para><b>Identity resolution.</b> <see cref="PlayerIdentity"/> is the stable dedup key, resolved as the
/// first non-empty of: <c>PlayerId</c> (SteamID64/UUID — the gold standard), <c>PlayerAddr</c>,
/// <c>PlayerName</c>, <c>SessionKey</c> (fallback). This is deliberately different from the session-level
/// <c>sessionKey</c> (which is <c>key ?? addr ?? id ?? name</c>) — the player-level identity prioritizes
/// the stable account id.</para>
/// <para><b>Unknown resolution.</b> On API startup, all <c>online</c> entries are set to <c>unknown</c>
/// (we missed events during downtime). They resolve to <c>online</c> or <c>offline</c> only on the next
/// definitive event — no probing, no timeouts. Honest.</para>
/// <para><b>Never deleted.</b> A player record is permanent. Status changes are the only mutations.
/// <c>EnsureCreated</c>, not EF migrations (same dev-authority pattern as <see cref="AuditEntry"/>).</para>
/// </remarks>
public sealed class PlayerRecord
{
    public long RowId { get; set; }

    /// <summary>The game server instance this player belongs to.</summary>
    public string ServerId { get; set; } = default!;

    /// <summary>
    /// The stable player-level dedup key (first non-empty of PlayerId, PlayerAddr, PlayerName, SessionKey).
    /// One row per (ServerId, PlayerIdentity) — composite unique index.
    /// </summary>
    public string PlayerIdentity { get; set; } = default!;

    /// <summary>The stable account-layer id (SteamID64/UUID) when the game exposes one, otherwise null.</summary>
    public string? PlayerId { get; set; }

    /// <summary>The display name the game gave at join, or null.</summary>
    public string? PlayerName { get; set; }

    /// <summary>The real network address (ip:port) on a direct-socket game, or null.</summary>
    public string? PlayerAddr { get; set; }

    /// <summary>The player's current status — <see cref="PlayerStatus"/> enum, serialized as lowercase JSON.</summary>
    public PlayerStatus Status { get; set; }

    /// <summary>When this player first connected to this server (UTC).</summary>
    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>When this player last connected or disconnected (UTC) — updated on every join/leave.</summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Why the player was banned, or null if not banned.</summary>
    public string? BanReason { get; set; }
}

/// <summary>
/// The closed status vocabulary for <see cref="PlayerRecord.Status"/>. Serialized as lowercase JSON
/// via <c>[JsonStringEnumConverter]</c> — <c>"online"</c>, <c>"offline"</c>, <c>"banned"</c>, <c>"unknown"</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlayerStatus
{
    online,
    offline,
    banned,
    unknown
}
