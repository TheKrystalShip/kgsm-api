using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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

    /// <summary>The library RAWG.io metadata cache, one row per blueprint (cover/hero/description/genres/
    /// tags — the M8·a library increment). Created by the same <c>EnsureCreated</c>; the deployed DB needs a
    /// one-time wipe when it lands.</summary>
    public DbSet<RawgEntry> RawgEntries => Set<RawgEntry>();

    /// <summary>This host's operator-editable identity overrides (region/label) — one row, keyed by host id.
    /// Mapped here so EF reads/writes it; on an EXISTING DB it is instead created by
    /// <see cref="Services.Aggregation.HostSettingsStore"/>'s idempotent <c>CREATE TABLE IF NOT EXISTS</c>
    /// (EnsureCreated no-ops there, and we must not wipe the shared audit log).</summary>
    public DbSet<HostSettingsEntity> HostSettings => Set<HostSettingsEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IntegrationEntity>(e =>
        {
            e.ToTable("integrations");
            e.HasKey(i => i.Provider);
        });

        modelBuilder.Entity<RawgEntry>(e =>
        {
            e.ToTable("rawg_entry");
            e.HasKey(r => r.BlueprintId);
            // Store FetchedAt as UTC ticks (long) so the worker's 30-day-old comparison is a translatable
            // INTEGER >= (the same posture as AuditEntry.Ts — SQLite has no date type / EF can't translate a
            // DateTimeOffset comparison stored as TEXT). Round-trips to a UTC DateTimeOffset on read.
            e.Property(r => r.FetchedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<HostSettingsEntity>(e =>
        {
            e.ToTable("host_settings");
            e.HasKey(h => h.Id);
            // Store UpdatedAt as UTC ticks (long) — the same posture as AuditEntry.Ts / RawgEntry.FetchedAt
            // (SQLite has no date type). A ValueConverter on the non-nullable underlying type; EF composes
            // it with the property's nullability (NULL stays NULL). Round-trips to a UTC DateTimeOffset.
            e.Property(h => h.UpdatedAt).HasConversion(
                new ValueConverter<DateTimeOffset, long>(
                    v => v.UtcTicks,
                    v => new DateTimeOffset(v, TimeSpan.Zero)));
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit");
            e.HasKey(a => a.RowId);
            // long key -> SQLite INTEGER PRIMARY KEY (a rowid alias), store-generated on insert.
            e.Property(a => a.RowId).ValueGeneratedOnAdd();
            e.HasIndex(a => a.Id).IsUnique();

            // Store Ts as UTC ticks (long). SQLite has no date type and EF Core can't translate a
            // DateTimeOffset `>=` comparison (it stores it as TEXT but emits no comparison SQL) — which
            // the `?since=` time-range filter needs. As ticks the comparison is a plain INTEGER `>=`,
            // fully translatable, and the value round-trips to a UTC DateTimeOffset on read (every audit
            // timestamp is UTC). Ordering is unaffected (keyset is on RowId, not Ts).
            e.Property(a => a.Ts).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));

            // Scope/filter indexes, each descending on RowId so a keyset page (RowId < cursor,
            // newest-first) is index-friendly — mirrors the §3·d CREATE INDEX … (col, rowid DESC) set.
            e.HasIndex(a => new { a.ServerId, a.RowId }).IsDescending(false, true);
            e.HasIndex(a => new { a.HostId, a.RowId }).IsDescending(false, true);
            e.HasIndex(a => new { a.ActorName, a.RowId }).IsDescending(false, true);
            e.HasIndex(a => new { a.Severity, a.RowId }).IsDescending(false, true);
        });
    }
}
