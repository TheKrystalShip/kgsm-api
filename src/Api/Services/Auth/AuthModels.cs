using System.Security.Claims;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// A Discord identity verified once at login (from <c>/users/@me</c>). The Discord OAuth token is
/// discarded after this is built — we keep no Discord token server-side (architecture.html §3·f).
/// The profile is a <em>snapshot</em> captured at login and embedded in the session JWT (see the
/// §3·f divergence: GET /auth/session returns this snapshot, not a fresh live fetch, because the
/// token is gone).
/// </summary>
public sealed record DiscordIdentity(
    string UserId,
    string Username,
    string Display,
    string? AvatarUrl,
    IReadOnlyList<string> Scopes);

/// <summary>The outcome of resolving an OAuth code: the verified identity + the tier the host's
/// bot grants it. <see cref="AuthTier.None"/> means "verified but no role here" → terminal 403.</summary>
public sealed record ResolvedPrincipal(DiscordIdentity Identity, AuthTier Tier);

/// <summary>
/// The claims read back from a valid refresh token at <c>/auth/session/refresh</c> — enough to
/// re-mint a fresh access token (same identity, tier, profile snapshot, <see cref="SessionId"/> AND
/// the rotating <see cref="Jti"/>) with no Discord round-trip. Role is NOT re-checked on refresh
/// (deferred — the long-term "transparent role changes" idea); today a role change still takes effect
/// at the next full OAuth bounce (≤ the 30-day absolute cap of the LAST refresh). The
/// <see cref="SessionId"/> is carried through so the re-minted access keeps the same session
/// (D7/D8 — same sid across a session's lifetime, sliding window on the session row's Expires).
/// The <see cref="Jti"/> is the presented refresh's id — the controller validates it against the
/// session row's stored CurrentJti (reuse detection); a stale jti → 401 (old/stolen token).
/// </summary>
public sealed record RefreshClaims(DiscordIdentity Identity, AuthTier Tier, string SessionId, string Jti);

/// <summary>
/// Reads the identity + tier back out of a validated session token's claims — shared by the refresh
/// path (<see cref="ISessionTokenService.ReadRefreshAsync"/>) and <c>GET /auth/session</c> (which
/// reads <c>HttpContext.User</c>). The profile is the snapshot embedded at login (the §3·f
/// divergence — we keep no Discord token to re-fetch live).
/// </summary>
public static class SessionClaims
{
    /// <summary>The <c>sub</c> claim is <c>discord:{userId}</c>; returns null if absent/malformed.</summary>
    public static DiscordIdentity? ReadIdentity(ClaimsIdentity ci)
    {
        string? sub = ci.FindFirst("sub")?.Value ?? ci.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (sub is null || !sub.StartsWith("discord:", StringComparison.Ordinal))
            return null;
        string userId = sub["discord:".Length..];
        if (userId.Length == 0)
            return null;

        string username = ci.FindFirst(AuthClaims.Username)?.Value ?? userId;
        string display = ci.FindFirst(AuthClaims.Display)?.Value ?? username;
        string? avatar = ci.FindFirst(AuthClaims.Avatar)?.Value;
        string scope = ci.FindFirst("scope")?.Value ?? "";
        IReadOnlyList<string> scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return new DiscordIdentity(userId, username, display, avatar, scopes);
    }

    public static AuthTier ReadTier(ClaimsIdentity ci) =>
        AuthTiers.Parse(ci.FindFirst(AuthClaims.Tier)?.Value);

    /// <summary>
    /// The <see cref="AuthClaims.SessionId"/> claim (<c>sid_&lt;guid&gt;</c>), or <see langword="null"/>
    /// when absent (a pre-M4·c token — the validator rejects it). Read at refresh (carried into the
    /// re-minted access) and by the per-request session validator (the registry lookup key).
    /// </summary>
    public static string? ReadSessionId(ClaimsIdentity ci) =>
        ci.FindFirst(AuthClaims.SessionId)?.Value;

    /// <summary>
    /// The <see cref="AuthClaims.Jti"/> claim (the per-token JWT ID), or <see langword="null"/> when
    /// absent (a pre-rotation token). Read on the refresh path; the controller validates it against
    /// the session row's stored <c>CurrentJti</c> (reuse detection — a stale jti → 401). For access
    /// tokens <c>jti</c> is informational only (the per-request validator does NOT check jti —
    /// short-lived access tokens rely on the session registry, not on per-token reuse detection).
    /// </summary>
    public static string? ReadJti(ClaimsIdentity ci) =>
        ci.FindFirst(AuthClaims.Jti)?.Value;
}
