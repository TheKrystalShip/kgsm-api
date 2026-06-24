using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Controllers;

/// <summary>The <c>GET /api/v1/ping</c> latency target — a frontend contract (unlike the
/// ops-only <c>/health</c>): the SPA times the round trip to render the dashboard's Ping KPI.
/// Deliberately OPEN (<see cref="AllowAnonymous"/>) and header-light so the SPA can hit it
/// with a bare GET (no <c>Authorization</c> → no CORS preflight to inflate the first reading)
/// and so it works before login. Returns the smallest honest body and touches nothing
/// (no DB/leaf) — the measurement must reflect the link, not server work.</summary>
[ApiController]
[Route("api/v1/ping")]
[AllowAnonymous]
public sealed class PingController : ControllerBase
{
    [HttpGet]
    public PingResponse Get() => new(true);
}
