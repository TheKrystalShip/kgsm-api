using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Controllers;

/// <summary>The <c>GET /api/v1</c> root — the frontend's connectivity handshake and
/// version check ("reach the API").</summary>
[ApiController]
[Route("api/v1")]
public sealed class MetaController : ControllerBase
{
    [HttpGet]
    public ApiInfo Get() => new("kgsm-api", "v1", DateTimeOffset.UtcNow);
}
