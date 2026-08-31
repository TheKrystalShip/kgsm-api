
namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The permanent player roster response for one server —
/// <c>GET /servers/{id}/players</c>. Returns ALL players who have ever connected, each with
/// their current status (online/offline/banned/unknown). The roster of record lives in the
/// <c>PlayerRecord</c> table; this is a read of the DB-backed
/// <c>PlayerHistoryService</c> projection.
/// </summary>
/// <param name="Detection"><see cref="PlayerDetection.Configured"/> when the instance declares at
/// least one of <c>player_joined_regex</c>/<c>player_left_regex</c> (native) — presence is only ever
/// <em>knowable</em> when some detection exists; <c>Unknown</c> when neither is
/// configured, in which case <see cref="Players"/> is <strong>always</strong> <c>[]</c>. Never collapse
/// "we can't see" into "nobody's here" — the UI must render "presence not available for this game",
/// never "0 players online", on <c>unknown</c>.</param>
/// <param name="Players">All players who have ever connected to this server, ordered by status
/// (online → unknown → offline → banned) then most recently seen first. Empty when no players have
/// ever been observed (only meaningful when <see cref="Detection"/> is
/// <see cref="PlayerDetection.Configured"/>).</param>
/// <param name="Moderation">Which moderation actions this game supports, so a client renders the
/// buttons it can actually offer. It rides on this response rather than a separate call so the
/// roster and the actions available on it can never disagree.</param>
/// <param name="Mechanism">How presence is observed, in the supervisor's own vocabulary — <c>log</c>
/// (matched from the game's output, so real transitions), <c>rcon</c> (polled and diffed, so churn
/// between polls is invisible), <c>none</c>, or <c>unknown</c>. Beside <paramref name="Detection"/>
/// rather than folded into it, because the two answer different questions: whether to render a roster
/// at all, and what an empty one is worth. An RCON reading showing nobody cannot distinguish an empty
/// server from one somebody joined and left between two polls.</param>
public sealed record PlayersResponse(
    string Detection,
    IReadOnlyList<RosterPlayer> Players,
    ModerationCapability Moderation,
    string? Mechanism = null);

/// <summary>
/// A player in the permanent roster, keyed on
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

/// <summary>The closed moderation-action vocabulary — <c>POST /servers/{id}/players/{identity}/{action}</c>.</summary>
public static class ModerationAction
{
    public const string Kick = "kick";
    public const string Ban = "ban";
    public const string Unban = "unban";
}

/// <summary>
/// The result of a moderation action. Echoes what was done and — usefully for a client that wants
/// to explain itself — <see cref="TargetKind"/>, the identity the game asked for.
/// </summary>
/// <remarks>The resolved token itself is deliberately absent: the caller named a roster entry, not
/// an address, and handing the address back would invite a client to start sending it.</remarks>
/// <param name="ServerId">The server the action ran against.</param>
/// <param name="PlayerIdentity">The roster key that was moderated.</param>
/// <param name="Action">One of <see cref="ModerationAction"/>.</param>
/// <param name="TargetKind">The identity kind the game's template declared — <c>ip</c>, <c>name</c>
/// or <c>id</c>.</param>
public sealed record ModerationResult(
    string ServerId, string PlayerIdentity, string Action, string TargetKind);

/// <summary>
/// Which moderation actions a game supports, derived from the templates its blueprint declares.
/// Lets a client render the actions it can actually offer instead of discovering a <c>409</c> by
/// pressing a button — and never claims support the blueprint did not declare.
/// </summary>
/// <param name="Kick">Whether the game declares a kick command.</param>
/// <param name="Ban">Whether the game declares a ban command.</param>
/// <param name="Unban">Whether the game declares an unban command.</param>
/// <param name="TargetKind">The identity kind the declared commands address (<c>ip</c>, <c>name</c>
/// or <c>id</c>), or <see langword="null"/> when the game declares no moderation at all. A client
/// uses it to warn that a player without that identity cannot be moderated.</param>
public sealed record ModerationCapability(bool Kick, bool Ban, bool Unban, string? TargetKind);
