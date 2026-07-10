using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// HMAC-SHA256 cluster service tokens (see <see cref="IClusterTokenService"/>). Mirrors
/// <see cref="Services.Auth.SessionTokenService"/>'s crypto idiom — <see cref="JsonWebTokenHandler"/>,
/// a <see cref="SymmetricSecurityKey"/> derived via <c>SHA256(secret)</c> so any-length secret works,
/// HMAC-SHA256 signing — but is otherwise a distinct seam: the signing key is
/// <see cref="ApiOptions.ClusterSecret"/>, never <see cref="ApiOptions.SigningKey"/> (the two secrets
/// are deliberately independent, <c>PLAN-peers.md §3</c>), and the token proves cluster membership,
/// not a user identity or tier.
/// <para>
/// Blank <see cref="ApiOptions.ClusterSecret"/> ⇒ <see cref="ApiOptions.ClusterEnabled"/> is
/// <see langword="false"/> ⇒ this host is simply not part of a cluster: <see cref="Mint"/> throws
/// and <see cref="ValidateAsync"/> always returns <see langword="null"/>. That is the intended,
/// unconfigured-by-default posture (opt-in, like <see cref="ApiOptions.AssistantRelaySecret"/>) — not
/// a misconfiguration, so construction logs at <see cref="LogLevel.Information"/>, not a warning.
/// </para>
/// </summary>
public sealed class ClusterTokenService : IClusterTokenService
{
    /// <summary>The service token's audience — every cluster token carries this; validation rejects
    /// any other <c>aud</c>. Fixed, unlike the token's <c>iss</c> (see <see cref="ValidateAsync"/>).</summary>
    private const string ClusterAudience = "cluster";

    /// <summary>Deliberately short — a service token is minted fresh per outbound call (or per
    /// drainer tick), never cached across requests, so a tight TTL bounds a leaked token's blast
    /// radius to a minute rather than the ~15-min user access-token TTL.</summary>
    private static readonly TimeSpan ServiceTokenTtl = TimeSpan.FromSeconds(60);

    private readonly string _nodeId;
    private readonly bool _enabled;
    private readonly SigningCredentials? _signing;
    private readonly TokenValidationParameters? _currentValidation;
    private readonly TokenValidationParameters? _previousValidation;
    private readonly JsonWebTokenHandler _handler = new();

    public ClusterTokenService(ApiOptions options, ILogger<ClusterTokenService> logger)
    {
        _nodeId = options.NodeId;
        _enabled = options.ClusterEnabled;

        if (!_enabled)
        {
            logger.LogInformation(
                "KGSM_API_CLUSTER_SECRET is not set — this node is not part of a cluster. " +
                "The cluster service-token seam (mint + validate) stays dormant.");
            return;
        }

        SymmetricSecurityKey currentKey = DeriveKey(options.ClusterSecret);
        _signing = new SigningCredentials(currentKey, SecurityAlgorithms.HmacSha256);
        _currentValidation = BuildValidationParameters(currentKey);

        bool rotating = !string.IsNullOrWhiteSpace(options.ClusterSecretPrevious);
        if (rotating)
            _previousValidation = BuildValidationParameters(DeriveKey(options.ClusterSecretPrevious));

        logger.LogInformation(
            "Cluster enabled as node {NodeId}{Rotating}.",
            _nodeId, rotating ? " (accepting the previous secret during a rotation overlap window)" : "");
    }

    /// <inheritdoc/>
    public MintedClusterToken Mint()
    {
        if (!_enabled || _signing is null)
            throw new InvalidOperationException(
                "The cluster is not configured (KGSM_API_CLUSTER_SECRET is blank) — cannot mint a service token.");

        DateTime expires = DateTime.UtcNow.Add(ServiceTokenTtl);
        var descriptor = new SecurityTokenDescriptor
        {
            // iss = this node's id (PLAN-peers.md §3) — dynamic per-node, NOT a fixed issuer string;
            // the receiving node reads it back in ValidateAsync to learn who is calling.
            Issuer = _nodeId,
            Audience = ClusterAudience,
            Subject = new ClaimsIdentity([new Claim("sub", $"node:{_nodeId}")]),
            Expires = expires,
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = _signing,
        };
        string token = _handler.CreateToken(descriptor);
        return new MintedClusterToken(token, new DateTimeOffset(expires, TimeSpan.Zero));
    }

    /// <inheritdoc/>
    public async Task<ClusterPrincipal?> ValidateAsync(string token)
    {
        if (!_enabled || _currentValidation is null)
            return null;

        ClusterPrincipal? principal = await TryValidateAsync(token, _currentValidation).ConfigureAwait(false);
        if (principal is not null)
            return principal;

        // Rotation overlap (PLAN-peers.md §2 #9): a peer that hasn't rolled to the new secret yet
        // still signs with the previous one — accept it too until every node has rotated.
        if (_previousValidation is not null)
            principal = await TryValidateAsync(token, _previousValidation).ConfigureAwait(false);

        return principal;
    }

    private async Task<ClusterPrincipal?> TryValidateAsync(string token, TokenValidationParameters parameters)
    {
        // Fail-closed: ANY exception (malformed token, library edge case) collapses to null, never
        // propagates — a bad token must never crash the caller into an ambiguous state.
        try
        {
            TokenValidationResult result = await _handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
            if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
                return null;

            // Read Issuer straight off the parsed token rather than FindFirst("iss") on the resulting
            // ClaimsIdentity — robust regardless of any inbound claim-type mapping, and this IS the
            // node id per the mint-side descriptor.Issuer assignment above.
            string nodeId = jwt.Issuer;
            return string.IsNullOrWhiteSpace(nodeId) ? null : new ClusterPrincipal(nodeId);
        }
        catch
        {
            return null;
        }
    }

    private static SymmetricSecurityKey DeriveKey(string secret) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    // ValidateIssuer is deliberately OFF: unlike SessionTokenService's fixed "kgsm-api" issuer, a
    // cluster token's iss IS the caller's node id — there is no single ValidIssuer to check it
    // against, so ValidateAsync reads it back post-validation instead (PLAN-peers.md §3).
    private static TokenValidationParameters BuildValidationParameters(SymmetricSecurityKey key) => new()
    {
        ValidateIssuer = false,
        ValidateAudience = true,
        ValidAudience = ClusterAudience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = key,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
    };
}
