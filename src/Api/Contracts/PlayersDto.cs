using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The permanent player roster response for one server (player-presence-contract.md §5) —
/// <c>GET /servers/{id}/players</c>. Returns ALL players who have ever connected, each with
/// their current status (online/offline/banned/unknown). The roster of record lives in the
/// <see cref="Data.PlayerRecord"/> table; this is a read of the DB-backed
/// <see cref="Services.Players.PlayerHistoryService"/> projection.
/// </summary>
/// <param name="Detection"><see cref="PlayerDetection.Configured"/> when the instance declares at
/// least one of <c>player_joined_regex</c>/<c>player_left_regex</c> (native) — presence is only ever
/// <em>knowable</em> when some detection exists; <see cref="PlayerDetection.Unknown"/> when neither is
/// configured, in which case <see cref="Players"/> is <strong>always</strong> <c>[]</c>. Never collapse
/// "we can't see" into "nobody's here" — the UI must render "presence not available for this game",
/// never "0 players online", on <c>unknown</c>.</param>
/// <param name="Players">All players who have ever connected to this server, ordered by status
/// (online → unknown → offline → banned) then most recently seen first. Empty when no players have
/// ever been observed (only meaningful when <see cref="Detection"/> is
/// <see cref="PlayerDetection.Configured"/>).</param>
public sealed record PlayersResponse(string Detection, IReadOnlyList<RosterPlayer> Players);

/// <summary>
/// A player in the permanent roster (player-presence-contract.md §5), keyed on
/// <see cref="PlayerIdentity"/> — the stable dedup key (first non-blank of PlayerId, PlayerName,
/// PlayerAddr, SessionKey). One row per unique player per server; never deleted, only status changes.
/// </summary>
/// <param name="PlayerIdentity">The stable player-level dedup key — deliberately different from
/// the session-level <c>sessionKey</c> (which is <c>key ?? addr ?? id ?? name</c>). The player-level
/// identity prioritizes the stable account id (SteamID64/UUID), then the character <c>name</c> (the
/// person, for account-less games), before the ephemeral network <c>addr</c>.</param>
/// <param name="PlayerId">The stable account-layer id (SteamID64/UUID) when the game exposes one safely,
/// otherwise <see langword="null"/>. Never fabricated.</param>
/// <param name="PlayerName">The display label the game gave at join, or <see langword="null"/> when the
/// source never carried one. Never fabricated.</param>
/// <param name="PlayerAddr">The real network address (<c>ip:port</c>) on a direct-socket game, otherwise
/// <see langword="null"/> (Steam-relay/P2P games never expose one). Never fabricated.</param>
/// <param name="Status">The player's current status: <see cref="PlayerStatus.online"/>,
/// <see cref="PlayerStatus.offline"/>, <see cref="PlayerStatus.banned"/>, or
/// <see cref="PlayerStatus.unknown"/> (API missed events during downtime — honest until resolved
/// by a definitive join/leave event).</param>
/// <param name="FirstSeen">When this player first connected to this server (UTC).</param>
/// <param name="LastSeen">When this player last connected or disconnected (UTC) — updated on every
/// join/leave event.</param>
/// <param name="BanReason">Why the player was banned, or <see langword="null"/> if not banned.</param>
public sealed record RosterPlayer(
    string PlayerIdentity,
    string? PlayerId,
    string? PlayerName,
    string? PlayerAddr,
    PlayerStatus Status,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? BanReason);

/// <summary>The closed <see cref="PlayersResponse.Detection"/> vocabulary.</summary>
public static class PlayerDetection
{
    public const string Configured = "configured";
    public const string Unknown = "unknown";
}

/// <summary>The <c>players.join</c>/<c>players.leave</c>/<c>players.ban</c> WS payload:
/// <c>{ serverId, player }</c>.</summary>
public sealed record PlayerTransition(string ServerId, RosterPlayer Player);

/// <summary>The <c>players.reset</c> WS payload: <c>{ serverId }</c> — no per-player data, the client
/// marks all players for that server as offline.</summary>
public sealed record PlayerReset(string ServerId);
