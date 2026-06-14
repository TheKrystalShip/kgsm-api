using Microsoft.EntityFrameworkCore;

namespace TheKrystalShip.Api.Data;

/// <summary>
/// EF Core context for the API's own operational metadata — the small set of tables
/// the aggregator persists (the domain itself is live-scraped from the leaves, never
/// stored). Real tables arrive with their milestones, via EF migrations:
/// <c>Session</c> (M4 auth) and an append-only <c>AuditEntry</c> (M5). For M0 it holds
/// only <see cref="Probe"/>, a throwaway round-trip check that the EF+SQLite wiring
/// builds and runs on this toolchain — remove it when the first real table lands.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Probe> Probes => Set<Probe>();
}

/// <summary>M0 de-risk only — see <see cref="AppDbContext"/>.</summary>
public sealed class Probe
{
    public int Id { get; set; }
    public string Note { get; set; } = "";
    public DateTimeOffset At { get; set; }
}
