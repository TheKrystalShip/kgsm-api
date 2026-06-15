using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Smoke-only diagnostics (exercised by scripts/smoke.sh, not part of the frontend
/// surface): <c>_throw</c> proves the unhandled-exception path produces the error
/// envelope natively; <c>_dbcheck</c> proves the EF Core + SQLite wiring round-trips.
/// Both are de-risk probes — <b>admin-gated (M4·a)</b> since <c>_dbcheck</c> touches the DB
/// and <c>_throw</c> forces a 500; the secure-by-default fallback would require auth anyway,
/// this pins the tier explicitly.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = AuthPolicy.Admin)]
public sealed class DiagnosticsController(AppDbContext db) : ControllerBase
{
    [HttpGet("_throw")]
    public IActionResult Throw() => throw new InvalidOperationException("smoke: deliberate throw");

    [HttpGet("_dbcheck")]
    public async Task<DbCheckResult> DbCheck()
    {
        // A READ round-trip (connect → ensure schema → query) — proves the EF Core + SQLite wiring
        // without writing. The audit table is append-only and immutable, so a probe must never insert
        // a fake row (it would be an indelible bogus audit fact). EnsureCreated lands the schema on a
        // fresh DB; against an existing DB it is a no-op (no migrations — see AppDbContext).
        await db.Database.EnsureCreatedAsync();
        int auditRows = await db.Audit.CountAsync();
        return new DbCheckResult("EF Core 10 + Microsoft.Data.Sqlite", auditRows);
    }
}

public sealed record DbCheckResult(string Driver, int AuditRows);
