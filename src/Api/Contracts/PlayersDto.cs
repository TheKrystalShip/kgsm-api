namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The live roster response for one server (player-presence-contract.md §5) —
/// <c>GET /servers/{id}/players</c>. The roster of record lives in the API (keyed on the opaque
/// <c>sessionKey</c> the watchdog's session map emits); this is a pure read of the in-memory
/// projection <see cref="Services.Players.PlayerRosterService"/> maintains from the
/// <c>player.join</c>/<c>player.leave</c> event stream.
/// </summary>
/// <param name="Detection"><see cref="PlayerDetection.Configured"/> when the instance declares at
/// least one of <c>player_joined_regex</c>/<c>player_left_regex</c> (native) — presence is only ever
/// <em>knowable</em> when some detection exists; <see cref="PlayerDetection.Unknown"/> when neither is
/// configured, in which case <see cref="Players"/> is <strong>always</strong> <c>[]</c>. Never collapse
/// "we can't see" into "nobody's here" — the UI must render "presence not available for this game",
/// never "0 players online", on <c>unknown</c>.</param>
/// <param name="Players">The live session roster, empty when nobody is connected (a REAL empty, only
/// meaningful when <see cref="Detection"/> is <see cref="PlayerDetection.Configured"/>).</param>
public sealed record PlayersResponse(string Detection, IReadOnlyList<RosterPlayer> Players);

/// <summary>
/// One connected session in the live roster (player-presence-contract.md §5), keyed on
/// <see cref="SessionKey"/> — never on <see cref="Name"/> (display names collide across sessions, e.g.
/// duplicate romestead personas or Valheim/Core Keeper character names).
/// </summary>
/// <param name="SessionKey">The opaque per-session correlation token (<c>key ?? addr ?? id ?? name</c>)
/// — always a non-empty string on the real wire; the roster's actual key.</param>
/// <param name="Name">The display label the game gave at join, or <see langword="null"/> when the
/// source never carried one. Never fabricated.</param>
/// <param name="Id">The stable account-layer id (SteamID64/UUID) when the game exposes one safely,
/// otherwise <see langword="null"/>. Never fabricated, never a cross-line guess.</param>
/// <param name="Addr">The real network address (<c>ip:port</c>) on a direct-socket game, otherwise
/// <see langword="null"/> (Steam-relay/P2P games never expose one). Never fabricated.</param>
/// <param name="Since">When this session joined (UTC) — the event's own timestamp when the emitter
/// supplied one, else when this api observed the join.</param>
public sealed record RosterPlayer(
    string SessionKey,
    string? Name,
    string? Id,
    string? Addr,
    DateTimeOffset Since);

/// <summary>The closed <see cref="PlayersResponse.Detection"/> vocabulary.</summary>
public static class PlayerDetection
{
    public const string Configured = "configured";
    public const string Unknown = "unknown";
}
