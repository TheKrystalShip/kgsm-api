using System.Security.Claims;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Reads a refresh token the cluster's auth anchor issued, so this node can carry a session it did
/// not mint through a window in which the anchor cannot be reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a member re-mints at all.</b> An access bearer lives fifteen minutes. If refreshing were
/// the anchor's alone, an anchor outage would stop every member accepting anybody a quarter of an
/// hour later — the whole panel, not just signing in — which is the serving-path dependency the
/// design exists to avoid. Refresh stays the anchor's in the ordinary case; this is the path for when
/// it is not there.
/// </para>
/// <para>
/// <b>What it deliberately does not hand this node.</b> The token read here is <em>anchor-signed</em>,
/// so this node can verify it and cannot forge one. Standing is resolved from this node's own replica
/// rather than from the token's tier claim. What is minted from it is an access token with
/// <em>this node's</em> key, audienced to <em>this node</em> — valid here and refused by every other
/// member. And nothing mints a replacement refresh token, so the absolute cap on the session stays
/// the anchor's: a member spends what the anchor already granted and cannot extend it.
/// </para>
/// <para>
/// That adds no capability a member did not already have — it holds its own signing key and could
/// always mint itself a local token. What it cannot do is produce anything another member accepts,
/// which is the property asymmetric signing protects.
/// </para>
/// </remarks>
public sealed class ClusterSessionExchange(
    ISessionTokenService tokens,
    IClusterSessionKeys clusterKeys,
    ApiOptions options)
{
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>A refresh token the anchor issued, read and found good.</summary>
    /// <param name="Identity">Who it names. Resolved against this node's replica by the caller.</param>
    /// <param name="SessionId">The session, as the anchor knows it. Carried through unchanged so a
    /// revoke naming it reaches what this node mints from it.</param>
    /// <param name="Expires">The anchor's absolute cap on the session. Never extended here.</param>
    public sealed record ClusterRefresh(KgsmIdentity Identity, string SessionId, DateTimeOffset Expires);

    /// <summary>
    /// Read a presented refresh token, or <see langword="null"/> when it is not a cluster refresh
    /// token this node can accept.
    /// </summary>
    /// <remarks>
    /// Every reason to refuse collapses to one answer on purpose: an expired token, one signed with a
    /// key nobody published, one carrying this node's own audience, an access token presented in a
    /// refresh token's place, and a member that has simply not heard who holds the accounts yet are
    /// all "no". Separate answers here would let a caller learn which of those it had.
    /// </remarks>
    public async Task<ClusterRefresh?> ReadAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        TokenValidationResult result;
        try
        {
            result = await _handler.ValidateTokenAsync(
                token,
                ClusterSessionValidation.Accepting(tokens.ValidationParameters, clusterKeys))
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (!result.IsValid || result.ClaimsIdentity is not { } claims)
            return null;

        // The anchor's, not this node's. A member's own refresh token goes through the ordinary
        // rotation path, which extends the session — the whole point of this one is that it cannot.
        if (!ClusterSessionValidation.IsClusterSession(claims, options.HostId))
            return null;

        // An access token buys nothing here. It lives fifteen minutes and a refresh token lives
        // weeks, so accepting one in the other's place would turn the short-lived bearer into a
        // credential for the length of the session.
        if (claims.FindFirst(KgsmAuthClaims.TokenKind)?.Value != KgsmTokenKind.Refresh)
            return null;

        if (SessionClaims.ReadIdentity(claims) is not { } identity)
            return null;

        if (SessionClaims.ReadSessionId(claims) is not { Length: > 0 } sessionId)
            return null;

        // The anchor's expiry, read off the token it signed. This is the cap this node honours and
        // the one it writes onto the row it keeps, so nothing here can outlive what the anchor
        // granted.
        if (result.SecurityToken is not JsonWebToken jwt)
            return null;

        return new ClusterRefresh(identity, sessionId, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
    }
}
