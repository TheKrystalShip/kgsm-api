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
    /// <c>PUT .../services/reactor/rules/{ruleId}</c> — store one rule, by asking the leaf to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This API neither writes rule files nor judges rules.</b> The leaf owns its directory and is
    /// the only thing that knows what the running build can honour; a copy of that knowledge here is
    /// exactly how the panel and the leaf come to disagree about which rules are valid. So this
    /// authenticates the caller, names them to the leaf, and relays the answer — body and status —
    /// without an opinion of its own.
    /// </para>
    /// <para>
    /// <b>A rule at a time, and nothing restarts.</b> The leaf applies the rule in process and the
    /// others carry on undisturbed, so saving one rule is no longer an event in the life of the host.
    /// </para>
    /// <para>
    /// <b>422 is the leaf refusing a rule</b>, with its reasons: nothing was written and nothing
    /// changed. That is an answer to the caller rather than a failure of this relay, so it travels
    /// as-is and the panel shows the problems beside the field that caused them.
    /// </para>
    /// <para>
    /// <b>409, not 400, when this host is not wired to deliver it</b> — the request is fine and the
    /// leaf is unreachable, the same distinction the config path makes.
    /// </para>
    /// </remarks>
    [HttpPut("reactor/rules/{ruleId}")]
    public async Task<IActionResult> PutReactorRule(
        string id, string ruleId, [FromServices] ReactorClient reactor, CancellationToken ct)
    {
        if (!IsThisHost(id) || !catalog.IsConfigTarget("reactor"))
            return NotFound();

        using var reader = new StreamReader(Request.Body);
        string rule = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(rule))
            return BadRequest(new ErrorEnvelope(new ErrorBody("bad_request", "no rule to store")));

        // ⚠ Built from the authenticated principal, never bound from the request. A caller-supplied
        // name would let anybody author a rule as somebody else, and the leaf cannot tell the two
        // apart — it checks the shape and trusts whoever authenticated the person.
        string? actor = AuditPrincipal.ActorString(User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new ErrorEnvelope(new ErrorBody("forbidden",
                    "a rule records who wrote it, and this request names nobody")));
        }

        (string? body, int status) = await reactor.WriteRuleAsync(ruleId, rule, actor, ct);

        if (status == 0)
        {
            return StatusCode(StatusCodes.Status409Conflict,
                new ErrorEnvelope(new ErrorBody("conflict", "the reactor could not be reached")));
        }

        // Audited only when the leaf actually stored it. A refused rule changed nothing, and an audit
        // row for it would read as an edit that happened.
        if (status is >= 200 and < 300)
            await AuditRuleAsync(ruleId, removed: false, ct);

        return Relay(body, status);
    }

    /// <summary>
    /// The leaf's own answer, passed through.
    /// </summary>
    /// <remarks>
    /// ⚠ Parsed only to re-serialise as JSON rather than as a quoted string; nothing here reads a
    /// field. A refusal the leaf phrased in prose (its 400s are text) travels as text, because
    /// rewrapping it would replace the leaf's sentence with this API's guess at what it meant.
    /// </remarks>
    private IActionResult Relay(string? body, int status)
    {
        if (string.IsNullOrWhiteSpace(body))
            return StatusCode(status);

        try
        {
            using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(body);
            return StatusCode(status, parsed.RootElement.Clone());
        }
        catch (System.Text.Json.JsonException)
        {
            return StatusCode(status,
                new ErrorEnvelope(new ErrorBody(
                    status == StatusCodes.Status404NotFound ? "not_found" : "bad_request", body)));
        }
    }

    /// <summary>
    /// <c>DELETE .../services/reactor/rules/{ruleId}</c> — remove a rule's file outright.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Deleting is not retiring, and the panel retires.</b> A retired rule keeps its file so the
    /// decisions it already made still resolve to a rule that can be named — a rule id is the actor on
    /// every one of them. This exists for a rule that was never meant to be, and it is why the panel's
    /// ordinary "turn this off" writes the rule back with <c>retired</c> set instead of calling here.
    /// </remarks>
    [HttpDelete("reactor/rules/{ruleId}")]
    public async Task<IActionResult> DeleteReactorRule(
        string id, string ruleId, [FromServices] ReactorClient reactor, CancellationToken ct)
    {
        if (!IsThisHost(id) || !catalog.IsConfigTarget("reactor"))
            return NotFound();

        (string? body, int status) = await reactor.DeleteRuleAsync(ruleId, ct);

        if (status == 0)
        {
            return StatusCode(StatusCodes.Status409Conflict,
                new ErrorEnvelope(new ErrorBody("conflict", "the reactor could not be reached")));
        }

        if (status == StatusCodes.Status404NotFound)
            return NotFound(new ErrorEnvelope(new ErrorBody("not_found", body ?? "no such rule")));

        await AuditRuleAsync(ruleId, removed: true, ct);
        return Relay(body, status);
    }

    private bool IsThisHost(string id) =>
        string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records that somebody changed what this host's reactor is allowed to think.
    /// </summary>
    /// <remarks>
    /// The rule id rather than its contents: a rule is edited through its own surface where the before
    /// and after are both visible, and an audit row carrying a whole rule document would be unreadable
    /// beside every other row. What the log is answering is who changed which rule, and when.
    /// </remarks>
    private Task AuditRuleAsync(string ruleId, bool removed, CancellationToken ct) =>
        journal.ServiceConfigAsync(
            "reactor", "Reactor", [$"rules/{ruleId}"], removed ? "removed" : "stored",
            AuditPrincipal.ActorString(User) ?? "", AuditOrigin.Api, ct);

    private async Task AuditProvisioningAsync(string leaf, bool provisioned, CancellationToken ct)
    {
        LeafDescriptor? descriptor = LeafCatalog.Default.FirstOrDefault(l => string.Equals(l.Id, leaf, StringComparison.Ordinal));
        string display = descriptor?.DisplayName ?? leaf;
        await journal.ServiceProvisioningAsync(
            provisioned, leaf, display, AuditPrincipal.ActorString(User) ?? "", AuditOrigin.Api, ct);
    }
}
