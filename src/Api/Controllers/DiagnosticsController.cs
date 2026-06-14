using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Smoke-only diagnostics (exercised by scripts/smoke.sh, not part of the frontend
/// surface): <c>_throw</c> proves the unhandled-exception path produces the error
/// envelope natively; <c>_dbcheck</c> proves the EF Core + SQLite wiring round-trips.
/// Both are de-risk probes — remove/restrict before exposing the host publicly.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class DiagnosticsController(AppDbContext db) : ControllerBase
{
    [HttpGet("_throw")]
    public IActionResult Throw() => throw new InvalidOperationException("smoke: deliberate throw");

    [HttpGet("_dbcheck")]
    public async Task<DbCheckResult> DbCheck()
    {
        await db.Database.EnsureCreatedAsync();
        db.Probes.Add(new Probe { Note = "ef-roundtrip", At = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        int probes = await db.Probes.CountAsync();
        return new DbCheckResult("EF Core 10 + Microsoft.Data.Sqlite", probes);
    }
}

public sealed record DbCheckResult(string Driver, int Probes);
