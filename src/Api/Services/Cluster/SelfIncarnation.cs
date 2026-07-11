namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// This node's own monotonic <b>incarnation</b> counter (<c>PLAN-peers.md §2·b</c>, G2) — the SWIM
/// mechanism a node uses to REFUTE a false <c>suspect</c>/<c>dead</c> that gossip carries about itself: only
/// the node itself may raise its own incarnation, and a strictly higher incarnation always wins the merge,
/// so re-asserting <c>alive</c> at <c>observed + 1</c> supersedes the stale report everywhere it has spread.
/// </summary>
/// <remarks>
/// A process-lifetime singleton, starting at <c>0</c>. It is deliberately NOT persisted: on restart it
/// resets to <c>0</c>, and the FIRST gossip round where a returning node hears its own stale
/// <c>dead@k</c>/<c>suspect@k</c> drives <see cref="RaiseToRefute"/> to <c>k + 1</c> — the standard SWIM
/// refutation path, which recovers a restarted node's identity without any persisted counter. Thread-safe
/// (the gossip worker and the <c>/peers/sync</c> request path both touch it concurrently).
/// </remarks>
public sealed class SelfIncarnation
{
    private long _value;

    /// <summary>The current incarnation this node stamps onto its own gossip self-entry.</summary>
    public long Current => Interlocked.Read(ref _value);

    /// <summary>
    /// Refute a stale report about ourselves: if <paramref name="observedIncarnation"/> is at least our
    /// current value, jump to <c>observedIncarnation + 1</c> so our re-asserted <c>alive</c> strictly
    /// supersedes it (<c>PLAN-peers.md §2·b</c>, G2). A monotonic compare-and-swap loop — never regresses,
    /// safe under concurrent callers. Returns the (possibly unchanged) resulting incarnation.
    /// </summary>
    public long RaiseToRefute(long observedIncarnation)
    {
        while (true)
        {
            long current = Interlocked.Read(ref _value);
            if (observedIncarnation < current)
                return current;
            long next = observedIncarnation + 1;
            if (Interlocked.CompareExchange(ref _value, next, current) == current)
                return next;
        }
    }
}
