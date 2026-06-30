using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The <strong>admin</strong> write half of the host Services panel (the leaf-runtime-provisioning/config
/// feature) — connect/disconnect a leaf at runtime (Phase 1) and edit a leaf's config (Phase 2). Separate
/// from the operator-gated read-only <see cref="ServicesController"/>; these mutate, so they are admin-gated
/// (someone who could SSH to the box anyway). Per-host API: a foreign host id → 404; an unknown /
/// non-provisionable leaf → 404.
/// </summary>
[ApiController]
[Route("api/v1/hosts/{id}/services")]
[Authorize(Policy = AuthPolicy.Admin)]
public sealed class ServicesProvisioningController(
    LeafRegistry registry,
    LeafHealthMonitor health,
    ServicesAggregator services,
    LeafConfigService config,
    AuditService audit,
    ApiOptions options) : ControllerBase
{
    /// <summary><c>POST .../services/{leaf}/connect</c> — provision (connect) a leaf at runtime; re-poll so the
    /// SPA's capability set lights up live; audit. Returns the refreshed leaf row.</summary>
    [HttpPost("{leaf}/connect")]
    public Task<IActionResult> Connect(string id, string leaf, CancellationToken ct) =>
        SetProvisionedAsync(id, leaf, provisioned: true, ct);

    /// <summary><c>POST .../services/{leaf}/disconnect</c> — deprovision (disconnect) a leaf at runtime; re-poll
    /// so the SPA's capability set tears down live; audit. Returns the refreshed leaf row.</summary>
    [HttpPost("{leaf}/disconnect")]
    public Task<IActionResult> Disconnect(string id, string leaf, CancellationToken ct) =>
        SetProvisionedAsync(id, leaf, provisioned: false, ct);

    private async Task<IActionResult> SetProvisionedAsync(string id, string leaf, bool provisioned, CancellationToken ct)
    {
        if (!IsThisHost(id) || !ProvisionableLeaf.IsProvisionable(leaf))
            return NotFound();

        await registry.SetProvisionedAsync(leaf, provisioned, ct);
        // Force an immediate capability poll so the WS capabilities.patch fires + GET /hosts is fresh now.
        await health.PollNowAsync(ct);

        await AuditProvisioningAsync(leaf, provisioned, ct);

        // Return the refreshed Services-board row for this leaf (Provisioned now reflects the flip).
        ServicesSnapshot snapshot = await services.SnapshotAsync(ct);
        LeafService? row = snapshot.Data.FirstOrDefault(s => string.Equals(s.Id, leaf, StringComparison.Ordinal));
        return row is null ? NotFound() : Ok(row);
    }

    /// <summary><c>GET .../services/{leaf}/config</c> — the leaf's settable-key manifest joined with the current
    /// overrides (secrets masked). 404 when the leaf is not a config target.</summary>
    [HttpGet("{leaf}/config")]
    public async Task<IActionResult> GetConfig(string id, string leaf, CancellationToken ct)
    {
        if (!IsThisHost(id))
            return NotFound();
        LeafConfig? cfg = await config.GetConfigAsync(leaf, ct);
        return cfg is null ? NotFound() : Ok(cfg);
    }

    /// <summary><c>PUT .../services/{leaf}/config</c> — apply a config update (write → render → restart →
    /// health-canary → auto-rollback). Unknown key / bad value → 400; secrets are write-only + redacted in the
    /// audit. 404 when the leaf is not a config target.</summary>
    [HttpPut("{leaf}/config")]
    public async Task<IActionResult> PutConfig(string id, string leaf, [FromBody] LeafConfigUpdate? body, CancellationToken ct)
    {
        if (!IsThisHost(id) || !LeafConfigManifest.IsConfigTarget(leaf))
            return NotFound();

        body ??= new LeafConfigUpdate(null, null);
        string? actor = AuditPrincipal.ActorString(User);
        LeafConfigApplyResponse resp = await config.ApplyAsync(leaf, body, actor, AuditOrigin.Api, ct);
        if (resp.ErrorMessage is not null)
            return StatusCode(StatusCodes.Status400BadRequest,
                new ErrorEnvelope(new ErrorBody("bad_request", resp.ErrorMessage)));
        return Ok(resp.Result);
    }

    private bool IsThisHost(string id) =>
        string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase);

    private async Task AuditProvisioningAsync(string leaf, bool provisioned, CancellationToken ct)
    {
        LeafDescriptor? descriptor = LeafCatalog.Default.FirstOrDefault(l => string.Equals(l.Id, leaf, StringComparison.Ordinal));
        string display = descriptor?.DisplayName ?? leaf;
        var write = new AuditWrite(
            Ts: DateTimeOffset.UtcNow,
            Origin: AuditOrigin.Api,
            Actor: AuditMapping.ParseActor(AuditPrincipal.ActorString(User)),
            Action: provisioned ? AuditAction.ServiceConnect : AuditAction.ServiceDisconnect,
            Severity: AuditSeverity.Info,
            Target: new AuditTarget(AuditTargetKind.Leaf, leaf, display),
            ServerId: null,
            HostId: options.HostId,
            Summary: provisioned ? $"connected {display}" : $"disconnected {display}",
            Meta: null);
        await audit.AppendAsync(write, ct);
    }
}
