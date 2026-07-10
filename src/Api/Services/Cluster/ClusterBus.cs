using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Json;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The cluster bus's outbox reader/writer (<c>docs/cluster-message-bus-plan.md §6</c>) — the single
/// reader/writer of the <c>cluster_outbox</c> table. A scoped-singleton store, the same idiom as
/// <see cref="Auth.SessionStore"/>/<see cref="Cluster.ClusterInbox"/>: owns an
/// <see cref="IServiceScopeFactory"/> + a write gate, resolves <see cref="AppDbContext"/> per operation
/// from its own DI scope so it works equally from a request (<see cref="EnqueueAsync"/>) or a background
/// worker (<see cref="OutboxDrainer"/>'s per-tick reads/writes, <see cref="ClusterBusGcWorker"/>'s prune).
/// Registered as both <see cref="IClusterBus"/> (the public send seam) and this concrete type (the
/// drainer/GC's store-side helpers, which are not part of that public seam).
/// </summary>
public sealed class ClusterBus(
    IServiceScopeFactory scopeFactory,
    ILogger<ClusterBus> logger) : IClusterBus
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // The same ApiJson-configured options the wire envelope (PeersController) and the drainer use — a
    // payload serialized here must round-trip identically to what the receiving node's inbox expects.
    private static readonly JsonSerializerOptions PayloadJsonOptions = BuildJsonOptions();

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var options = new JsonSerializerOptions();
        ApiJson.Configure(options);
        return options;
    }

    /// <inheritdoc/>
    public async Task EnqueueAsync(
        string type, object payload, IEnumerable<ClusterTarget> targets, CancellationToken ct)
    {
        List<ClusterTarget> targetList = targets as List<ClusterTarget> ?? targets.ToList();
        if (targetList.Count == 0)
            return; // nothing to enqueue

        string messageId = Guid.NewGuid().ToString("N");
        string payloadJson = JsonSerializer.Serialize(payload, PayloadJsonOptions);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (ClusterTarget target in targetList)
            {
                db.ClusterOutbox.Add(new OutboxMessage
                {
                    Id = $"{messageId}:{target.NodeId}",
                    MessageId = messageId,
                    TargetNodeId = target.NodeId,
                    TargetUrl = target.Url,
                    Type = type,
                    Payload = payloadJson,
                    Status = "pending",
                    Attempts = 0,
                    NextAttemptAt = now,
                    CreatedAt = now,
                });
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogDebug(
                "cluster outbox: enqueued {MessageId} (type={Type}) to {Count} target(s)",
                messageId, type, targetList.Count);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// The outbox drainer's per-tick due-scan (<c>docs/cluster-message-bus-plan.md §6</c>): every
    /// <c>pending</c> row whose <see cref="OutboxMessage.NextAttemptAt"/> has passed, oldest-first, capped
    /// at <paramref name="max"/> so one tick can never unboundedly balloon. A read — no write gate needed.
    /// </summary>
    public async Task<IReadOnlyList<OutboxMessage>> ListDueAsync(DateTimeOffset now, int max, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ClusterOutbox.AsNoTracking()
            .Where(o => o.Status == "pending" && o.NextAttemptAt <= now)
            .OrderBy(o => o.CreatedAt)
            .Take(max)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Mark a row delivered (a <c>2xx</c> ack). A no-op if the row no longer exists (already
    /// pruned/never existed) — the drainer never throws over a row that vanished under it.</summary>
    public Task MarkDeliveredAsync(string id, DateTimeOffset now, CancellationToken ct) =>
        UpdateRowAsync(id, ct, row =>
        {
            row.Status = "delivered";
            row.DeliveredAt = now;
        });

    /// <summary>Record a transient delivery failure: bump <see cref="OutboxMessage.Attempts"/>, push
    /// <see cref="OutboxMessage.NextAttemptAt"/> out by the caller's computed backoff, and note the
    /// error. <see cref="OutboxMessage.Status"/> stays <c>pending</c> — the row keeps retrying.</summary>
    public Task MarkTransientFailureAsync(
        string id, int attempts, DateTimeOffset nextAttemptAt, string error, CancellationToken ct) =>
        UpdateRowAsync(id, ct, row =>
        {
            row.Attempts = attempts;
            row.NextAttemptAt = nextAttemptAt;
            row.LastError = error;
        });

    /// <summary>Dead-letter a row (a permanent peer rejection, or the retry TTL elapsed) — it is never
    /// retried again. <paramref name="reason"/> lands in <see cref="OutboxMessage.LastError"/> for
    /// diagnostics (the caller is expected to ALSO log this loudly — a dead row is an operational signal,
    /// not a silent drop).</summary>
    public Task MarkDeadAsync(string id, string reason, CancellationToken ct) =>
        UpdateRowAsync(id, ct, row =>
        {
            row.Status = "dead";
            row.LastError = reason;
        });

    private async Task UpdateRowAsync(string id, CancellationToken ct, Action<OutboxMessage> mutate)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            OutboxMessage? row = await db.ClusterOutbox
                .FirstOrDefaultAsync(o => o.Id == id, ct).ConfigureAwait(false);
            if (row is null)
                return;
            mutate(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// The retention GC sweep (<c>docs/cluster-message-bus-plan.md §7</c> "Retention / GC"): deletes
    /// <c>delivered</c>/<c>dead</c> outbox rows AND inbox dedupe-ledger rows older than
    /// <paramref name="cutoff"/> (the caller computes <c>cutoff = now - ClusterRetentionDays</c> — the
    /// window is enforced elsewhere, <see cref="ApiOptions.ClusterRetentionDays"/>, to exceed the retry
    /// TTL). Uses EF Core's bulk <c>ExecuteDeleteAsync</c> (no change-tracker materialization), the same
    /// posture as <see cref="Auth.SessionStore.DeleteExpiredAsync"/>. <paramref name="now"/> is accepted
    /// for symmetry/future diagnostics (e.g. logging how stale the oldest surviving row is) — the delete
    /// predicate itself only needs <paramref name="cutoff"/>. Returns the total rows deleted (both tables).
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoff, DateTimeOffset now, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            int outboxDeleted = await db.ClusterOutbox
                .Where(o => (o.Status == "delivered" || o.Status == "dead") && o.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            int inboxDeleted = await db.ClusterInbox
                .Where(i => i.ReceivedAt < cutoff)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            logger.LogDebug(
                "cluster bus GC: pruned {Outbox} outbox row(s), {Inbox} inbox row(s) (as of {Now:O})",
                outboxDeleted, inboxDeleted, now);
            return outboxDeleted + inboxDeleted;
        }
        finally { _writeGate.Release(); }
    }
}
