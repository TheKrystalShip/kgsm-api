using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
// Disambiguate from Microsoft.Extensions.Hosting.Host (pulled in by ImplicitUsings).
using Host = TheKrystalShip.Api.Contracts.Host;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The hosts read surface (architecture §4·a). This api is per-host, so <c>GET /hosts</c>
/// returns exactly the one host it runs on (the SPA fans out across hosts and rolls up
/// client-side — there is no <c>/fleet</c> endpoint). M1·a: capacity + the §4·b capability
/// block, scraped from kgsm-monitor; no kgsm-lib domain join yet (that is <c>/servers</c>, M1·b).
/// </summary>
[ApiController]
[Route("api/v1/hosts")]
public sealed class HostsController(HostAggregator aggregator) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<Host>> GetAll(CancellationToken ct) =>
        [await aggregator.GetHostAsync(ct)];

    [HttpGet("{id}")]
    public async Task<ActionResult<Host>> GetById(string id, CancellationToken ct)
    {
        Host host = await aggregator.GetHostAsync(ct);

        // Unknown id -> 404 with no body; UseStatusCodePages renders the not_found envelope.
        if (!string.Equals(id, host.Id, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        return host;
    }
}
