using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Engine;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The kgsm engine's identity — <c>GET /hosts/{id}/engine</c>, the Overview of the engine's pseudo-leaf
/// page: version and directory layout, straight from the engine via <see cref="EngineInfoService"/>.
/// </summary>
/// <remarks>
/// The engine sits on the Services board as a pseudo-leaf but is deliberately NOT in the leaf catalog:
/// it has no systemd unit, no journal of its own, and no leaf config descriptor, so none of the per-leaf
/// endpoints apply to it — this endpoint is its whole detail surface. Gated at operator like the rest of
/// the Services surface (an install path is host internals).
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{id}/engine")]
[Authorize(Policy = AuthPolicy.Operator)]
public sealed class EngineController(EngineInfoService engine, ApiOptions options) : ControllerBase
{
    /// <summary>
    /// <c>GET /hosts/{id}/engine</c> → <see cref="EngineInfo"/>. <b>404</b> when this host has no engine
    /// configured (a host can run the panel without one); <b>503</b> when it has one that would not
    /// answer — a different fact, and the one worth retrying.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (!options.KgsmProvisioned)
            return NotFound();

        EngineInfo? info = await engine.GetAsync(ct).ConfigureAwait(false);
        return info is null
            ? StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ErrorEnvelope(new ErrorBody("unavailable", "the kgsm engine did not answer")))
            : Ok(info);
    }
}
