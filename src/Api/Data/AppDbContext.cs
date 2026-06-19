using Microsoft.EntityFrameworkCore;

namespace TheKrystalShip.Api.Data;

/// <summary>
/// EF Core context for the API's own operational metadata — the small set of tables the
/// aggregator persists (the domain itself is live-scraped from the leaves, never stored).
/// As of M5 it holds the single append-only <see cref="AuditEntry"/> table (architecture.html
/// §3·d). M4 auth is stateless JWT — no session/user rows.
/// <para>
/// <b>Schema is created via <c>EnsureCreated</c>, not EF migrations.</b> The project has
/// greenfield/dev authority: on a schema change we wipe the dev DB rather than carry a
/// migration history (PLAN.md M5). <c>EnsureCreated</c> is a no-op against an existing DB,
/// so a schema change means deleting the DB file — there is no <c>__EFMigrationsHistory</c>.
/// </para>
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    /// <summary>M8·c — outbound-notification integration config, one row per provider (§3·e). The
    /// first non-audit table; created by the same <c>EnsureCreated</c> (delete the dev DB once when it
    /// lands — EnsureCreated no-ops on an existing DB).</summary>
    public DbSet<IntegrationEntity> Integrations => Set<IntegrationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IntegrationEntity>(e =>
        {
            e.ToTable("integrations");
            e.HasKey(i => i.Provider);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit");
            e.HasKey(a => a.RowId);
            // long key -> SQLite INTEGER PRIMARY KEY (a rowid alias), store-generated on insert.
            e.Property(a => a.RowId).ValueGeneratedOnAdd();
            e.HasIndex(a => a.Id).IsUnique();

            // Scope/filter indexes, each descending on RowId so a keyset page (RowId < cursor,
            // newest-first) is index-friendly — mirrors the §3·d CREATE INDEX … (col, rowid DESC) set.
            e.HasIndex(a => new { a.ServerId, a.RowId }).IsDescending(false, true);
            e.HasIndex(a => new { a.HostId, a.RowId }).IsDescending(false, true);
            e.HasIndex(a => new { a.ActorName, a.RowId }).IsDescending(false, true);
            e.HasIndex(a => new { a.Severity, a.RowId }).IsDescending(false, true);
        });
    }
}
