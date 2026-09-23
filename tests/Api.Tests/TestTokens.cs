using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.KGSM.Auth;

using TheKrystalShip.KGSM.Auth.Sessions;

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
    public static string MintByAnUnpublishedAnchor(KgsmTier tier)
    {
        using var stranger = EcdsaSessionSigner.Generate();
        var tokens = new SessionTokenService(
            new SessionTokenOptions(
                HostId: AuthTestFactory.ClusterId,
                SigningKey: "",
                AccessLifetime: TimeSpan.FromMinutes(15),
                RefreshLifetime: TimeSpan.FromDays(30),
                Issuer: AuthTestFactory.AnchorIssuer),
            logger: null,
            signer: stranger);
        return tokens.MintAccess(FakeDiscordResolver.Identity, tier, "sid_test_" + Guid.NewGuid().ToString("N")).Token;
    }

    /// <summary>
    /// A session a surface signed for itself with a symmetric key, audienced to this node — the kind of
    /// token this node would have to be holding a key to accept, and holds none for.
    /// </summary>
    public static string MintSymmetric(string signingKey, KgsmTier tier)
    {
        var tokens = new SessionTokenService(new SessionTokenOptions(
            HostId: AuthTestFactory.HostId,
            SigningKey: signingKey,
            AccessLifetime: TimeSpan.FromMinutes(15),
            RefreshLifetime: TimeSpan.FromDays(30),
            Issuer: "kgsm-api"));
        return tokens.MintAccess(FakeDiscordResolver.Identity, tier, "sid_test_" + Guid.NewGuid().ToString("N")).Token;
    }

    /// <summary>
    /// A session the stand-in anchor genuinely signed, carrying every claim a session does except the
    /// <see cref="KgsmAuthClaims.SessionId"/> — a session nothing could ever end, which this node refuses
    /// to hold open.
    /// </summary>
    public static string MintAnchorSignedWithoutSid(KgsmTier tier)
    {
        var claims = new List<Claim>
        {
            new("sub", FakeDiscordResolver.Identity.Handle),
            new(KgsmAuthClaims.Tier, KgsmTiers.ToWire(tier)),
            new(KgsmAuthClaims.Host, AuthTestFactory.ClusterId),
            new(KgsmAuthClaims.TokenKind, KgsmTokenKind.Access),
            new(KgsmAuthClaims.Username, FakeDiscordResolver.Identity.Username),
            new(KgsmAuthClaims.Display, FakeDiscordResolver.Identity.Display),
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
