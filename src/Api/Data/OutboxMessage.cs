namespace TheKrystalShip.Api.Data;

/// <summary>
/// One row per (message, target) delivery of the cluster message bus's transactional outbox
/// (<c>docs/cluster-message-bus-plan.md §5</c>) — a durable, at-least-once send obligation this node
/// owns until the target node has acknowledged it (or it is dead-lettered). A broadcast to N peers
/// enqueues N independent rows, each retried on its own schedule.
/// <para>
/// This table is the <em>foundation</em> laid in Phase 1 — no drainer, no <c>IClusterBus.Enqueue</c>,
/// and nothing writes a row yet. Those land in a later phase; this entity + its EF mapping exist so
/// the schema is in place and reviewable ahead of the write path.
/// </para>
/// <para>
/// <b>EnsureCreated, NOT a migration</b> (the project's dev authority — see <see cref="AppDbContext"/>):
/// a schema change here means wiping the dev DB, not carrying an EF migration.
/// </para>
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>
    /// <c>&lt;MessageId&gt;:&lt;TargetNodeId&gt;</c> — unique per delivery, so the same broadcast
    /// enqueues one distinguishable row per target. Primary key.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>The envelope <c>id</c> (<c>docs/cluster-message-bus-plan.md §3</c>) — identical
    /// across every target row of one broadcast; the dedupe key the receiving node's inbox checks.</summary>
    public string MessageId { get; set; } = "";

    /// <summary>The recipient node's id (matches a <c>Peers</c> row's <c>nodeId</c>, once that table
    /// exists — P0 in <c>PLAN-peers.md</c>).</summary>
    public string TargetNodeId { get; set; } = "";

    /// <summary>
    /// The delivery URL for <see cref="TargetNodeId"/> at the time this row was enqueued — e.g.
    /// <c>https://node-b:8097</c>, so the drainer POSTs to <c>{TargetUrl}/api/v1/peers/inbox</c>.
    /// <b>Not in the §5 spec table</b> — added here because the drainer needs a delivery address and
    /// there is no <c>Peers</c> table yet (that lands in the separate, later P0 peer-foundation work);
    /// denormalizing the URL onto the outbox row lets the bus be built and tested standalone, decoupled
    /// from the unbuilt peer registry. A later phase may resolve this from <c>Peers</c> at drain time
    /// instead and drop the denormalized copy.
    /// </summary>
    public string TargetUrl { get; set; } = "";

    /// <summary>The envelope's discriminated-union <c>type</c> (e.g. <c>session.revoke</c>).</summary>
    public string Type { get; set; } = "";

    /// <summary>The envelope's <c>payload</c>, serialized as a JSON object string (type-specific).</summary>
    public string Payload { get; set; } = "";

    /// <summary>Delivery state: <c>pending</c> (awaiting/retrying delivery), <c>delivered</c> (acked
    /// <c>2xx</c>), or <c>dead</c> (permanently rejected or past the retry TTL — never retried again).</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Number of delivery attempts made so far; incremented on each transient failure and
    /// used to compute the next backoff.</summary>
    public int Attempts { get; set; }

    /// <summary>The next time the drainer should attempt this row (UTC). The drainer's due-scan is
    /// <c>Status = pending AND NextAttemptAt &lt;= now</c>.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>When this row was enqueued (UTC) — the anchor for the 7-day retry TTL dead-letter check.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the target acknowledged (UTC). <see langword="null"/> while <see cref="Status"/>
    /// is not <c>delivered</c>.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>The last transient failure's description (diagnostics only), or <see langword="null"/>
    /// if this row has never failed.</summary>
    public string? LastError { get; set; }
}
