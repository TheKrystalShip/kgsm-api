using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Transfers;

/// <summary>The two sides a node can be of a transfer, as <see cref="TransferEntity.Role"/> spells them.</summary>
public static class TransferRole
{
    public const string Source = "source";
    public const string Target = "target";
}

/// <summary>
/// This node's transfer records: the durable rows, and a copy of every row in memory for the readers that
/// cannot wait on a database (the server list, the DNS instance source).
/// </summary>
/// <remarks>
/// <para>
/// The posture of <see cref="Commands.BatchStore"/>: a singleton owning a scope per operation, writes behind
/// one gate, and an idempotent <c>CREATE TABLE IF NOT EXISTS</c> beside <c>EnsureCreated</c> so the table
/// appears on a host whose database already exists.
/// </para>
/// <para>
/// <b>The copy in memory is whole.</b> This process is the table's only writer and a node holds a
/// handful of transfers, so every row is read once at load and every write lands in both places under
/// the gate. Until the first load a reader sees <see cref="Loaded"/> false, and the DNS source holds its
/// answer back rather than reporting an instance without the inheritance its record would add.
/// </para>
/// </remarks>
public sealed class TransferStore(IServiceScopeFactory scopeFactory, ILogger<TransferStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(string Id, string Role), TransferEntity> _rows = new();
    private volatile bool _loaded;

    /// <summary>Whether the rows have been read from the database.</summary>
    public bool Loaded => _loaded;

    /// <summary>Creates the table if needed and reads every row. Idempotent.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS transfers (
                    "TransferId" TEXT NOT NULL,
                    "Role" TEXT NOT NULL,
                    "InstanceId" TEXT NOT NULL,
                    "PeerMemberId" TEXT NOT NULL,
                    "GameName" TEXT NULL,
                    "State" TEXT NOT NULL,
                    "Phase" TEXT NULL,
                    "Reason" TEXT NULL,
                    "ImportId" TEXT NULL,
                    "TargetInstanceId" TEXT NULL,
                    "Library" TEXT NULL,
                    "JobId" TEXT NULL,
                    "CommitEnqueued" INTEGER NOT NULL DEFAULT 0,
                    "TicketHash" TEXT NULL,
                    "TicketExpiresAt" INTEGER NULL,
                    "Actor" TEXT NULL,
                    "Origin" TEXT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL,
                    CONSTRAINT "PK_transfers" PRIMARY KEY ("TransferId", "Role")
                );
                """, ct).ConfigureAwait(false);

            List<TransferEntity> rows = await db.Transfers.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            foreach (TransferEntity row in rows)
                _rows[(row.TransferId, row.Role)] = row;

            _loaded = true;
            logger.LogDebug("transfer store loaded {Count} record(s)", rows.Count);
        }
        finally { _gate.Release(); }
    }

    /// <summary>One record, or null.</summary>
    public async Task<TransferEntity?> GetAsync(string transferId, string role, CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        return _rows.TryGetValue((transferId, role), out TransferEntity? row) ? row.Copy() : null;
    }

    /// <summary>Every record of one role.</summary>
    public async Task<IReadOnlyList<TransferEntity>> ListAsync(string role, CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        return [.. _rows.Values.Where(r => r.Role == role).Select(r => r.Copy())];
    }

    /// <summary>The records as last written, without waiting. Empty until <see cref="Loaded"/>.</summary>
    public IEnumerable<TransferEntity> Snapshot => _rows.Values.Select(r => r.Copy());

    /// <summary>Adds a record. False when one already exists for that transfer and role.</summary>
    public async Task<bool> TryAddAsync(TransferEntity row, CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rows.ContainsKey((row.TransferId, row.Role))) return false;

            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Transfers.Add(row.Copy());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _rows[(row.TransferId, row.Role)] = row.Copy();
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to one record and writes it. Returns the record as written, or
    /// null when there is none.
    /// </summary>
    public async Task<TransferEntity?> UpdateAsync(
        string transferId, string role, Action<TransferEntity> change, CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_rows.TryGetValue((transferId, role), out TransferEntity? current)) return null;

            TransferEntity next = current.Copy();
            change(next);
            next.UpdatedAt = DateTimeOffset.UtcNow;

            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Transfers.Update(next.Copy());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _rows[(transferId, role)] = next;
            return next.Copy();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The transfer a server on this node is part of right now, for <see cref="Server.Transfer"/>: a source
    /// record that is locked, or ticketed with its ticket unexpired.
    /// </summary>
    /// <remarks>
    /// Only the source holds a server mid-transfer. The target has no instance until <c>import finish</c>
    /// mints it, and records it in the same write that marks the transfer committed, so an arriving
    /// server appears already settled; the target's progress is its job.
    /// </remarks>
    public ServerTransfer? ActiveFor(string serverId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (TransferEntity r in _rows.Values)
        {
            if (r.Role == TransferRole.Source
                && string.Equals(r.InstanceId, serverId, StringComparison.Ordinal)
                && IsLive(r, now))
            {
                return new ServerTransfer(r.TransferId, r.PeerMemberId, TransferDirection.Out, r.State);
            }
        }

        return null;
    }

    /// <summary>Whether a source record still holds its instance: locked, or ticketed and unexpired.</summary>
    public static bool IsLive(TransferEntity source, DateTimeOffset now) =>
        source.State == TransferState.Locked
        || (source.State == TransferState.Ticketed && source.TicketExpiresAt is { } exp && now < exp);
}
