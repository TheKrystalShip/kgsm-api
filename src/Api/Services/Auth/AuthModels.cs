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
/// re-mint a fresh access token (same identity, tier and profile snapshot) with no Discord
/// round-trip. Role is NOT re-checked on refresh, so a role change only takes effect at the next
/// full bounce (≤ the 8h refresh cap) — an accepted, documented trade.
/// </summary>
public sealed record RefreshClaims(DiscordIdentity Identity, AuthTier Tier);

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
}
