using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Controllers;

/// <summary>The <c>GET /api/v1</c> root — the frontend's connectivity handshake and
/// version check ("reach the API"). Deliberately OPEN (the SPA checks reachability before
/// login), so it opts out of the secure-by-default fallback (M4·a).</summary>
[ApiController]
[Route("api/v1")]
[AllowAnonymous]
public sealed class MetaController : ControllerBase
{
    [HttpGet]
    public ApiInfo Get() => new("kgsm-api", ApiInfo.ApiVersion, DateTimeOffset.UtcNow);
}
