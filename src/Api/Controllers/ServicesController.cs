using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The host's KGSM leaf-service control center — <c>GET /hosts/{id}/services</c> (the host "Services" tab).
/// Returns one row per configured leaf (watchdog, monitor, assistant, firewall, api, bot) joining its live
/// <b>systemd</b> liveness (<see cref="SystemdReader"/>) with the api's deep-health probe where it has one
/// (<see cref="LeafHealthMonitor"/>). Host-OS introspection, sourced directly (like the host logs / file
/// browser), NOT via kgsm-lib.
/// <para>
/// Gated at <b>operator</b> — the same host-internals sensitivity as the host logs (unit names, pids,
/// memory, enablement). The host deep-dive page is already admin-gated on the frontend, so reaching here
/// clears the read gate. <strong>Read-only in this slice</strong> — start/stop/restart controls are a later
/// increment (they need a polkit grant scoped to <c>kgsm-*.service</c>, an admin gate, and audit rows).
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/hosts/{id}/services")]
[Authorize(Policy = AuthPolicy.Operator)]
public sealed class ServicesController(ServicesAggregator services, ApiOptions options) : ControllerBase
{
    /// <summary><c>GET /hosts/{id}/services</c> → <c>{ data:[LeafService] }</c> in catalog order. Per-host
    /// api: the only valid <c>{id}</c> is this host (unknown ⇒ 404, mirroring the other host surfaces).</summary>
    [HttpGet]
    public async Task<ActionResult<ServicesSnapshot>> GetServices(string id, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        return await services.SnapshotAsync(ct);
    }
}
