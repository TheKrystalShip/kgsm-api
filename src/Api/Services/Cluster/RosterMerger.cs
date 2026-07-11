using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>What <see cref="RosterMerger.Decide"/> concluded should happen for one incoming gossip member.</summary>
public enum MergeAction
{
    /// <summary>Do nothing — the incoming report is stale, about a locally-disabled peer, or loses the
    /// (incarnation, precedence) ordering to what we already hold.</summary>
    Ignore,

    /// <summary>A node we've never heard of — insert it as hearsay (provisional; never first-hand yet, so it
    /// displays as <c>joining</c> until a probe authenticates it, G3).</summary>
    Insert,

    /// <summary>The incoming report supersedes our row — adopt its state/incarnation/addressing (but never
    /// its first-hand liveness triple, which only this node's own probe writes).</summary>
    Update,

    /// <summary>The report is about US and is non-<c>alive</c> at ≥ our incarnation — refute it by raising
    /// our own incarnation (<see cref="MergeOutcome.RaiseSelfTo"/>) and re-asserting <c>alive</c>. No row.</summary>
    RefuteSelf,
}

/// <summary>The decision plus, for <see cref="MergeAction.RefuteSelf"/>, the incarnation to jump to.</summary>
public readonly record struct MergeOutcome(MergeAction Action, long RaiseSelfTo = 0);

/// <summary>
/// The pure anti-entropy merge core (<c>PLAN-peers.md §2·b</c>, G2/G3) — decides, for ONE incoming gossip
/// <see cref="SyncMember"/> against our current row for that node, what to do. Deliberately side-effect-free
/// and dependency-free so the merge rules are unit-testable in isolation from the DB and HTTP;
/// <see cref="GossipService"/> is the thin shell that reads the current row, calls <see cref="Decide"/>, and
/// applies the outcome through <see cref="PeersStore"/>.
/// </summary>
/// <remarks>
/// <para><b>The ordering (G2).</b> A strictly HIGHER incarnation always wins — that is what lets a returning
/// node beat its own stale <c>dead</c> by re-asserting <c>alive</c> at a bumped incarnation. At EQUAL
/// incarnation, the higher <see cref="GossipState.Precedence"/> wins (a <c>suspect</c> overrides <c>alive</c>,
/// a terminal <c>dead</c>/<c>left</c> overrides both) — with one guard below.</para>
/// <para><b>First-hand evidence beats equal-incarnation hearsay.</b> If we are currently reaching a peer
/// with our own probe (<paramref name="existingFirstHandFresh"/>), we do NOT let an equal-incarnation
/// gossiped <c>suspect</c>/<c>dead</c> override that — our own eyes outrank a peer's hearsay. Only a strictly
/// higher incarnation (i.e. the node itself, or its owner, moving on) overrides first-hand liveness. This is
/// the honest, conservative stand-in for full SWIM indirect-probe corroboration, which is deferred (G5).</para>
/// <para><b>Local disable is absolute.</b> A row we've locally disabled is never resurrected or altered by
/// gossip — disable is this node's own override of the shared-secret trust (§2 #7/#8).</para>
/// </remarks>
public static class RosterMerger
{
    public static MergeOutcome Decide(
        SyncMember incoming,
        PeerEntity? existing,
        string myNodeId,
        long selfIncarnation,
        bool existingFirstHandFresh)
    {
        // A report about ourselves — never a row; refute it if it's stale/negative (G2).
        if (string.Equals(incoming.NodeId, myNodeId, StringComparison.Ordinal))
        {
            if (incoming.State != GossipState.Alive && incoming.Incarnation >= selfIncarnation)
                return new MergeOutcome(MergeAction.RefuteSelf, incoming.Incarnation + 1);
            return new MergeOutcome(MergeAction.Ignore);
        }

        // Never heard of it — insert as provisional hearsay.
        if (existing is null)
            return new MergeOutcome(MergeAction.Insert);

        // Locally disabled — the operator's override wins over anything gossip says.
        if (!existing.Enabled)
            return new MergeOutcome(MergeAction.Ignore);

        return Supersedes(incoming, existing, existingFirstHandFresh)
            ? new MergeOutcome(MergeAction.Update)
            : new MergeOutcome(MergeAction.Ignore);
    }

    private static bool Supersedes(SyncMember incoming, PeerEntity existing, bool existingFirstHandFresh)
    {
        // Strictly newer incarnation always wins — the refutation channel (G2).
        if (incoming.Incarnation > existing.Incarnation)
            return true;
        if (incoming.Incarnation < existing.Incarnation)
            return false;

        // Equal incarnation: my own fresh first-hand contact outranks any equal-incarnation hearsay.
        if (existingFirstHandFresh)
            return false;

        return GossipState.Precedence(incoming.State) > GossipState.Precedence(existing.MembershipState);
    }
}
