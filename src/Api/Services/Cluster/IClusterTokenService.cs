namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Mints and validates the cluster service token — the node-to-node auth bearer described in
/// <c>PLAN-peers.md §3</c> and <c>docs/cluster-message-bus-plan.md §9</c>. A service token is a
/// short-lived HMAC-signed JWT (<c>sub=node:&lt;NodeId&gt;</c>, <c>iss=&lt;NodeId&gt;</c>,
/// <c>aud=cluster</c>) that proves "I am a member of this cluster," nothing more — it is not a user
/// identity and carries no tier. This is the seam every cross-node call (peer queries, the
/// <c>/peers/inbox</c> message bus, the SSO vouch endpoint) authenticates through; later phases add
/// the <c>iss</c>-is-an-enabled-peer check on top of a successful <see cref="ValidateAsync"/>.
/// </summary>
public interface IClusterTokenService
{
    /// <summary>
    /// Mint a fresh service token for THIS node. Throws <see cref="InvalidOperationException"/> when
    /// <see cref="ApiOptions.ClusterEnabled"/> is <see langword="false"/> (a blank
    /// <c>Api__ClusterSecret</c> means this host cannot present itself as a cluster member at
    /// all — minting would produce a token nobody, including this same host, could ever validate).
    /// </summary>
    MintedClusterToken Mint();

    /// <summary>
    /// Validate a presented service token: signature (against the current secret, then the previous
    /// one during a rotation overlap — <c>PLAN-peers.md §2</c> #9), <c>aud == "cluster"</c>, and
    /// lifetime. Returns the calling node's identity (read from <c>iss</c>) on success,
    /// <see langword="null"/> on ANY failure — bad signature, wrong audience, expired, malformed, or
    /// the cluster secret unconfigured. <b>Fail-closed</b>: never throws, never partially trusts a
    /// token (<c>PLAN-peers.md §2</c> #26).
    /// </summary>
    Task<ClusterPrincipal?> ValidateAsync(string token);
}

/// <summary>
/// A just-minted cluster service token plus its absolute expiry — the mint-time TTL, precise, so a
/// caller never has to decode the JWT to know when to re-mint. <see cref="ExpiresAt"/> is UTC.
/// </summary>
public sealed record MintedClusterToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// The authenticated identity of a cluster peer that presented a valid service token — just the
/// node id (read from the token's <c>iss</c> claim, which by construction equals its <c>sub</c>'s
/// <c>node:</c> suffix). Later phases (the inbox handler, the peer registry) layer the "is this
/// node an ENABLED peer" and "does <c>from</c> match <c>iss</c>" checks on top of this.
/// </summary>
public sealed record ClusterPrincipal(string NodeId);
