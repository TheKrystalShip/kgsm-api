using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// The durable half of a batch: what was asked for, and how far each member got.
/// </summary>
/// <remarks>
/// <para>
/// Same posture as the other stores that came after the first deploy — a singleton owning a scope per
/// operation, writes behind a gate (SQLite has one writer), and an idempotent
/// <c>CREATE TABLE IF NOT EXISTS</c> beside <c>EnsureCreated</c> so the tables appear on a host whose
/// database already exists. The audit log lives in that same file and is never wiped to add a table.
/// </para>
/// <para>
/// It holds no in-flight state. What is running right now is <see cref="JobRegistry"/>'s answer and is
/// rebuilt from nothing on every start; this holds what a batch was asked to do, which has to survive
/// the restart that empties the registry.
/// </para>
/// </remarks>
public sealed class BatchStore(IServiceScopeFactory scopeFactory)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_ensured) return;
        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured) return;
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS batches (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_batches" PRIMARY KEY,
                    "RunId" TEXT NULL,
                    "Verb" TEXT NOT NULL,
                    "State" TEXT NOT NULL,
                    "Actor" TEXT NULL,
                    "Origin" TEXT NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "SettledAt" INTEGER NULL
                );
                """, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS batch_members (
                    "BatchId" TEXT NOT NULL,
                    "ServerId" TEXT NOT NULL,
                    "State" TEXT NOT NULL,
                    "JobId" TEXT NULL,
                    "Position" INTEGER NULL,
                    "Error" TEXT NULL,
                    "SettledAt" INTEGER NULL,
                    CONSTRAINT "PK_batch_members" PRIMARY KEY ("BatchId", "ServerId")
                );
                """, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """CREATE INDEX IF NOT EXISTS "IX_batches_State" ON batches ("State");""",
                ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """CREATE INDEX IF NOT EXISTS "IX_batches_RunId" ON batches ("RunId");""",
                ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """CREATE INDEX IF NOT EXISTS "IX_batch_members_State" ON batch_members ("State");""",
                ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>Record an accepted batch and every member it took or turned away.</summary>
    public async Task CreateAsync(BatchEntity batch, IReadOnlyList<BatchMemberEntity> members, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Batches.Add(batch);
            db.BatchMembers.AddRange(members);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Move one member to a new state, and settle the batch when nothing is left that isn't terminal.
    /// </summary>
    /// <remarks>
    /// The batch's own settle happens here rather than in the worker so that every path that finishes a
    /// member — the worker, a cancel, the restart reconciliation — closes the batch by the same rule.
    /// </remarks>
    public async Task SetMemberStateAsync(
        string batchId, string serverId, string state, string? error = null, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            BatchMemberEntity? member = await db.BatchMembers
                .FirstOrDefaultAsync(m => m.BatchId == batchId && m.ServerId == serverId, ct)
                .ConfigureAwait(false);
            if (member is null) return;

            member.State = state;
            member.Error = error;
            if (BatchMemberState.IsTerminal(state)) member.SettledAt = DateTimeOffset.UtcNow;

            await SettleIfDoneAsync(db, batchId, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Cancel every member that has not started, and report what could not be stopped.
    /// </summary>
    /// <remarks>
    /// A running member is left alone and named in the result. A kgsm invocation already under way is not
    /// interruptible, and reporting a clean halt that did not happen is worse than reporting a partial one.
    /// </remarks>
    public async Task<(IReadOnlyList<string> cancelled, IReadOnlyList<string> stillRunning, IReadOnlyList<string> jobIds)>
        CancelPendingAsync(string batchId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            List<BatchMemberEntity> members = await db.BatchMembers
                .Where(m => m.BatchId == batchId)
                .ToListAsync(ct).ConfigureAwait(false);

            var cancelled = new List<string>();
            var stillRunning = new List<string>();
            var jobIds = new List<string>();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            foreach (BatchMemberEntity m in members)
            {
                if (m.State == BatchMemberState.Pending)
                {
                    m.State = BatchMemberState.Cancelled;
                    m.SettledAt = now;
                    cancelled.Add(m.ServerId);
                    if (m.JobId is not null) jobIds.Add(m.JobId);
                }
                else if (m.State == BatchMemberState.Running)
                {
                    stillRunning.Add(m.ServerId);
                }
            }

            await SettleIfDoneAsync(db, batchId, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (cancelled, stillRunning, jobIds);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// The members the worker may pick up, oldest batch first, with the batch's provenance attached.
    /// </summary>
    /// <remarks>
    /// Ordered by the batch's creation time and then the member's own position, so a second batch never
    /// overtakes the one already waiting and a batch runs in the order the client asked for it.
    /// </remarks>
    public async Task<IReadOnlyList<PendingMember>> GetPendingAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await (
            from m in db.BatchMembers
            join b in db.Batches on m.BatchId equals b.Id
            where m.State == BatchMemberState.Pending
            orderby b.CreatedAt, m.Position
            select new PendingMember(
                m.BatchId, m.ServerId, m.JobId, m.Position, b.Verb, b.Actor, b.Origin, b.CreatedAt))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Members left mid-run by a process that ended, so the worker can settle them honestly on start.
    /// </summary>
    public async Task<IReadOnlyList<PendingMember>> GetOrphanedRunningAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await (
            from m in db.BatchMembers
            join b in db.Batches on m.BatchId equals b.Id
            where m.State == BatchMemberState.Running
            orderby b.CreatedAt, m.Position
            select new PendingMember(
                m.BatchId, m.ServerId, m.JobId, m.Position, b.Verb, b.Actor, b.Origin, b.CreatedAt))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One batch with its members, or null.</summary>
    public async Task<BatchView?> GetAsync(string batchId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        BatchEntity? batch = await db.Batches.FirstOrDefaultAsync(b => b.Id == batchId, ct).ConfigureAwait(false);
        if (batch is null) return null;

        List<BatchMemberEntity> members = await db.BatchMembers
            .Where(m => m.BatchId == batchId)
            .ToListAsync(ct).ConfigureAwait(false);

        return View(batch, members);
    }

    /// <summary>
    /// Batches, newest first. <paramref name="activeOnly"/> is what a client polls on reconnect;
    /// <paramref name="runId"/> narrows to one cluster-wide run's share of this node.
    /// </summary>
    public async Task<IReadOnlyList<BatchView>> ListAsync(
        bool activeOnly, string? runId, int limit, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        IQueryable<BatchEntity> q = db.Batches;
        if (activeOnly) q = q.Where(b => b.State == BatchState.Active);
        if (!string.IsNullOrWhiteSpace(runId)) q = q.Where(b => b.RunId == runId);

        List<BatchEntity> batches = await q
            .OrderByDescending(b => b.CreatedAt)
            .Take(limit)
            .ToListAsync(ct).ConfigureAwait(false);

        if (batches.Count == 0) return [];

        List<string> ids = batches.Select(b => b.Id).ToList();
        List<BatchMemberEntity> members = await db.BatchMembers
            .Where(m => ids.Contains(m.BatchId))
            .ToListAsync(ct).ConfigureAwait(false);

        var byBatch = members.GroupBy(m => m.BatchId).ToDictionary(g => g.Key, g => g.ToList());
        return batches
            .Select(b => View(b, byBatch.TryGetValue(b.Id, out List<BatchMemberEntity>? ms) ? ms : []))
            .ToList();
    }

    // Close the batch when no member is left that could still move. Called on the same tracked context
    // as the change that may have been the last one, so the settle lands in the same SaveChanges.
    private static async Task SettleIfDoneAsync(AppDbContext db, string batchId, CancellationToken ct)
    {
        List<BatchMemberEntity> all = await db.BatchMembers
            .Where(m => m.BatchId == batchId)
            .ToListAsync(ct).ConfigureAwait(false);

        if (all.Any(m => !BatchMemberState.IsTerminal(m.State))) return;

        BatchEntity? batch = await db.Batches.FirstOrDefaultAsync(b => b.Id == batchId, ct).ConfigureAwait(false);
        if (batch is null || batch.State == BatchState.Settled) return;

        batch.State = BatchState.Settled;
        // The batch settled when its last member did, not when this query noticed.
        batch.SettledAt = all.Max(m => m.SettledAt) ?? DateTimeOffset.UtcNow;
    }

    private static BatchView View(BatchEntity b, List<BatchMemberEntity> members)
    {
        int Count(string state) => members.Count(m => m.State == state);
        return new BatchView(
            b.Id, b.RunId, b.Verb, b.State, b.Actor, b.Origin, b.CreatedAt, b.SettledAt,
            new BatchCounts(
                members.Count,
                Count(BatchMemberState.Pending),
                Count(BatchMemberState.Running),
                Count(BatchMemberState.Succeeded),
                Count(BatchMemberState.Failed),
                Count(BatchMemberState.Refused),
                Count(BatchMemberState.Cancelled),
                Count(BatchMemberState.Unknown)),
            members
                // Refused members have no position; they sort after the queue they never joined.
                .OrderBy(m => m.Position ?? int.MaxValue)
                .ThenBy(m => m.ServerId, StringComparer.Ordinal)
                .Select(m => new BatchMember(m.ServerId, m.State, m.JobId, m.Position, m.Error, m.SettledAt))
                .ToList());
    }
}

/// <summary>A member the worker can act on, carrying the batch's verb and provenance so the worker needs
/// no second read to run it.</summary>
public sealed record PendingMember(
    string BatchId,
    string ServerId,
    string? JobId,
    int? Position,
    string Verb,
    string? Actor,
    string Origin,
    DateTimeOffset BatchCreatedAt);
