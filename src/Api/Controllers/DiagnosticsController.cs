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
        await db.Database.EnsureCreatedAsync();
        db.Probes.Add(new Probe { Note = "ef-roundtrip", At = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        int probes = await db.Probes.CountAsync();
        return new DbCheckResult("EF Core 10 + Microsoft.Data.Sqlite", probes);
    }
}

public sealed record DbCheckResult(string Driver, int Probes);
