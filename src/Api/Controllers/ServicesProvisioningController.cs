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
    LeafConfigCatalog catalog,
    ApiJournal journal,
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
        if (!IsThisHost(id) || !catalog.IsConfigTarget(leaf))
            return NotFound();

        body ??= new LeafConfigUpdate(null, null);
        string? actor = AuditPrincipal.ActorString(User);
        LeafConfigApplyResponse resp = await config.ApplyAsync(leaf, body, actor, AuditOrigin.Api, ct);
        if (resp.ErrorMessage is not null)
        {
            // 409 when the request is fine but this host cannot deliver it (no override drop-in for the
            // leaf); 400 when the body itself is wrong.
            return resp.IsConflict
                ? StatusCode(StatusCodes.Status409Conflict,
                    new ErrorEnvelope(new ErrorBody("conflict", resp.ErrorMessage)))
                : StatusCode(StatusCodes.Status400BadRequest,
                    new ErrorEnvelope(new ErrorBody("bad_request", resp.ErrorMessage)));
        }
        return Ok(resp.Result);
    }

    /// <summary>
    /// <c>PUT .../services/reactor/rules</c> — store the rules this host's reactor runs, point the leaf
    /// at them and restart it onto them, then report what it made of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write half of the rule editor. The reactor's socket is read-only — it publishes what a rule
    /// may be made of and what one would decide, and never accepts one — so storing is this API's half:
    /// it writes a file and restarts the unit through the grant it already holds for every other leaf
    /// setting. Nothing off this host acquires the ability to tell a leaf what to think.
    /// </para>
    /// <para>
    /// ⚠ <b>What a rule means is the leaf's judgement, not this API's.</b> The body is checked for being
    /// a rules document and nothing more: which signals, operators and actions exist belongs to the
    /// running build, and a second copy of that here is how the panel and the leaf come to disagree
    /// about which rules are valid. The leaf's verdict is read back and returned as <c>problems</c>.
    /// </para>
    /// <para>
    /// ⚠ <b>Problems are not an error.</b> A file with one bad rule in it stores, and the rest runs — so
    /// this answers <c>200</c> with the leaf's complaints rather than refusing the whole write, which
    /// would make a partly-good file impossible to save and therefore impossible to fix.
    /// </para>
    /// <para>
    /// <b>409, not 400, when this host is not wired to deliver it</b> — the request is fine and the host
    /// is missing the leaf's override drop-in, the same distinction the config path makes.
    /// </para>
    /// </remarks>
    [HttpPut("reactor/rules")]
    public async Task<IActionResult> PutReactorRules(
        string id, [FromServices] ReactorRulesService rules, CancellationToken ct)
    {
        if (!IsThisHost(id) || !catalog.IsConfigTarget("reactor"))
            return NotFound();

        using var reader = new StreamReader(Request.Body);
        string body = await reader.ReadToEndAsync(ct);

        string? actor = AuditPrincipal.ActorString(User);
        ReactorRulesResult result = await rules.WriteAsync(body, actor, AuditOrigin.Api, ct);

        if (!result.Ok)
        {
            return result.IsConflict
                ? StatusCode(StatusCodes.Status409Conflict,
                    new ErrorEnvelope(new ErrorBody("conflict", result.ErrorMessage!)))
                : StatusCode(StatusCodes.Status400BadRequest,
                    new ErrorEnvelope(new ErrorBody("bad_request", result.ErrorMessage!)));
        }

        return Ok(new ReactorRulesApplied(result.Path!, result.Problems, result.Live));
    }

    private bool IsThisHost(string id) =>
        string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase);

    private async Task AuditProvisioningAsync(string leaf, bool provisioned, CancellationToken ct)
    {
        LeafDescriptor? descriptor = LeafCatalog.Default.FirstOrDefault(l => string.Equals(l.Id, leaf, StringComparison.Ordinal));
        string display = descriptor?.DisplayName ?? leaf;
        await journal.ServiceProvisioningAsync(
            provisioned, leaf, display, AuditPrincipal.ActorString(User) ?? "", AuditOrigin.Api, ct);
    }
}
