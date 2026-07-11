namespace TheKrystalShip.Api.Data;

/// <summary>
/// One row of the cluster membership roster (<c>PLAN-peers.md</c> §2 #12, P0 — peer foundation) — the
/// durable "who else is in this cluster" set every peer-foundation piece reads through
/// <see cref="Services.Cluster.PeersStore"/>: the disable-list gate, the outbox fan-out target provider,
/// the latency poller, and <c>PeersController</c>.
/// <para>
/// A cluster is masterless (<c>PLAN-peers.md</c> §2·b) — every node keeps its OWN copy of this roster;
/// there is no shared/authoritative table. A row is added by the join-via-seed handshake (an admin pastes
/// one member's URL) and later, once the gossip milestone lands, converges automatically from peers this
/// node has never been directly told about.
/// </para>
/// <para>
/// <b>EnsureCreated, NOT a migration</b> (the project's dev authority — see <see cref="AppDbContext"/>):
/// because <c>EnsureCreated</c> no-ops on an existing DB, <see cref="Services.Cluster.PeersStore"/> ALSO
/// issues an idempotent <c>CREATE TABLE IF NOT EXISTS</c> so this table appears on an already-deployed host
/// without wiping the append-only audit log that shares the DB.
/// </para>
/// </summary>
public sealed class PeerEntity
{
    /// <summary>
    /// This node's own local identifier for the row (primary key) — distinct from <see cref="NodeId"/>,
    /// which is the peer's OWN stable identity. Locally assigned when the row is added (the handshake), so
    /// it is stable even if the far side's advertised <see cref="Url"/> later changes.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The peer's advertised, browser-reachable client URL (<c>PLAN-peers.md</c> §2 #13a) — what the SPA
    /// uses to talk to that node directly (e.g. <c>https://node-b:8097</c>). A node behind LAN/VPN may
    /// gossip over a different address than this one, which is why <see cref="GossipUrl"/> exists
    /// separately; when the two coincide only this field is set.
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// The peer's node-to-node URL, when it differs from <see cref="Url"/> (<c>PLAN-peers.md</c> §2 #13a) —
    /// e.g. an internal/VPN address the gossip and message-bus traffic rides, while <see cref="Url"/> stays
    /// the browser-reachable one. <see langword="null"/> ⇒ collapse to the same address as <see cref="Url"/>.
    /// </summary>
    public string? GossipUrl { get; set; }

    /// <summary>Operator-assigned display label for this peer (e.g. "Gaming Box"). <see langword="null"/> ⇒
    /// the SPA falls back to <see cref="NodeId"/> or the URL.</summary>
    public string? Nickname { get; set; }

    /// <summary>
    /// The peer's own stable node id, as reported by its <c>GET /api/v1/peers/identity</c>
    /// (<c>PLAN-peers.md</c> §2 #2) — the value a cluster service token's <c>iss</c> carries for that node,
    /// so this is the lookup key inbound calls are attributed against (see
    /// <see cref="Services.Cluster.PeersStore.GetByNodeIdAsync"/>). Not necessarily equal to <see cref="Id"/>
    /// (this row's own local key).
    /// </summary>
    public string NodeId { get; set; } = "";

    /// <summary>
    /// This peer's incarnation number (<c>PLAN-peers.md</c> §2·b, G2) — a monotonic counter only the peer
    /// itself may raise, used by the (not-yet-built) gossip layer to order conflicting reports about a node
    /// and let a node refute a false <c>suspect</c>/<c>dead</c> about itself. Unused until gossip lands
    /// (P0.5); defaults to <c>0</c>.
    /// </summary>
    public long Incarnation { get; set; }

    /// <summary>
    /// This peer's last-observed liveness: <c>"reachable"</c>, <c>"unreachable"</c>, or <c>"unknown"</c>
    /// (the honest cold-start default before the first latency poll ever completes — never fabricated as
    /// reachable). Written by <see cref="Services.Cluster.PeerLatencyPoller"/>.
    /// </summary>
    public string Status { get; set; } = "unknown";

    /// <summary>Round-trip latency of the last successful poll, in milliseconds. <see langword="null"/>
    /// when never successfully probed.</summary>
    public int? LatencyMs { get; set; }

    /// <summary>When this peer was last successfully reached (UTC). <see langword="null"/> when never
    /// successfully probed.</summary>
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>
    /// The peer's route-version (<c>PLAN-peers.md</c> §2 #10, e.g. <c>"v1"</c>) as reported at handshake
    /// time — cluster membership matches on this, not on build version, so a rolling upgrade across a
    /// heterogeneous-build mesh still works.
    /// </summary>
    public string ApiVersion { get; set; } = "";

    /// <summary>
    /// The disable-list flag (<c>PLAN-peers.md</c> §0 #8, §2 #7/#8) — the ONLY local override to the
    /// shared-secret trust boundary. <see langword="true"/> (the default) means this peer's cluster
    /// service tokens are accepted; flipping it to <see langword="false"/> rejects them (<c>403
    /// peer_disabled</c>) without removing the row, so a disabled peer can be re-enabled with one click.
    /// Absence from this table entirely is NOT rejection — only an explicit <see langword="false"/> here is.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
