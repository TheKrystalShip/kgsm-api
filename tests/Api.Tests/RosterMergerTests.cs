using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit-level coverage of <see cref="RosterMerger.Decide"/> (<c>PLAN-peers.md §2·b</c>, G2/G3) — the pure
/// anti-entropy merge core, tested directly with no DB/HTTP: incarnation ordering, precedence at equal
/// incarnation, first-hand-fresh outranking equal-incarnation hearsay, self-refutation, unknown-node
/// insertion, and the local-disable override. <see cref="GossipConvergenceTests"/> covers the same rules
/// end-to-end through real nodes; this file pins the decision table in isolation.
/// </summary>
public sealed class RosterMergerTests
{
    private const string MyNodeId = "node-a";

    private static PeerEntity Peer(
        string nodeId = "node-b", long incarnation = 0, string state = GossipState.Alive,
        bool enabled = true, string status = "unknown", DateTimeOffset? lastSeen = null) => new()
    {
        Id = "peer_" + nodeId,
        Url = $"http://{nodeId}",
        NodeId = nodeId,
        Incarnation = incarnation,
        MembershipState = state,
        Enabled = enabled,
        Status = status,
        LastSeen = lastSeen,
        ApiVersion = "v1",
    };

    private static SyncMember Member(
        string nodeId = "node-b", long incarnation = 0, string state = GossipState.Alive) =>
        new(nodeId, [new NodeCandidate($"https://{nodeId}", Client: true)], incarnation, state, "v1");

    [Fact]
    public void HigherIncarnation_Wins_Update()
    {
        SyncMember incoming = Member(incarnation: 5, state: GossipState.Alive);
        PeerEntity existing = Peer(incarnation: 3, state: GossipState.Suspect);

        MergeOutcome outcome = RosterMerger.Decide(incoming, existing, MyNodeId, selfIncarnation: 0, existingFirstHandFresh: false);

        Assert.Equal(MergeAction.Update, outcome.Action);
    }

    [Fact]
    public void LowerIncarnation_Loses_Ignore()
    {
        SyncMember incoming = Member(incarnation: 2, state: GossipState.Dead);
        PeerEntity existing = Peer(incarnation: 4, state: GossipState.Alive);

        MergeOutcome outcome = RosterMerger.Decide(incoming, existing, MyNodeId, selfIncarnation: 0, existingFirstHandFresh: false);

        Assert.Equal(MergeAction.Ignore, outcome.Action);
    }

    [Fact]
    public void EqualIncarnation_WorsePrecedence_NotFresh_Update()
    {
        SyncMember incoming = Member(incarnation: 7, state: GossipState.Suspect);
        PeerEntity existing = Peer(incarnation: 7, state: GossipState.Alive);

        MergeOutcome outcome = RosterMerger.Decide(incoming, existing, MyNodeId, selfIncarnation: 0, existingFirstHandFresh: false);

        Assert.Equal(MergeAction.Update, outcome.Action);
    }

    [Fact]
    public void EqualIncarnation_WorsePrecedence_ButFirstHandFresh_Ignore()
    {
        SyncMember incoming = Member(incarnation: 7, state: GossipState.Suspect);
        PeerEntity existing = Peer(incarnation: 7, state: GossipState.Alive);

        MergeOutcome outcome = RosterMerger.Decide(incoming, existing, MyNodeId, selfIncarnation: 0, existingFirstHandFresh: true);

        Assert.Equal(MergeAction.Ignore, outcome.Action);
    }

    [Fact]
    public void EqualIncarnation_SamePrecedence_NoSupersede_Ignore()
    {
        SyncMember incoming = Member(incarnation: 3, state: GossipState.Alive);
        PeerEntity existing = Peer(incarnation: 3, state: GossipState.Alive);

        MergeOutcome outcome = RosterMerger.Decide(incoming, existing, MyNodeId, selfIncarnation: 0, existingFirstHandFresh: false);

        Assert.Equal(MergeAction.Ignore, outcome.Action);
    }

    [Fact]
    public void ReportAboutSelf_DeadAtOrAboveSelfIncarnation_RefutesAndRaises()
    {
        SyncMember incoming = Member(nodeId: MyNodeId, incarnation: 5, state: GossipState.Dead);

        MergeOutcome outcome = RosterMerger.Decide(incoming, existing: null, MyNodeId, selfIncarnation: 5, existingFirstHandFresh: false);

        Assert.Equal(MergeAction.RefuteSelf, outcome.Action);
        Assert.Equal(6, outcome.RaiseSelfTo);
    }

    [Fact]
    public void ReportAboutSelf_Alive_Ignore()
    {
        SyncMember incoming = Member(nodeId: MyNodeId, incarnation: 9, state: GossipState.Alive);

        MergeOutcome outcome = RosterMerger.Decide(incoming, existing: null, MyNodeId, selfIncarnation: 0, existingFirstHandFresh: false);

        Assert.Equal(MergeAction.Ignore, outcome.Action);
    }

    [Fact]
    public void UnknownNode_Insert()
    {
        SyncMember incoming = Member(nodeId: "node-c", incarnation: 0, state: GossipState.Alive);

        MergeOutcome outcome = RosterMerger.Decide(incoming, existing: null, MyNodeId, selfIncarnation: 0, existingFirstHandFresh: false);

        Assert.Equal(MergeAction.Insert, outcome.Action);
    }

    [Fact]
    public void LocallyDisabled_Ignore_EvenWhenIncomingWouldOtherwiseSupersede()
    {
        SyncMember incoming = Member(incarnation: 9, state: GossipState.Alive);
        PeerEntity existing = Peer(incarnation: 1, state: GossipState.Dead, enabled: false);

        MergeOutcome outcome = RosterMerger.Decide(incoming, existing, MyNodeId, selfIncarnation: 0, existingFirstHandFresh: false);

        Assert.Equal(MergeAction.Ignore, outcome.Action);
    }
}
