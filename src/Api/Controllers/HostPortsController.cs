using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// What is bound on this host's ports, and where two claimants want the same one —
/// <c>GET /hosts/{id}/ports</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two scans behind one read, and each carries its own state. One can answer while the other cannot,
/// and an unread conflict scan reported as an empty list is invisible: no conflicts is the ordinary
/// answer, so a failure collapsing into it looks exactly like a healthy host.
/// </para>
/// <para>
/// A conflict is the engine's own finding. Nothing here re-derives one by comparing instance configs
/// against each other — the engine knows what a configured port is and this surface does not.
/// </para>
/// <para>
/// Operator-gated, like the rest of the host-internals surface: what is listening on a machine and
/// under which process is a map of it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{id}/ports")]
[Authorize(Policy = AuthPolicy.Operator)]
public sealed class HostPortsController(ApiOptions options) : ControllerBase
{
    /// <summary>
    /// <c>GET /hosts/{id}/ports</c> → <see cref="HostPortsDto"/>. <b>404</b> for another host's id or a
    /// host with no engine; a scan that could not be read reports its own half as unavailable rather
    /// than failing the request, because the other half is still an answer.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (!options.KgsmProvisioned ||
            HttpContext.RequestServices.GetService(typeof(INetworkService)) is not INetworkService network)
            return NotFound();

        // Which instance is configured for a port is joined from the engine's own instance list rather
        // than guessed from the process name, which is a binary's name and not a server's.
        var instances = HttpContext.RequestServices.GetService(typeof(IInstanceService)) as IInstanceService;

        List<HostPort>? used = await Task.Run(network.ListUsedPortsDetailed, ct).ConfigureAwait(false);
        List<PortConflict>? conflicts = await Task.Run(network.FindConflictsDetailed, ct).ConfigureAwait(false);
        IReadOnlyDictionary<(int, string), string> owners = await Task.Run(
            () => PortOwners(instances), ct).ConfigureAwait(false);

        return Ok(new HostPortsDto(
            used is null ? "unavailable" : "available",
            used is null
                ? []
                : [.. used.Select(p => new HostPortDto(
                    p.Port,
                    p.Protocol,
                    string.IsNullOrWhiteSpace(p.Process) ? null : p.Process,
                    owners.GetValueOrDefault((p.Port, p.Protocol))))],
            conflicts is null ? "unavailable" : "available",
            conflicts is null
                ? []
                : [.. conflicts.Select(c => new PortConflictDto(
                    c.Port, c.Protocol, c.Instance, c.Other,
                    string.Equals(c.Kind, "instance", StringComparison.Ordinal)))]));
    }

    /// <summary>
    /// Which instance declares each (port, protocol), from the engine's own instance list. An engine
    /// that will not answer yields no owners rather than failing the read: the ports are measured
    /// either way and an unattributed one is honestly unattributed.
    /// </summary>
    private static IReadOnlyDictionary<(int, string), string> PortOwners(IInstanceService? instances)
    {
        var owners = new Dictionary<(int, string), string>();
        if (instances is null)
            return owners;

        try
        {
            foreach ((string id, Instance instance) in instances.GetAllOrNull() ?? [])
                foreach ((int port, string protocol) in instance.Ports.Expand())
                    owners.TryAdd((port, protocol), id);
        }
        catch (Exception)
        {
            return owners;
        }

        return owners;
    }
}
