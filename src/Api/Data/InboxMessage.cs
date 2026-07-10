namespace TheKrystalShip.Api.Data;

/// <summary>
/// One row per received envelope of the cluster message bus's inbox dedupe ledger
/// (<c>docs/cluster-message-bus-plan.md §5</c>/§7) — the processed-id record that makes at-least-once
/// redelivery idempotent: a PK conflict on insert means "already seen this envelope id," so the
/// handler never re-applies it.
/// <para>
/// This table is the <em>foundation</em> laid in Phase 1 — no <c>/peers/inbox</c> endpoint and no
/// dispatch handler write to it yet. Those land in a later phase; this entity + its EF mapping exist
/// so the schema is in place ahead of the receive path.
/// </para>
/// <para>
/// <b>EnsureCreated, NOT a migration</b> (the project's dev authority — see <see cref="AppDbContext"/>):
/// a schema change here means wiping the dev DB, not carrying an EF migration.
/// </para>
/// </summary>
public sealed class InboxMessage
{
    /// <summary>The envelope <c>id</c> — the dedupe key. Primary key.</summary>
    public string Id { get; set; } = "";

    /// <summary>The sending node's id (the authenticated cluster-token <c>iss</c>, verified to equal
    /// the envelope's own <c>from</c> before this row is written). Diagnostics only.</summary>
    public string FromNodeId { get; set; } = "";

    /// <summary>The envelope's discriminated-union <c>type</c>. Diagnostics only.</summary>
    public string Type { get; set; } = "";

    /// <summary>When this envelope was first received (UTC).</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>When the dispatched handler completed successfully (UTC). <see langword="null"/>
    /// while the row represents an in-flight or not-yet-processed receive; a later phase's dedupe
    /// check treats a null-<c>ProcessedAt</c> duplicate as "another attempt in flight" and acks
    /// without re-dispatching (<c>docs/cluster-message-bus-plan.md §7</c>).</summary>
    public DateTimeOffset? ProcessedAt { get; set; }
}
