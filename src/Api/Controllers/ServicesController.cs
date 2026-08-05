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
public sealed class ServicesController(
    ServicesAggregator services,
    LeafCommandStore commands,
    ApiOptions options) : ControllerBase
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

    /// <summary>
    /// <c>GET /hosts/{id}/services/{leaf}/commands</c> → the leaf's own catalog of the commands it answers
    /// to, straight from the manifest it ships. <b>404 when it ships none</b> — most leaves take no
    /// commands at all, and that is the honest answer rather than an empty list, which would read as "this
    /// one takes commands and currently has none".
    /// </summary>
    /// <remarks>
    /// Read-only reference material about a leaf, so it sits with the rest of the read-only Services
    /// surface at operator rather than behind the admin config gate. Nothing here is interpreted: the
    /// manifest's own words for what each command does, and for what the leaf checks before running one,
    /// are passed through — this API cannot verify a gate it does not implement, so it does not restate it.
    /// </remarks>
    [HttpGet("{leaf}/commands")]
    public ActionResult<LeafCommandManifest> GetCommands(string id, string leaf)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        LeafCommandManifest? manifest = commands.For(leaf);
        return manifest is null ? NotFound() : manifest;
    }
}
