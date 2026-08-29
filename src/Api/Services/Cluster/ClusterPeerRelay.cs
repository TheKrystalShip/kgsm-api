using System.Net.Http.Headers;
using TheKrystalShip.Api.Data;

using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Cluster.Messaging;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>The outcome of a server-side node-proxy relay (<see cref="ClusterPeerRelay"/>).</summary>
public enum ClusterRelayStatus
{
    /// <summary>The peer answered 2xx; <see cref="ClusterRelayResult.Payload"/> holds its verbatim body.</summary>
    Ok,
    /// <summary>No such peer row on this node (or the cluster is disabled → empty roster).</summary>
    UnknownNode,
    /// <summary>The peer row exists but is disabled on this node (the admin disable-list).</summary>
    Disabled,
    /// <summary>The peer could not be reached, or answered non-2xx — honest "unreachable," never a fabricated body.</summary>
    Unreachable,
}

/// <summary>The relay result: a status plus, on <see cref="ClusterRelayStatus.Ok"/>, the peer's response
/// body and content type passed through <strong>verbatim</strong> (so the peer's existing DTOs reach the
/// caller unchanged — the whole point of "reuse existing DTOs," PLAN-peers.md P2).</summary>
public sealed record ClusterRelayResult(ClusterRelayStatus Status, string? Payload, string? ContentType);

/// <summary>
/// The <strong>server-side capacity fan-out</strong> — the one node-proxied path (PLAN-peers.md P2). Given a
/// peer's roster-row id, it mints a cluster service token and GETs that peer's <c>/api/v1/peers/self/{leaf}</c>
/// surface, returning the response verbatim. This is consumed by the on-demand "find a node with capacity"
/// logic and (later) the assistant — <strong>never by the SPA</strong>, which reads a peer's resources
/// directly over its own native session (per-node pages stay client-side; §8).
/// </summary>
/// <remarks>
/// Same node-to-node addressing (the peer's pinned candidate) and reused named <see cref="System.Net.Http.HttpClient"/>
/// (<see cref="OutboxDrainer.HttpClientName"/>, 10s timeout) as the vouch relay and the outbox drainer, so
/// every node-to-node call shares one bounded, mint-authenticated client. A down peer degrades to
/// <see cref="ClusterRelayStatus.Unreachable"/> — never a 500 (the P2 honesty rule).
/// </remarks>
public sealed class ClusterPeerRelay(
    PeersStore peers,
    IClusterTokenService clusterTokens,
    IHttpClientFactory httpClientFactory,
    ApiOptions options,
    ILogger<ClusterPeerRelay> logger)
{
    /// <summary>Relay a GET to peer <paramref name="peerId"/>'s <c>/peers/self/{<paramref name="leaf"/>}</c>
    /// (<c>leaf</c> ∈ resources|capabilities|library — an internal constant, never user input). The peer's body
    /// is returned untouched on success.</summary>
    public async Task<ClusterRelayResult> RelayGetAsync(string peerId, string leaf, CancellationToken ct)
    {
        // A non-cluster node has an empty roster (PeersStore never seeds rows without ClusterEnabled), so this
        // reads as an explicit "unknown node," matching the vouch relay's early-out.
        if (!options.ClusterEnabled)
            return new ClusterRelayResult(ClusterRelayStatus.UnknownNode, null, null);

        PeerEntity? peer = await peers.GetAsync(peerId, ct).ConfigureAwait(false);
        if (peer is null)
            return new ClusterRelayResult(ClusterRelayStatus.UnknownNode, null, null);
        if (!peer.Enabled)
            return new ClusterRelayResult(ClusterRelayStatus.Disabled, null, null);

        MintedClusterToken token = clusterTokens.Mint();
        // The pinned candidate — the same address GossipWorker/OutboxDrainer/the vouch relay reach this
        // peer's own HTTP surface on.
        string url = $"{peer.Url.TrimEnd('/')}/api/v1/peers/self/{leaf}";

        HttpClient http = httpClientFactory.CreateClient(OutboxDrainer.HttpClientName);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("cluster relay GET /self/{Leaf} to peer '{PeerId}' returned HTTP {Status}",
                    leaf, peerId, (int)response.StatusCode);
                return new ClusterRelayResult(ClusterRelayStatus.Unreachable, null, null);
            }

            string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            return new ClusterRelayResult(ClusterRelayStatus.Ok, payload, contentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "cluster relay GET /self/{Leaf} to peer '{PeerId}' failed", leaf, peerId);
            return new ClusterRelayResult(ClusterRelayStatus.Unreachable, null, null);
        }
    }
}
