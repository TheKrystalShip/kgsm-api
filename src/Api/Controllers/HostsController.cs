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
/// <c>PATCH /hosts/{id}</c> (<c>api:hosts.write</c>) edits the host's identity overrides (label/region).
/// </summary>
[ApiController]
[Route("api/v1/hosts")]
public sealed class HostsController(
    HostAggregator aggregator, HostSettingsStore settings, ApiOptions options, NodeAccess access) : ControllerBase
{
    // The identity strings are operator-facing labels, not free-form blobs — bound so a typo can't store
    // a megabyte. Generous but finite; over-length is a 400, never a silent truncation.
    private const int MaxIdentityLength = 100;

    [HttpGet]
    [RequiresAction(ActionIds.HostsRead)]
    public async Task<IReadOnlyList<Host>> GetAll(CancellationToken ct) =>
        [await aggregator.GetHostAsync(ct)];

    [HttpGet("{id}")]
    [RequiresAction(ActionIds.HostsRead)]
    public async Task<ActionResult<Host>> GetById(string id, CancellationToken ct)
    {
        // Check the id against this host's identity BEFORE the detail build, so an unknown id never
        // triggers the firewall probe. Unknown id -> 404 (UseStatusCodePages renders the envelope).
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        // Detail view: the list element + the M6·b open-ports grid (one on-demand firewall probe).
        Host host = await aggregator.GetHostDetailAsync(ct);

        // Without api:services.read — the action that shows this host's units, pids and memory — GPU
        // contexts belonging to no unit this host knows about lose their pid and name. The monitor sees
        // everything using the card, and a host runs more than KGSM — naming somebody's own work to every
        // reader is the mistake the audit rows already avoid. The withheld memory is kept as one unnamed
        // row per device, so the card still totals honestly: what is withheld is an identity, never a
        // quantity.
        return await access.AllowsAsync(User, ActionIds.ServicesRead, access.NodeTarget, ct)
            ? host
            : host with { GpuProcesses = GpuRedaction.Redacted(host.GpuProcesses) };
    }

    /// <summary>Edit this host's identity overrides — the editable half of the card (architecture §4·a).
    /// Sparse: an absent field is unchanged, an explicit empty string clears the override (back to the
    /// <c>Api__*</c> config default). Returns the refreshed host detail.</summary>
    [HttpPatch("{id}")]
    [RequiresAction(ActionIds.HostsWrite)]
    public async Task<ActionResult<Host>> Patch(string id, [FromBody] HostPatch? body, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        body ??= new HostPatch(null, null);
        HostSettingsRecord current = await settings.GetAsync(ct);

        // Sparse + clear-on-blank: null => unchanged; "" => clear (config default); value => set (trimmed).
        if (!TryResolve(body.Label, current.Label, out string? label, out ActionResult? labelError)) return labelError!;
        if (!TryResolve(body.Region, current.Region, out string? region, out ActionResult? regionError)) return regionError!;

        await settings.SaveAsync(label, region, ct);
        return await aggregator.GetHostDetailAsync(ct);
    }

    // null patch field => keep current; blank => clear (null); otherwise the trimmed value (length-bounded).
    private bool TryResolve(string? patchValue, string? current, out string? resolved, out ActionResult? error)
    {
        error = null;
        if (patchValue is null) { resolved = current; return true; }
        string trimmed = patchValue.Trim();
        if (trimmed.Length == 0) { resolved = null; return true; }
        if (trimmed.Length > MaxIdentityLength)
        {
            resolved = null;
            error = StatusCode(StatusCodes.Status400BadRequest, new ErrorEnvelope(new ErrorBody(
                "bad_request", $"identity values must be at most {MaxIdentityLength} characters")));
            return false;
        }
        resolved = trimmed;
        return true;
    }
}
