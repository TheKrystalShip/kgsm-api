using Microsoft.IdentityModel.Tokens;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Mints and validates the host-scoped session JWTs (the M4 bearer decision: stateless JWT, no
/// session table — honors "no user row anywhere" and keeps M5 as the first EF migration). An
/// <em>access</em> token (~15 min) is the bearer on every protected endpoint; a <em>refresh</em>
/// token (8h absolute cap) lets the client rotate the access token with no Discord round-trip until
/// the cap, after which it silently re-bounces through the SSO anchor (architecture.html §3·f).
/// </summary>
public interface ISessionTokenService
{
    /// <summary>Mint a short-lived access bearer for an authorized identity/tier.</summary>
    string MintAccess(DiscordIdentity identity, AuthTier tier);

    /// <summary>Mint the refresh token (its lifetime IS the 8h absolute cap — there is no
    /// server-side state to extend, so the cap can't be moved by rotation).</summary>
    string MintRefresh(DiscordIdentity identity, AuthTier tier);

    /// <summary>Validate a refresh token presented at <c>/auth/session/refresh</c>. Returns the
    /// claims needed to re-mint a fresh access token, or <c>null</c> if the token is invalid,
    /// expired (past the cap), or not a refresh token.</summary>
    Task<RefreshClaims?> ReadRefreshAsync(string token);

    /// <summary>The validation rules shared with the JwtBearer pipeline so access tokens and
    /// refresh tokens validate identically (issuer, audience=host, lifetime, signing key).</summary>
    TokenValidationParameters ValidationParameters { get; }
}
