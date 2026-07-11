namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The SWIM-derived membership states the gossip layer converges on (<c>PLAN-peers.md §2·b</c>, G2/G3) and
/// their conflict-ordering precedence. Stored in <see cref="Data.PeerEntity.MembershipState"/>, exchanged in
/// the <c>/peers/sync</c> payload, ordered by <c>(incarnation, precedence)</c> in <see cref="RosterMerger"/>.
/// </summary>
/// <remarks>
/// <b>Two liveness axes, never conflated</b> (the codebase's metric-presence ≠ status rule, applied to the
/// mesh): <see cref="Data.PeerEntity.Status"/> is this node's FIRST-HAND probe result
/// (reachable/unreachable/unknown, poller-owned); these membership states are what the mesh CONVERGED on.
/// <see cref="Joining"/> is deliberately NOT a stored/gossiped state — it is a DERIVED display value
/// (<see cref="Alive"/> per gossip but never yet reached first-hand, <see cref="Data.PeerEntity.LastSeen"/>
/// null), so an unverified hearsay peer is never shown a plain <c>alive</c> (G3) yet the state that
/// propagates through this node stays the honest <c>alive</c> it heard — no demotion-on-relay.
/// </remarks>
public static class GossipState
{
    /// <summary>Confirmed a member — either first-hand authenticated here, or vouched by the mesh.</summary>
    public const string Alive = "alive";

    /// <summary>This node's own first-hand probes are failing; not yet escalated to <see cref="Dead"/>.
    /// A local hypothesis (indirect-probe corroboration is deferred, G5) — never adopted from gossip to
    /// override a peer this node can currently reach.</summary>
    public const string Suspect = "suspect";

    /// <summary>Presumed gone (suspect past the suspect timeout). Refutable: the node itself, on return,
    /// beats this with a higher <see cref="Data.PeerEntity.Incarnation"/> (§2·b G2 restart handling).</summary>
    public const string Dead = "dead";

    /// <summary>Gracefully departed. Terminal like <see cref="Dead"/> for reaping; distinguished so a
    /// clean leave reads differently from a crash.</summary>
    public const string Left = "left";

    /// <summary>DERIVED display only (not stored, not gossiped): gossip says <see cref="Alive"/> but this
    /// node has never reached the peer first-hand. See the type remarks (G3).</summary>
    public const string Joining = "joining";

    /// <summary>
    /// Conflict precedence at EQUAL incarnation — higher wins (<c>PLAN-peers.md §2·b</c>, G2). A suspicion
    /// at the same incarnation overrides <c>alive</c>; a terminal <c>dead</c>/<c>left</c> overrides both.
    /// Refutation across incarnations is handled separately in <see cref="RosterMerger"/> (a strictly higher
    /// incarnation always wins regardless of state — that is how a returning node beats its own <c>dead</c>).
    /// </summary>
    public static int Precedence(string state) => state switch
    {
        Alive => 0,
        Suspect => 1,
        Left => 2,
        Dead => 3,
        _ => 0,
    };

    /// <summary>Terminal states eligible for reaping once <see cref="ApiOptions.ClusterReapMs"/> elapses.</summary>
    public static bool IsTerminal(string state) => state is Dead or Left;

    /// <summary>
    /// The DERIVED display membership a consumer sees (<c>GET /peers</c>): a peer that gossip reports
    /// <see cref="Alive"/> but this node has never reached first-hand (<paramref name="lastSeen"/> null) is
    /// shown <see cref="Joining"/> — hearsay-provisional (G3); every other case shows the converged state
    /// verbatim. This is a pure read-side projection: the STORED/gossiped state stays the honest
    /// <see cref="Alive"/>, so relaying it onward never demotes it.
    /// </summary>
    public static string Display(string membershipState, DateTimeOffset? lastSeen) =>
        membershipState == Alive && lastSeen is null ? Joining : membershipState;
}
