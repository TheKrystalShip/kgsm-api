namespace TheKrystalShip.Api.Services.Players;

/// <summary>
/// The single source of truth for the durable <b>player-level</b> identity — the dedup key for the
/// permanent roster (<see cref="Data.PlayerRecord.PlayerIdentity"/>), keyed one row per person per
/// server. Deliberately different from the <b>session-level</b> key
/// (<see cref="PlayerRosterService"/>'s <c>ResolveKey</c>, which is <c>key ?? addr ?? id ?? name</c>
/// and correctly disambiguates concurrent connections).
/// </summary>
/// <remarks>
/// <para><b>Precedence: <c>id → name → addr → sessionKey</c>.</b> The stable account id (SteamID64/UUID)
/// wins when a game exposes one. When it does not — the common case for direct-socket games like
/// romestead — the <b>character name</b> is the person, so it ranks <b>above</b> the network address.
/// This is the fix for the reconnect-duplicate bug: <c>addr</c> is <c>ip:port</c>, and both the port
/// (ephemeral, reassigned every connection) and the ip (ISP-mutable) fracture a durable identity, so
/// keying the person-row on <c>addr</c> minted a new row per reconnect (player-presence-contract.md §5).</para>
/// <para><b>Name is present on both join and leave.</b> Even when a game's <c>player_left_regex</c>
/// captures only <c>addr</c>, the watchdog's <c>PlayerSessionMap</c> backfills the join-captured name
/// onto the leave event, so keying on name still correlates a leave to the right person-row.</para>
/// <para><b>Trim, don't case-fold.</b> The name is trimmed (guards a stray captured space) but its case
/// is preserved: a game reports a fixed character name deterministically per session, so case-folding
/// would add a merge risk (distinct-cased humans) with no reconnect benefit.</para>
/// </remarks>
public static class PlayerIdentityResolver
{
    /// <summary>Resolve the durable person-identity: first non-blank of <c>id</c>, <c>name</c>,
    /// <c>addr</c>, <c>sessionKey</c> (trimmed); <c>"unknown"</c> when none is present.</summary>
    public static string Resolve(string? id, string? name, string? addr, string? sessionKey) =>
        !string.IsNullOrWhiteSpace(id) ? id.Trim()
        : !string.IsNullOrWhiteSpace(name) ? name.Trim()
        : !string.IsNullOrWhiteSpace(addr) ? addr.Trim()
        : !string.IsNullOrWhiteSpace(sessionKey) ? sessionKey.Trim()
        : "unknown";
}
