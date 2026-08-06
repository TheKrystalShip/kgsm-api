using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>Mints tokens with an arbitrary signing key/host — used to forge a validly-shaped but
/// wrong-signature token, proving the pipeline rejects it, or a validly-signed but sid-LESS token
/// (a pre-M4·c simulation), proving the D10 clean break.</summary>
internal static class TestTokens
{
    public static string MintAccessWithKey(string signingKey, string hostId, KgsmTier tier)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:HostId"] = hostId,
                ["Api:SigningKey"] = signingKey,
            })
            .Build();
        var tokens = new SessionTokenService(ApiOptions.FromConfiguration(config), NullLogger<SessionTokenService>.Instance);
        return tokens.MintAccess(FakeDiscordResolver.Identity, tier, "sid_test_" + Guid.NewGuid().ToString("N")).Token;
    }

    /// <summary>
    /// Forge a validly-SIGNED access token that carries EVERY M4·a claim EXCEPT the M4·c
    /// <see cref="KgsmAuthClaims.SessionId"/> (<c>sid</c>) claim — a faithful pre-M4·c token. JwtBearer
    /// validates it (correct signature/issuer/audience/lifetime), <c>OnTokenValidated</c> reaches the
    /// D10 null-sid check → <c>ctx.Fail("no session id (pre-M4·c token)")</c> → 401. This proves the
    /// clean break: the sid-less tokens the pre-M4·c code mints stop authorizing once Increment 4
    /// ships, with no grandfather path (D10).
    /// </summary>
    public static string MintAccessWithoutSidClaim(string signingKey, string hostId, KgsmTier tier)
    {
        byte[] keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(signingKey));
        var key = new SymmetricSecurityKey(keyBytes);
        var signing = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new("sub", $"discord:{FakeDiscordResolver.Identity.UserId}"),
            new(KgsmAuthClaims.Tier, KgsmTiers.ToWire(tier)),
            new(KgsmAuthClaims.Host, hostId),
            new(KgsmAuthClaims.TokenKind, KgsmTokenKind.Access),
            // NO sid claim — the whole point (pre-M4·c token simulation).
            new(KgsmAuthClaims.Username, FakeDiscordResolver.Identity.Username),
            new(KgsmAuthClaims.Display, FakeDiscordResolver.Identity.Display),
            new("scope", string.Join(' ', FakeDiscordResolver.Identity.Scopes)),
        };
        if (FakeDiscordResolver.Identity.AvatarUrl is not null)
            claims.Add(new Claim(KgsmAuthClaims.Avatar, FakeDiscordResolver.Identity.AvatarUrl));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "kgsm-api",
            Audience = hostId,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(15),
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = signing,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
