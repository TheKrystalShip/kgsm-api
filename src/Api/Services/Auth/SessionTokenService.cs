using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// HMAC-SHA256 session tokens (see <see cref="ISessionTokenService"/>). The signing key is derived
/// once from <c>Api__SigningKey</c> (SHA-256 → a 256-bit key, so any-length secret works);
/// when that is blank and auth is on, an ephemeral per-process key is generated and a loud warning is
/// logged (tokens won't survive a restart — fine for dev/smoke, set a stable secret on a real host).
/// </summary>
public sealed class SessionTokenService : ISessionTokenService
{
    private const string Issuer = "kgsm-api";
    private static readonly TimeSpan AccessTtl = TimeSpan.FromMinutes(15);
    // The absolute session cap: how long a user can stay signed in (and silently rotate access
    // tokens) before a fresh Discord login is required. Deliberately long — this is a trusted,
    // role-restricted friends group, so the convenience of not re-authorizing for weeks outweighs
    // a strict refresh window (user directive 2026-06-23). ⚠ A refresh token only survives this
    // long if the signing key is STABLE (Api__SigningKey set) — an ephemeral per-process
    // key invalidates every token on restart, see the ctor warning below.
    //
    // M4·c: this is the MINT-side mirror of ApiOptions.SessionsRefreshAbsoluteDays — the two MUST
    // stay in lockstep (if you change one, change both). The session row's `Expires` column =
    // `Created + this` (D8); a refresh within the window re-mints access with the same `sid`, no
    // sliding window (the cap stays absolute, per the original M4·a lock rationale).
    private static readonly TimeSpan RefreshTtl = TimeSpan.FromDays(30);

    private readonly string _host;
    private readonly SymmetricSecurityKey _key;
    private readonly SigningCredentials _signing;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenValidationParameters ValidationParameters { get; }

    public SessionTokenService(ApiOptions options, ILogger<SessionTokenService> logger)
    {
        _host = options.HostId;

        byte[] keyBytes;
        if (!string.IsNullOrWhiteSpace(options.SigningKey))
        {
            keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(options.SigningKey));
        }
        else
        {
            keyBytes = RandomNumberGenerator.GetBytes(32);
            if (options.AuthEnabled)
                logger.LogWarning(
                    "Api__SigningKey is not set — generated an EPHEMERAL signing key. "
                    + "Sessions will not survive a restart. Set a stable secret on any real host.");
        }

        _key = new SymmetricSecurityKey(keyBytes);
        _signing = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);

        ValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = _host,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
        };
    }

    public MintedToken MintAccess(DiscordIdentity identity, AuthTier tier, string sessionId) =>
        Mint(identity, tier, TokenKind.Access, AccessTtl, sessionId);

    public MintedToken MintRefresh(DiscordIdentity identity, AuthTier tier, string sessionId) =>
        Mint(identity, tier, TokenKind.Refresh, RefreshTtl, sessionId);

    private MintedToken Mint(DiscordIdentity identity, AuthTier tier, string kind, TimeSpan ttl, string sessionId)
    {
        // M4·c rotation — a fresh jti per mint (per-token JWT ID). For refresh tokens this is the
        // reuse-detection key: the session row stores the CURRENT refresh's jti, /refresh rejects a
        // stale jti (old/stolen token → 401). For access tokens the jti is informational only (the
        // per-request validator + the short 15min TTL are enough); we add it uniformly so every token
        // is uniquely identifiable (forensics) without a per-kind code path.
        string jti = Guid.NewGuid().ToString("N");
        var claims = new List<Claim>
        {
            new("sub", $"discord:{identity.UserId}"),
            new(AuthClaims.Tier, AuthTiers.ToWire(tier)),
            new(AuthClaims.Host, _host),
            new(AuthClaims.TokenKind, kind),
            // M4·c — the session id, stable across a session's lifetime (carried by access + refresh;
            // survives access rotation). Absent on a pre-M4·c token (the D10 validator rejects those).
            new(AuthClaims.SessionId, sessionId),
            // M4·c rotation — a unique jti per token (see comment above).
            new(AuthClaims.Jti, jti),
            new(AuthClaims.Username, identity.Username),
            new(AuthClaims.Display, identity.Display),
            new("scope", string.Join(' ', identity.Scopes)),
        };
        if (identity.AvatarUrl is not null)
            claims.Add(new Claim(AuthClaims.Avatar, identity.AvatarUrl));

        DateTime expires = DateTime.UtcNow.Add(ttl);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = _host,
            Subject = new ClaimsIdentity(claims),
            Expires = expires,
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = _signing,
        };
        string token = _handler.CreateToken(descriptor);
        return new MintedToken(token, new DateTimeOffset(expires, TimeSpan.Zero), jti);
    }

    public async Task<RefreshClaims?> ReadRefreshAsync(string token)
    {
        TokenValidationResult result = await _handler.ValidateTokenAsync(token, ValidationParameters);
        if (!result.IsValid || result.ClaimsIdentity is null)
            return null;

        ClaimsIdentity ci = result.ClaimsIdentity;
        if (ci.FindFirst(AuthClaims.TokenKind)?.Value != TokenKind.Refresh)
            return null;

        DiscordIdentity? identity = SessionClaims.ReadIdentity(ci);
        if (identity is null)
            return null;

        // M4·c — carry the session id through so the re-minted access keeps the same `sid` (D7/D8).
        // A pre-M4·c refresh token has no `sid` claim → null → caller 401s (the D10 clean break).
        string? sid = SessionClaims.ReadSessionId(ci);
        if (sid is null)
            return null;

        // M4·c rotation — carry the presented refresh's jti through; the controller validates it
        // against the session row's stored CurrentJti (reuse detection — a stale jti → 401). A
        // pre-rotation refresh token has no `jti` claim → null → caller 401s (the same clean-break
        // posture as the sid check: pre-rotation tokens stop working once this increment ships).
        string? jti = SessionClaims.ReadJti(ci);
        if (jti is null)
            return null;

        return new RefreshClaims(identity, SessionClaims.ReadTier(ci), sid, jti);
    }
}
