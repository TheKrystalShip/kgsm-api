using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The stateful shell around the pure <see cref="RosterMerger"/> (<c>PLAN-peers.md §2·b</c>, P0.5): merges an
/// incoming gossip roster into ours, projects our roster into the <c>/peers/sync</c> wire shape (self-entry +
/// peers), and advances the failure timers (<c>alive→suspect→dead→reap</c>). Shared by both gossip paths —
/// the <c>POST /peers/sync</c> receive side (<see cref="Controllers.PeersController"/>) and the outbound
/// <see cref="GossipWorker"/> loop — so the merge/ordering rules live in exactly one place.
/// </summary>
public sealed class GossipService
{
    // How recently a first-hand probe must have succeeded for a peer's liveness to outrank equal-incarnation
    // gossip (RosterMerger's existingFirstHandFresh). A few poll intervals (the poller runs at 10s) — long
    // enough to bridge a missed tick, short enough that a truly-gone peer stops being "fresh" quickly.
    private static readonly TimeSpan FirstHandFreshWindow = TimeSpan.FromSeconds(30);

    private readonly PeersStore _peers;
    private readonly SelfIncarnation _selfIncarnation;
    private readonly ApiOptions _options;
    private readonly ILogger<GossipService> _logger;

    public GossipService(
        PeersStore peers, SelfIncarnation selfIncarnation, ApiOptions options, ILogger<GossipService> logger)
    {
        _peers = peers;
        _selfIncarnation = selfIncarnation;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Merge an incoming roster (from a <c>/peers/sync</c> request body or its response) into ours, one
    /// member at a time through <see cref="RosterMerger.Decide"/>: insert new hearsay, adopt superseding
    /// state/incarnation/addressing, refute reports about ourselves by raising
    /// <see cref="SelfIncarnation"/>. Never writes the first-hand liveness triple.
    /// </summary>
    public async Task MergeIncomingAsync(IReadOnlyList<SyncMember> members, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string myNodeId = _options.NodeId;

        foreach (SyncMember member in members)
        {
            try
            {
                PeerEntity? existing = await _peers.GetByNodeIdAsync(member.NodeId, ct).ConfigureAwait(false);
                bool fresh = existing is not null && IsFirstHandFresh(existing, now);
                MergeOutcome outcome = RosterMerger.Decide(member, existing, myNodeId, _selfIncarnation.Current, fresh);

                switch (outcome.Action)
                {
                    case MergeAction.RefuteSelf:
                        long raised = _selfIncarnation.RaiseToRefute(member.Incarnation);
                        _logger.LogInformation(
                            "refuted a stale {State}@{Incarnation} about self — bumped incarnation to {New}",
                            member.State, member.Incarnation, raised);
                        break;

                    case MergeAction.Insert:
                        var peer = new PeerEntity
                        {
                            Id = "peer_" + Guid.NewGuid().ToString("N")[..10],
                            Url = member.Url,
                            GossipUrl = member.GossipUrl,
                            NodeId = member.NodeId,
                            Incarnation = member.Incarnation,
                            MembershipState = member.State,
                            Status = "unknown",
                            LastSeen = null,
                            StateChangedAt = now,
                            ApiVersion = member.ApiVersion ?? "",
                            Enabled = true,
                        };
                        await _peers.UpsertAsync(peer, ct).ConfigureAwait(false);
                        _logger.LogInformation("learned peer {NodeId} via gossip (provisional)", member.NodeId);
                        break;

                    case MergeAction.Update:
                        await _peers
                            .UpdateMembershipAsync(
                                existing!.Id, member.State, member.Incarnation, now, member.Url, member.GossipUrl,
                                member.ApiVersion, ct)
                            .ConfigureAwait(false);
                        _logger.LogDebug(
                            "peer {NodeId} → {State}@{Incarnation} via gossip", member.NodeId, member.State,
                            member.Incarnation);
                        break;

                    case MergeAction.Ignore:
                    default:
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "gossip merge failed for member {NodeId} — skipping", member.NodeId);
            }
        }
    }

    /// <summary>
    /// This node's full roster as gossip members: our own self-entry (id/incarnation/state=alive +
    /// advertised URLs from config) followed by every ENABLED, non-reaped peer. Disabled peers are excluded
    /// (we don't re-inject a locally-banned node into the mesh). The push half of push-pull.
    /// </summary>
    public async Task<IReadOnlyList<SyncMember>> BuildLocalRosterAsync(CancellationToken ct)
    {
        var self = new SyncMember(
            _options.NodeId,
            _options.ClusterAdvertiseUrl,
            string.IsNullOrWhiteSpace(_options.ClusterGossipUrl) ? null : _options.ClusterGossipUrl,
            _selfIncarnation.Current,
            GossipState.Alive,
            ApiInfo.ApiVersion);

        IReadOnlyList<PeerEntity> peers = await _peers.ListEnabledAsync(ct).ConfigureAwait(false);
        var roster = new List<SyncMember>(peers.Count + 1) { self };
        foreach (PeerEntity p in peers)
        {
            if (string.Equals(p.NodeId, _options.NodeId, StringComparison.Ordinal))
                continue;
            roster.Add(new SyncMember(p.NodeId, p.Url, p.GossipUrl, p.Incarnation, p.MembershipState, p.ApiVersion));
        }
        return roster;
    }

    /// <summary>
    /// Advance the failure timers off the <b>last-evidence clock</b> (<c>LastSeen</c>) + <c>StateChangedAt</c>:
    /// an <c>alive</c> peer with no liveness evidence for <see cref="ApiOptions.ClusterSuspectMs"/> →
    /// <c>suspect</c>; a <c>suspect</c> peer still silent past that window → <c>dead</c>; a
    /// <c>dead</c>/<c>left</c> peer past <see cref="ApiOptions.ClusterReapMs"/> → reaped (row deleted).
    /// <b>Evidence is mutual and from either direction</b>: our own successful probe
    /// (<see cref="PeerLatencyPoller"/> → <see cref="PeersStore.UpdateLivenessAsync"/>/<see cref="PeersStore.PromoteAliveAsync"/>)
    /// OR an authenticated inbound sync FROM that peer (<see cref="RecordInboundContactAsync"/>) — both stamp
    /// <c>LastSeen</c>. So a node we can't probe but that still gossips to us stays alive (an asymmetric
    /// partition resolves in favour of the demonstrably-live node), and only a node that goes fully silent
    /// dies. Recovery from <c>suspect</c> is owned by those promotion paths, not this method. A never-yet-confirmed
    /// hearsay peer (null <c>LastSeen</c>) gets a <see cref="ApiOptions.ClusterSuspectMs"/> grace off its
    /// insert time (<c>StateChangedAt</c>) for the poller to authenticate it before it is suspected. Runs each
    /// gossip tick.
    /// </summary>
    public async Task AdvanceFailureTimersAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<PeerEntity> peers = await _peers.ListAsync(ct).ConfigureAwait(false);

        foreach (PeerEntity p in peers)
        {
            try
            {
                if (p.MembershipState == GossipState.Alive)
                {
                    // Last liveness evidence (probe success OR inbound authenticated contact); for a peer we
                    // have never confirmed (null LastSeen), fall back to when we learned it (StateChangedAt) —
                    // a grace window for the poller to authenticate a freshly-gossiped peer before it is
                    // suspected. A peer with NEITHER (no evidence, no known learn-time) has nothing vouching
                    // for it and is immediately eligible for suspicion.
                    DateTimeOffset? since = p.LastSeen ?? p.StateChangedAt;
                    if (since is null || now - since.Value >= TimeSpan.FromMilliseconds(_options.ClusterSuspectMs))
                    {
                        await _peers
                            .UpdateMembershipAsync(p.Id, GossipState.Suspect, p.Incarnation, now, null, null, null, ct)
                            .ConfigureAwait(false);
                        _logger.LogInformation("peer {NodeId} → suspect (no liveness evidence)", p.NodeId);
                    }
                }
                else if (p.MembershipState == GossipState.Suspect)
                {
                    // Recovery (fresh evidence → back to alive) is owned by PromoteAliveAsync / the inbound
                    // contact path, which set LastSeen; here we only escalate a still-silent suspect to dead
                    // once it has stayed suspect for the suspect window.
                    if (p.StateChangedAt is { } since
                        && now - since >= TimeSpan.FromMilliseconds(_options.ClusterSuspectMs))
                    {
                        await _peers
                            .UpdateMembershipAsync(p.Id, GossipState.Dead, p.Incarnation, now, null, null, null, ct)
                            .ConfigureAwait(false);
                        _logger.LogInformation("peer {NodeId} → dead (suspect timeout)", p.NodeId);
                    }
                }
                else if (GossipState.IsTerminal(p.MembershipState))
                {
                    if (p.StateChangedAt is { } since
                        && now - since >= TimeSpan.FromMilliseconds(_options.ClusterReapMs))
                    {
                        await _peers.DeleteAsync(p.Id, ct).ConfigureAwait(false);
                        _logger.LogInformation("reaped {NodeId} ({State})", p.NodeId, p.MembershipState);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "failure-timer advance failed for peer {NodeId} — skipping", p.NodeId);
            }
        }
    }

    /// <summary>
    /// Record an authenticated inbound sync FROM <paramref name="fromNodeId"/> as first-hand liveness
    /// evidence (<c>PLAN-peers.md §2·b</c>, G5): the caller presented a valid cluster service token and
    /// reached us, so — if we hold a row for it — promote it to <c>alive</c> and stamp <c>LastSeen</c>.
    /// This is the mutual half of failure detection: a peer we cannot probe but that still talks to us is
    /// demonstrably alive, so it never falsely dies (and an <c>alive</c>↔<c>suspect</c> oscillation under an
    /// asymmetric partition can't run away). A no-op if we hold no row yet — the merge inserts it from the
    /// sync payload, and our own probe promotes it out of <c>joining</c> once it authenticates.
    /// </summary>
    public Task RecordInboundContactAsync(string fromNodeId, CancellationToken ct) =>
        _peers.RecordAliveContactAsync(fromNodeId, DateTimeOffset.UtcNow, ct);

    /// <summary>The freshness predicate <see cref="RosterMerger"/> consults — exposed so the merge uses one
    /// definition. A peer is "fresh first-hand" when we had liveness evidence for it recently (our probe
    /// succeeded OR it authenticated an inbound sync to us — both stamp <see cref="PeerEntity.LastSeen"/>),
    /// so equal-incarnation gossip hearsay can't override what we just confirmed ourselves.</summary>
    public bool IsFirstHandFresh(PeerEntity peer, DateTimeOffset now) =>
        peer.LastSeen is { } seen && now - seen <= FirstHandFreshWindow;
}
