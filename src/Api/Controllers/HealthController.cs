using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Controllers;

/// <summary>Ops liveness at <c>GET /healthz</c> — ours, not a frontend contract (see
/// <see cref="HealthStatus"/>). Lives outside <c>/api/v1</c> by convention.</summary>
[ApiController]
public sealed class HealthController : ControllerBase
{
    [HttpGet("/healthz")]
    public HealthStatus Get() => new("ok", "kgsm-api", DateTimeOffset.UtcNow);
}
