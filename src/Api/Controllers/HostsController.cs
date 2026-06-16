using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
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
[Authorize(Policy = AuthPolicy.Viewer)] // reads — viewer and up (M4·a)
public sealed class HostsController(HostAggregator aggregator, ApiOptions options) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<Host>> GetAll(CancellationToken ct) =>
        [await aggregator.GetHostAsync(ct)];

    [HttpGet("{id}")]
    public async Task<ActionResult<Host>> GetById(string id, CancellationToken ct)
    {
        // Check the id against this host's identity BEFORE the detail build, so an unknown id never
        // triggers the firewall probe. Unknown id -> 404 (UseStatusCodePages renders the envelope).
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        // Detail view: the list element + the M6·b open-ports grid (one on-demand firewall probe).
        return await aggregator.GetHostDetailAsync(ct);
    }
}
