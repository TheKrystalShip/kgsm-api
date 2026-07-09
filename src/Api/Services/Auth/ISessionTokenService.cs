using Microsoft.IdentityModel.Tokens;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Mints and validates the host-scoped session JWTs (the M4·a bearer decision: stateless JWT). An
/// <em>access</em> token (~15 min) is the bearer on every protected endpoint; a <em>refresh</em>
/// token (30-day absolute cap) lets the client rotate the access token with no Discord round-trip
/// until the cap, after which it silently re-bounces through the SSO anchor (architecture.html §3·f).
/// <para>
/// <b>M4·c — the <c>sid</c> claim:</b> each mint now carries a session id (<c>sid_&lt;guid&gt;</c>,
/// passed to <see cref="MintAccess"/>/<see cref="MintRefresh"/>), stable across a session's lifetime
/// (carried by access + refresh; survives access rotation). The per-request validator reads it back
/// to consult the session registry — the revocation authority the stateless JWT alone cannot be.
/// Pre-M4·c tokens (no <c>sid</c>) are rejected — the D10 clean break (see
/// <c>docs/session-management-plan.md</c>).
/// </para>
/// </summary>
public interface ISessionTokenService
{
    /// <summary>Mint a short-lived access bearer for an authorized identity/tier, scoped to a session.</summary>
    MintedToken MintAccess(DiscordIdentity identity, AuthTier tier, string sessionId);

    /// <summary>Mint the refresh token (its lifetime IS the 30-day absolute cap — there is no
    /// server-side state to extend the session past it, so the cap can't be moved by rotation).
    /// Carries the same <paramref name="sessionId"/> so a refresh re-mints access with the
    /// same <c>sid</c> (D7/D8).</summary>
    MintedToken MintRefresh(DiscordIdentity identity, AuthTier tier, string sessionId);

    /// <summary>Validate a refresh token presented at <c>/auth/session/refresh</c>. Returns the
    /// claims needed to re-mint a fresh access token (incl. the <c>sid</c> to carry through), or
    /// <c>null</c> if the token is invalid, expired (past the 30-day cap), not a refresh token, or
    /// a pre-M4·c token with no <c>sid</c> claim (the D10 clean break).</summary>
    Task<RefreshClaims?> ReadRefreshAsync(string token);

    /// <summary>The validation rules shared with the JwtBearer pipeline so access tokens and
    /// refresh tokens validate identically (issuer, audience=host, lifetime, signing key).</summary>
    TokenValidationParameters ValidationParameters { get; }
}

/// <summary>
/// A just-minted JWT plus its absolute expiry and unique jti — the one point the API knows the TTL
/// exactly and the per-token id it minted. Carried from the mint straight to the login/refresh
/// handoff (so the SPA gets a server-authoritative "when does this token expire" in Increment 7 —
/// the Group E #12 "session TTL" display — without having to base64-decode the JWT's <c>exp</c>
/// claim), AND to the session store (the refresh jti becomes the row's stored <c>CurrentJti</c> —
/// the reuse-detection key). <see cref="ExpiresAt"/> is UTC; <see cref="Jti"/> is a fresh
/// <c>Guid</c> per mint.
/// </summary>
public sealed record MintedToken(string Token, DateTimeOffset ExpiresAt, string Jti);
