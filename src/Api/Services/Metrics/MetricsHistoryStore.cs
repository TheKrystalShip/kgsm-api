using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Metrics;

/// <summary>
/// The single writer of the metrics history store (M9 Increment 1). Batched multi-row INSERT via raw
/// SQL for throughput (the 15s cadence can produce ~70 rows per flush for ~10 servers + host; EF
/// per-row SaveChanges would be needlessly expensive). Own DI scope per flush (the same pattern as
/// <see cref="Audit.AuditService"/>), serialized by a write gate (SQLite single-writer per file).
/// </summary>
public sealed class MetricsHistoryStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MetricsHistoryStore> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    public MetricsHistoryStore(IServiceScopeFactory scopeFactory, ILogger<MetricsHistoryStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        if (_ensured) return;
        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured) return;
            using IServiceScope scope = _scopeFactory.CreateScope();
            MetricsDbContext db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

            // auto_vacuum MUST be set BEFORE tables exist (SQLite ignores it after).
            // WAL can be set any time. Open the connection, set both pragmas, then create tables.
            SqliteConnection conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using (SqliteCommand pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL;";
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>Write a batch of raw sample rows in a single transaction. Rows with duplicate PKs
    /// are silently replaced (idempotent re-flush after a crash).</summary>
    public async Task WriteSamplesAsync(IReadOnlyList<MetricSample> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            MetricsDbContext db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            SqliteConnection conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using SqliteTransaction tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO sample (entity_kind, entity_id, metric, ts, value) VALUES ($k, $i, $m, $t, $v)";
            SqliteParameter pKind = cmd.Parameters.Add("$k", SqliteType.Text);
            SqliteParameter pId = cmd.Parameters.Add("$i", SqliteType.Text);
            SqliteParameter pMetric = cmd.Parameters.Add("$m", SqliteType.Text);
            SqliteParameter pTs = cmd.Parameters.Add("$t", SqliteType.Integer);
            SqliteParameter pVal = cmd.Parameters.Add("$v", SqliteType.Real);
            cmd.Prepare();

            foreach (MetricSample row in rows)
            {
                pKind.Value = row.EntityKind;
                pId.Value = row.EntityId;
                pMetric.Value = row.Metric;
                pTs.Value = row.Ts;
                pVal.Value = row.Value;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("metrics history: wrote {Count} sample rows", rows.Count);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Write rolled-up bucket rows. INSERT OR REPLACE for idempotent re-rollups.</summary>
    public async Task WriteRollupsAsync(IReadOnlyList<MetricRollup> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            MetricsDbContext db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            SqliteConnection conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using SqliteTransaction tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO rollup (entity_kind, entity_id, metric, bucket_ts, avg, min, max, n) VALUES ($k, $i, $m, $t, $a, $mn, $mx, $n)";
            SqliteParameter pKind = cmd.Parameters.Add("$k", SqliteType.Text);
            SqliteParameter pId = cmd.Parameters.Add("$i", SqliteType.Text);
            SqliteParameter pMetric = cmd.Parameters.Add("$m", SqliteType.Text);
            SqliteParameter pTs = cmd.Parameters.Add("$t", SqliteType.Integer);
            SqliteParameter pAvg = cmd.Parameters.Add("$a", SqliteType.Real);
            SqliteParameter pMin = cmd.Parameters.Add("$mn", SqliteType.Real);
            SqliteParameter pMax = cmd.Parameters.Add("$mx", SqliteType.Real);
            SqliteParameter pN = cmd.Parameters.Add("$n", SqliteType.Integer);
            cmd.Prepare();

            foreach (MetricRollup row in rows)
            {
                pKind.Value = row.EntityKind;
                pId.Value = row.EntityId;
                pMetric.Value = row.Metric;
                pTs.Value = row.BucketTs;
                pAvg.Value = row.Avg;
                pMin.Value = row.Min;
                pMax.Value = row.Max;
                pN.Value = row.N;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("metrics history: wrote {Count} rollup rows", rows.Count);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Delete raw samples older than <paramref name="cutoffMs"/> (unix ms).</summary>
    public async Task<int> PruneRawAsync(long cutoffMs, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            MetricsDbContext db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            SqliteConnection conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sample WHERE ts < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoffMs);
            int deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (deleted > 0)
                _logger.LogDebug("metrics history: pruned {Count} raw samples older than {CutoffMs}", deleted, cutoffMs);
            return deleted;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Delete rollup buckets older than <paramref name="cutoffMs"/> (unix ms).</summary>
    public async Task<int> PruneRollupsAsync(long cutoffMs, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            MetricsDbContext db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            SqliteConnection conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM rollup WHERE bucket_ts < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoffMs);
            int deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (deleted > 0)
                _logger.LogDebug("metrics history: pruned {Count} rollup rows older than {CutoffMs}", deleted, cutoffMs);
            return deleted;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Run PRAGMA incremental_vacuum to reclaim free pages after a prune.</summary>
    public async Task VacuumAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            MetricsDbContext db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            SqliteConnection conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA incremental_vacuum;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Roll up complete buckets from raw samples into the rollup table. Only buckets whose
    /// window is fully closed (bucket_start + step &lt;= now) are processed — the current open bucket
    /// is never touched. Idempotent (INSERT OR REPLACE).</summary>
    public async Task RollupAsync(int stepMinutes, long nowMs, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        long stepMs = stepMinutes * 60_000L;
        long currentBucketStart = (nowMs / stepMs) * stepMs;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            MetricsDbContext db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            SqliteConnection conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO rollup (entity_kind, entity_id, metric, bucket_ts, avg, min, max, n)
                SELECT entity_kind, entity_id, metric,
                       (ts / $step) * $step AS bucket_ts,
                       AVG(value), MIN(value), MAX(value), COUNT(*)
                FROM sample
                WHERE ts < $current_bucket
                GROUP BY entity_kind, entity_id, metric, bucket_ts";
            cmd.Parameters.AddWithValue("$step", stepMs);
            cmd.Parameters.AddWithValue("$current_bucket", currentBucketStart);
            int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows > 0)
                _logger.LogDebug("metrics history: rolled up {Count} buckets (step={StepMin}min)", rows, stepMinutes);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Read raw samples for an entity+metric in a time range, ordered by ts ascending.</summary>
    public async Task<List<MetricSample>> ReadRawAsync(
        string entityKind, string entityId, string? metric, long fromMs, long toMs, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = _scopeFactory.CreateScope();
        MetricsDbContext db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        SqliteConnection conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand cmd = conn.CreateCommand();

        if (metric is not null)
        {
            cmd.CommandText = "SELECT entity_kind, entity_id, metric, ts, value FROM sample WHERE entity_kind = $k AND entity_id = $i AND metric = $m AND ts >= $from AND ts <= $to ORDER BY ts";
            cmd.Parameters.AddWithValue("$m", metric);
        }
        else
        {
            cmd.CommandText = "SELECT entity_kind, entity_id, metric, ts, value FROM sample WHERE entity_kind = $k AND entity_id = $i AND ts >= $from AND ts <= $to ORDER BY ts";
        }
        cmd.Parameters.AddWithValue("$k", entityKind);
        cmd.Parameters.AddWithValue("$i", entityId);
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);

        var results = new List<MetricSample>();
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new MetricSample
            {
                EntityKind = reader.GetString(0),
                EntityId = reader.GetString(1),
                Metric = reader.GetString(2),
                Ts = reader.GetInt64(3),
                Value = reader.GetDouble(4)
            });
        }
        return results;
    }

    /// <summary>Read rollup buckets for an entity in a time range, ordered by bucket_ts ascending.</summary>
    public async Task<List<MetricRollup>> ReadRollupAsync(
        string entityKind, string entityId, string? metric, long fromMs, long toMs, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = _scopeFactory.CreateScope();
        MetricsDbContext db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        SqliteConnection conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand cmd = conn.CreateCommand();

        if (metric is not null)
        {
            cmd.CommandText = "SELECT entity_kind, entity_id, metric, bucket_ts, avg, min, max, n FROM rollup WHERE entity_kind = $k AND entity_id = $i AND metric = $m AND bucket_ts >= $from AND bucket_ts <= $to ORDER BY bucket_ts";
            cmd.Parameters.AddWithValue("$m", metric);
        }
        else
        {
            cmd.CommandText = "SELECT entity_kind, entity_id, metric, bucket_ts, avg, min, max, n FROM rollup WHERE entity_kind = $k AND entity_id = $i AND bucket_ts >= $from AND bucket_ts <= $to ORDER BY bucket_ts";
        }
        cmd.Parameters.AddWithValue("$k", entityKind);
        cmd.Parameters.AddWithValue("$i", entityId);
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);

        var results = new List<MetricRollup>();
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new MetricRollup
            {
                EntityKind = reader.GetString(0),
                EntityId = reader.GetString(1),
                Metric = reader.GetString(2),
                BucketTs = reader.GetInt64(3),
                Avg = reader.GetDouble(4),
                Min = reader.GetDouble(5),
                Max = reader.GetDouble(6),
                N = reader.GetInt32(7)
            });
        }
        return results;
    }
}
