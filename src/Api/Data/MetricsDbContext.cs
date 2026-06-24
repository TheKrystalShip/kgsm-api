using Microsoft.EntityFrameworkCore;

namespace TheKrystalShip.Api.Data;

/// <summary>
/// Dedicated EF Core context for the metrics history store (M9) — a separate <c>metrics.db</c> file,
/// isolated from the audit DB (D4: audit is precious/append-only/low-volume; metrics are
/// high-churn/prunable; SQLite single-writer per file → isolation avoids contention). Schema via
/// <c>EnsureCreated</c> (same posture as <see cref="AppDbContext"/>: wipe the DB on schema change).
/// WAL mode + incremental auto-vacuum configured at creation.
/// </summary>
public sealed class MetricsDbContext : DbContext
{
    public MetricsDbContext(DbContextOptions<MetricsDbContext> options) : base(options) { }

    public DbSet<MetricSample> Samples => Set<MetricSample>();
    public DbSet<MetricRollup> Rollups => Set<MetricRollup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MetricSample>(e =>
        {
            e.ToTable("sample");
            e.Property(s => s.EntityKind).HasColumnName("entity_kind");
            e.Property(s => s.EntityId).HasColumnName("entity_id");
            e.Property(s => s.Metric).HasColumnName("metric");
            e.Property(s => s.Ts).HasColumnName("ts");
            e.Property(s => s.Value).HasColumnName("value");
            e.HasKey(s => new { s.EntityKind, s.EntityId, s.Metric, s.Ts });
        });

        modelBuilder.Entity<MetricRollup>(e =>
        {
            e.ToTable("rollup");
            e.Property(r => r.EntityKind).HasColumnName("entity_kind");
            e.Property(r => r.EntityId).HasColumnName("entity_id");
            e.Property(r => r.Metric).HasColumnName("metric");
            e.Property(r => r.BucketTs).HasColumnName("bucket_ts");
            e.Property(r => r.Avg).HasColumnName("avg");
            e.Property(r => r.Min).HasColumnName("min");
            e.Property(r => r.Max).HasColumnName("max");
            e.Property(r => r.N).HasColumnName("n");
            e.HasKey(r => new { r.EntityKind, r.EntityId, r.Metric, r.BucketTs });
        });
    }
}

/// <summary>Tier-1 raw sample row: one (entity, metric, ts) point. Null metrics are never written (honest gap).</summary>
public sealed class MetricSample
{
    public required string EntityKind { get; set; }
    public required string EntityId { get; set; }
    public required string Metric { get; set; }
    public long Ts { get; set; }
    public double Value { get; set; }
}

/// <summary>Tier-2 rolled-up bucket: avg+min+max+n over a time window. One row per (entity, metric, bucket).</summary>
public sealed class MetricRollup
{
    public required string EntityKind { get; set; }
    public required string EntityId { get; set; }
    public required string Metric { get; set; }
    public long BucketTs { get; set; }
    public double Avg { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public int N { get; set; }
}
