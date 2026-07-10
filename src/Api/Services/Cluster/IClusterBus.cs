namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The cluster bus's SEND seam (<c>docs/cluster-message-bus-plan.md §6</c>) — the one method a caller
/// depends on to durably announce an event to peers. <see cref="ClusterBus"/> is the implementation
/// (registered as both <see cref="IClusterBus"/> and its concrete type — the drainer/GC worker depend on
/// the concrete type for the store-side helpers they need that aren't part of this public seam).
/// </summary>
public interface IClusterBus
{
    /// <summary>
    /// Enqueue one message, addressed to every target in <paramref name="targets"/>, for durable
    /// at-least-once delivery (<c>docs/cluster-message-bus-plan.md §6</c>). Mints a single
    /// <c>messageId</c> shared across every target's row (the envelope <c>id</c> the receiving inbox
    /// dedupes on is identical for all of them — this is one broadcast, not N independent messages) and
    /// writes one <see cref="Data.OutboxMessage"/> row per target, each delivered and retried on its own
    /// schedule. Returns as soon as the rows are committed — delivery itself is asynchronous (the outbox
    /// drainer's job).
    /// </summary>
    /// <param name="type">The discriminated-union message type (e.g. <c>session.revoke</c>) — must match
    /// a registered <see cref="IClusterMessageHandler.Type"/> on every target for it to actually apply
    /// (an unrecognized type is safely dropped there, per §3/§8 — not this method's concern).</param>
    /// <param name="payload">The type-specific payload object, serialized once (camelCase, ISO-8601 `Z`
    /// timestamps — the same <c>ApiJson</c>-configured options the wire envelope uses) and stored
    /// identically on every target's row.</param>
    /// <param name="targets">The peers to address. There is no <c>Peers</c> table yet (a later,
    /// separate milestone) — the caller supplies the target set itself. An empty sequence is a no-op
    /// (nothing to enqueue).</param>
    /// <param name="ct">Cancellation for the write.</param>
    /// <remarks>
    /// <para>
    /// <b>Transactional note — read before adding a caller.</b> Plan §6 wants this enqueue to share the
    /// SAME database transaction as the local effect it announces (the archetype: "logout everywhere"
    /// revokes this node's own sessions, then enqueues the peer notifications, atomically — a rollback of
    /// the local revoke must leave no ghost outbox row promising a peer something that never happened
    /// locally). <b>This overload does not do that.</b> It is a standalone gated write, run immediately
    /// after the caller's own local effect has already committed. That is safe today because there is no
    /// caller yet that needs the stronger guarantee — the first one (cluster-wide logout) is a later
    /// milestone (<c>PLAN-peers.md</c> P1), not built in this phase. The gap this leaves is a narrow crash
    /// window: the local effect commits, the process dies before this call's rows land, and the peer
    /// notification is lost (the local action itself is NOT lost — only the fan-out). A future
    /// transactional overload — accepting the caller's own <see cref="Data.AppDbContext"/> so both writes
    /// share one transaction — would close that window. It is deliberately not built now; add it only
    /// once a real caller needs it, not speculatively ahead of one.
    /// </para>
    /// </remarks>
    Task EnqueueAsync(string type, object payload, IEnumerable<ClusterTarget> targets, CancellationToken ct);
}
