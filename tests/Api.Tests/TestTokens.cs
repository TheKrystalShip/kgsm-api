using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.Auth;

using TheKrystalShip.Auth.Minting;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Tokens this node must refuse, each shaped like a real session in every way but one.
/// </summary>
internal static class TestTokens
{
    /// <summary>
    /// A session in the anchor's exact shape — audience, issuer, algorithm, claims — signed by a key
    /// nobody published.
    /// </summary>
    public static string MintByAnUnpublishedAnchor()
    {
        using var stranger = EcdsaSessionSigner.Generate();
        var tokens = new SessionTokenService(
            new SessionTokenOptions(
                Audience: AuthTestFactory.ClusterId,
                AccessLifetime: TimeSpan.FromMinutes(15),
                RefreshLifetime: TimeSpan.FromDays(30),
                Issuer: AuthTestFactory.AnchorIssuer),
            stranger);
        return tokens.MintAccess(TestIdentity.Identity, "sid_test_" + Guid.NewGuid().ToString("N")).Token;
    }

    /// <summary>
    /// A session signed with a symmetric key, audienced to this node — the kind of token this node would
    /// have to be holding a key to accept, and holds none for.
    /// </summary>
    public static string MintSymmetric(string signingKey)
    {
        var claims = new List<Claim>
        {
            new("sub", TestIdentity.Identity.Handle),
            new(KgsmAuthClaims.Host, AuthTestFactory.HostId),
            new(KgsmAuthClaims.TokenKind, KgsmTokenKind.Access),
            new(KgsmAuthClaims.SessionId, "sid_test_" + Guid.NewGuid().ToString("N")),
        };
        var key = new SymmetricSecurityKey(SHA256.HashData(Encoding.UTF8.GetBytes(signingKey)));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "kgsm-api",
            Audience = AuthTestFactory.HostId,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(15),
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>
    /// A session the stand-in anchor genuinely signed, carrying every claim a session does except the
    /// <see cref="KgsmAuthClaims.SessionId"/> — a session nothing could ever end, which this node refuses
    /// to hold open.
    /// </summary>
    public static string MintAnchorSignedWithoutSid()
    {
        var claims = new List<Claim>
        {
            new("sub", TestIdentity.Identity.Handle),
            new(KgsmAuthClaims.Host, AuthTestFactory.ClusterId),
            new(KgsmAuthClaims.TokenKind, KgsmTokenKind.Access),
            new(KgsmAuthClaims.Username, TestIdentity.Identity.Username),
            new(KgsmAuthClaims.Display, TestIdentity.Identity.Display),
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = AuthTestFactory.AnchorIssuer,
            Audience = AuthTestFactory.ClusterId,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(15),
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = AuthTestFactory.AnchorSigner.Credentials,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
