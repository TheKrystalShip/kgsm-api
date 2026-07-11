namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The ephemeral membership-gossip wire contract (<c>PLAN-peers.md §2·b</c>, G4 / P0.5) carried by
/// <c>POST /api/v1/peers/sync</c> — a best-effort, cluster-token-authed roster exchange that is
/// DELIBERATELY separate from the durable message-bus envelope (<c>ClusterEnvelope</c>): a sync round is
/// fire-and-forget anti-entropy, never persisted to the outbox, never retried to a corpse. Push-pull in one
/// round-trip — the caller POSTs its whole roster view (<see cref="SyncRequest"/>) and the receiver merges
/// it and returns its own (<see cref="SyncResponse"/>) for the caller to merge back.
/// </summary>
/// <param name="NodeId">The member's own stable node id — the merge key and, for the sender's own
/// self-entry, the identity a refutation is asserted under.</param>
/// <param name="Url">The member's advertised, browser-reachable client URL (may be empty when a node
/// doesn't know/advertise its own — <c>PLAN-peers.md §2</c> #13a).</param>
/// <param name="GossipUrl">The member's node-to-node URL when it differs from <see cref="Url"/>; null ⇒
/// same as <see cref="Url"/>.</param>
/// <param name="Incarnation">The member's incarnation (owned by that member) — the primary merge ordering
/// key; a strictly higher value always wins (G2 refutation).</param>
/// <param name="State">The member's SWIM membership state (see <see cref="Services.Cluster.GossipState"/>):
/// alive/suspect/dead/left. Never the derived "joining" (that is a local read-side display only).</param>
/// <param name="ApiVersion">The member's route version, propagated so a gossip-discovered node's version is
/// known before this node authenticates it first-hand; may be empty when not yet known.</param>
public sealed record SyncMember(
    string NodeId, string Url, string? GossipUrl, long Incarnation, string State, string ApiVersion);

/// <summary>POST /peers/sync request — the caller's full roster view, INCLUDING its own self-entry.</summary>
/// <param name="From">The caller's node id (equals the cluster service token's <c>iss</c>).</param>
/// <param name="Members">Every member the caller knows: its self-entry plus its peers.</param>
public sealed record SyncRequest(string From, IReadOnlyList<SyncMember> Members);

/// <summary>POST /peers/sync response — the receiver's full roster view for the caller to merge (the pull
/// half of push-pull). Same shape as the request.</summary>
public sealed record SyncResponse(string From, IReadOnlyList<SyncMember> Members);
