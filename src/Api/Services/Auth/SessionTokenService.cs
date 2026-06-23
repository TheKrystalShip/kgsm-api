using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// HMAC-SHA256 session tokens (see <see cref="ISessionTokenService"/>). The signing key is derived
/// once from <c>KGSM_API_AUTH_SIGNING_KEY</c> (SHA-256 → a 256-bit key, so any-length secret works);
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
    // long if the signing key is STABLE (KGSM_API_AUTH_SIGNING_KEY set) — an ephemeral per-process
    // key invalidates every token on restart, see the ctor warning below.
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
                    "KGSM_API_AUTH_SIGNING_KEY is not set — generated an EPHEMERAL signing key. "
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

    public string MintAccess(DiscordIdentity identity, AuthTier tier) =>
        Mint(identity, tier, TokenKind.Access, AccessTtl);

    public string MintRefresh(DiscordIdentity identity, AuthTier tier) =>
        Mint(identity, tier, TokenKind.Refresh, RefreshTtl);

    private string Mint(DiscordIdentity identity, AuthTier tier, string kind, TimeSpan ttl)
    {
        var claims = new List<Claim>
        {
            new("sub", $"discord:{identity.UserId}"),
            new(AuthClaims.Tier, AuthTiers.ToWire(tier)),
            new(AuthClaims.Host, _host),
            new(AuthClaims.TokenKind, kind),
            new(AuthClaims.Username, identity.Username),
            new(AuthClaims.Display, identity.Display),
            new("scope", string.Join(' ', identity.Scopes)),
        };
        if (identity.AvatarUrl is not null)
            claims.Add(new Claim(AuthClaims.Avatar, identity.AvatarUrl));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = _host,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.Add(ttl),
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = _signing,
        };
        return _handler.CreateToken(descriptor);
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

        return new RefreshClaims(identity, SessionClaims.ReadTier(ci));
    }
}
