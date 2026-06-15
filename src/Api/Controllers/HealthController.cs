using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Controllers;

/// <summary>Ops liveness at <c>GET /health</c> — ours, not a frontend contract (see
/// <see cref="HealthStatus"/>). The unified ecosystem health path (one <c>/health</c>
/// everywhere — this is the API's own, distinct from the leaf <c>/health</c> probes it
/// polls). Lives outside <c>/api/v1</c> by convention.</summary>
[ApiController]
public sealed class HealthController : ControllerBase
{
    [HttpGet("/health")]
    public HealthStatus Get() => new("ok", "kgsm-api", DateTimeOffset.UtcNow);
}
