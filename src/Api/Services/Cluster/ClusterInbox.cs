using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>The outcome of <see cref="ClusterInbox.ReceiveAsync"/> — <see cref="Controllers.PeersController"/>
/// maps every value except <see cref="TransientFailure"/> to a <c>200</c> (the caller stops retrying);
/// <see cref="TransientFailure"/> alone becomes a <c>500</c> (the caller keeps retrying).</summary>
public enum InboxResult
{
    /// <summary>Freshly dispatched and recorded — the handler ran and succeeded for the first time.</summary>
    Applied,

    /// <summary>An <see cref="InboxMessage"/> row with this envelope id already existed — the handler
    /// was NOT re-run (dedupe, §7).</summary>
    Duplicate,

    /// <summary>No registered <see cref="IClusterMessageHandler"/> answers this envelope's <c>type</c> —
    /// recorded (so a retransmit is recognized as a duplicate, not re-evaluated) and dropped. A newer
    /// peer may legitimately know a type this node doesn't (additive-only <c>/api/v1</c>); dropping is
    /// the safe, non-blocking behavior (§3/§8).</summary>
    DroppedUnknown,

    /// <summary>The handler threw — a presumed-transient failure (DB locked, etc.). Nothing was
    /// recorded, so the next redelivery re-runs the (idempotent) handler from scratch.</summary>
    TransientFailure,
}

/// <summary>
/// The cluster bus's receive/dedupe/dispatch algorithm (<c>docs/cluster-message-bus-plan.md §7</c>).
/// A scoped-singleton store — the same idiom as <see cref="Auth.SessionStore"/>: owns an
/// <see cref="IServiceScopeFactory"/> + a write gate, resolves <see cref="Data.AppDbContext"/> per
/// operation from its own DI scope so it works equally from a request or (in a later phase) a
/// background drainer retry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why handler-first, not insert-first (a deliberate divergence from the spec's literal §7
/// pseudocode).</b> §7 as written assumes steps 3a (insert the <c>Inbox</c> row) and 3b (dispatch the
/// handler) happen in <em>one</em> DB transaction, so a handler failure can roll the insert back with
/// it. That assumption doesn't hold here: a handler's effect lands in a COMPLETELY DIFFERENT
/// store/transaction than this inbox ledger — <c>session.revoke</c>, for instance, writes the
/// <c>sessions</c> table via <see cref="Auth.SessionStore"/>'s own <see cref="Data.AppDbContext"/>
/// scope and its own write gate, not this class's. There is no single transaction that could ever
/// roll both back together. Insert-first would risk marking an envelope "received" (or worse,
/// "processed") when the handler that was supposed to run for it never actually completed.
/// </para>
/// <para>
/// So this class **runs the handler FIRST and only inserts the ledger row after it returns
/// successfully**. That ordering is safe — not merely convenient — precisely BECAUSE every handler is
/// contractually idempotent (<see cref="IClusterMessageHandler"/>): if the process crashes between
/// "handler succeeded" and "ledger row committed," the next redelivery re-runs the handler, which is a
/// harmless no-op the second time. The two-layer idempotency the plan describes (§7's closing
/// paragraph) is exactly what makes handler-first correct without a cross-store transaction: the
/// ledger is the fast-path dedupe, the idempotent handler is the correctness backstop for the
/// crash-between-steps window insert-first would have (wrongly) tried to close with a single rollback.
/// </para>
/// </remarks>
public sealed class ClusterInbox
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClusterInbox> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly IReadOnlyDictionary<string, IClusterMessageHandler> _handlers;

    public ClusterInbox(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IClusterMessageHandler> handlers,
        ILogger<ClusterInbox> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _handlers = handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);
    }

    /// <summary>
    /// Receive one already-authenticated, already-validated envelope (the controller has already
    /// checked the cluster token, the peer gate, and <c>from == iss</c> before calling this). Serialized
    /// on <see cref="_writeGate"/> — the whole dedupe-check + dispatch + record sequence for ONE
    /// envelope runs atomically with respect to every other envelope this node is receiving, which is
    /// what makes the duplicate-delivery race (§11 #3) safe without a cross-store transaction (see the
    /// type remarks). See <see cref="InboxResult"/> for the caller's status-code mapping.
    /// </summary>
    public async Task<InboxResult> ReceiveAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 1. Dedupe check — an existing row (processed or not) means "don't re-run the handler."
            bool alreadySeen = await db.ClusterInbox.AsNoTracking()
                .AnyAsync(i => i.Id == envelope.Id, ct).ConfigureAwait(false);
            if (alreadySeen)
            {
                _logger.LogDebug(
                    "cluster inbox: duplicate envelope {Id} (type={Type} from={From}) — ack without re-dispatch",
                    envelope.Id, envelope.Type, envelope.From);
                return InboxResult.Duplicate;
            }

            // 2. Dispatch.
            if (!_handlers.TryGetValue(envelope.Type, out IClusterMessageHandler? handler))
            {
                _logger.LogWarning(
                    "cluster inbox: unknown message type {Type} (id={Id} from={From}) — dropped, acked 200",
                    envelope.Type, envelope.Id, envelope.From);
                // Record so a retransmit of this same unknown-type envelope is recognized as a
                // duplicate rather than logged/dropped again on every retry. ProcessedAt stays null —
                // there was no handler to succeed.
                await RecordAsync(db, envelope, processed: false, ct).ConfigureAwait(false);
                return InboxResult.DroppedUnknown;
            }

            try
            {
                await handler.HandleAsync(envelope, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Transient failure: do NOT record a ledger row. The sender gets a 500 and retries;
                // because the handler is idempotent, re-running it from scratch on redelivery is safe.
                _logger.LogWarning(ex,
                    "cluster inbox: transient failure dispatching {Type} (id={Id} from={From}) — not " +
                    "recorded, sender should retry",
                    envelope.Type, envelope.Id, envelope.From);
                return InboxResult.TransientFailure;
            }

            // 3. Handler succeeded — record the ledger row now (handler-first, see the type remarks).
            try
            {
                await RecordAsync(db, envelope, processed: true, ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // PK conflict on the insert: a concurrent delivery of the SAME envelope id committed its
                // row first. Under this class's own write gate that shouldn't happen for a request that
                // came through this instance, but a second ClusterInbox instance (a future multi-worker
                // drainer redelivery path) could race here — and per §7, that's fine: the effect is
                // idempotent, so whichever attempt's row lands first is the one that "owns" completion.
                _logger.LogDebug(
                    "cluster inbox: ledger insert for {Id} lost a race to a concurrent delivery — treated as success",
                    envelope.Id);
            }

            return InboxResult.Applied;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static Task RecordAsync(AppDbContext db, ClusterEnvelope envelope, bool processed, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.ClusterInbox.Add(new InboxMessage
        {
            Id = envelope.Id,
            FromNodeId = envelope.From,
            Type = envelope.Type,
            ReceivedAt = now,
            ProcessedAt = processed ? now : null,
        });
        return db.SaveChangesAsync(ct);
    }
}
