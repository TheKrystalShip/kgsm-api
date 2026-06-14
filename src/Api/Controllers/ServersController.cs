using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The servers read surface (architecture §3) — M1·b. <c>GET /servers</c> returns this host's
/// kgsm instances, each the honest join of domain + run-state (kgsm-lib) with per-instance metrics
/// (kgsm-monitor); see <see cref="Server"/> for the frozen shape and its deliberate divergences from
/// the aspirational example. Per-host: every server carries this host's <c>hostId</c>, and the SPA
/// fans out across hosts client-side.
/// </summary>
[ApiController]
[Route("api/v1/servers")]
public sealed class ServersController(ServerAggregator aggregator) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<Server>> GetAll(CancellationToken ct) =>
        await aggregator.GetServersAsync(ct);

    /// <summary>
    /// One server's record. For M1·b this is the same shape as a list element (full detail —
    /// console, files, players — arrives in later milestones), filtered out of the bulk join so the
    /// list and detail views never diverge.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Server>> GetById(string id, CancellationToken ct)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct);
        Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

        // Unknown id -> 404 with no body; UseStatusCodePages renders the not_found envelope.
        if (server is null)
            return NotFound();

        return server;
    }
}
