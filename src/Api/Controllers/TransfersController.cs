using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;
using TheKrystalShip.Api.Services.Transfers;
using TheKrystalShip.KGSM;
using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Moving a server to another node — the source's person-facing routes, under
/// <c>/api/v1/servers/{id}/transfers</c>, gated on <c>kgsm:server.export</c> at the server.
/// </summary>
/// <remarks>
/// A refusal answers the standard error envelope with the refusal code as <c>error.code</c>
/// (<see cref="TransferRefusalCodes"/>). Both routes are published in the operations manifest with their
/// gate, which is how the panel decides whether to offer a transfer.
/// </remarks>
[ApiController]
[Route("api/v1/servers/{id}/transfers")]
public sealed class ServerTransfersController(TransferSource source) : ControllerBase
{
    /// <summary>
    /// <c>POST /servers/{id}/transfers</c> <c>{ targetMemberId }</c> — mints a transfer of a stopped server
    /// and the ticket the target redeems. <c>201</c> <see cref="TransferTicket"/>; <c>404</c> unknown server;
    /// <c>409 instance_running</c>, <c>instance_locked</c>, <c>transfer_in_progress</c>;
    /// <c>422 target_not_node</c>.
    /// </summary>
    [HttpPost]
    [RequiresAction(ActionIds.ServerExport)]
    public async Task<IActionResult> Create(string id, [FromBody] TransferCreateRequest? body, CancellationToken ct)
    {
        (TransferTicket? ticket, TransferProblem? problem) =
            await source.CreateAsync(id, body?.TargetMemberId, User, ct).ConfigureAwait(false);
        return ticket is not null ? StatusCode(StatusCodes.Status201Created, ticket) : Problem(problem!);
    }

    /// <summary>
    /// <c>DELETE /servers/{id}/transfers/current[?force=true]</c> — ends the transfer holding the server and
    /// releases its lock, after asking the target. <c>204</c>; <c>404</c> when none is under way;
    /// <c>409 transfer_committed</c> when the target already holds the server; <c>409 transfer_in_progress</c>
    /// while the target still runs it and <c>502 member_unreachable</c> when it cannot be asked, both unless
    /// <c>force</c>, which releases the lock as the person's own decision.
    /// </summary>
    [HttpDelete("current")]
    [RequiresAction(ActionIds.ServerExport)]
    public async Task<IActionResult> Cancel(string id, [FromQuery] bool force, CancellationToken ct)
    {
        TransferProblem? problem = await source.CancelAsync(id, force, AuditPrincipal.ActorString(User), ct)
            .ConfigureAwait(false);
        return problem is null ? NoContent() : Problem(problem);
    }

    private ObjectResult Problem(TransferProblem p) =>
        StatusCode(p.Status, new ErrorEnvelope(new ErrorBody(p.Code, p.Message)));
}

/// <summary>
/// Bringing a server over from another node — the target's person-facing routes, gated on
/// <c>kgsm:server.import</c> at this node.
/// </summary>
[ApiController]
[Route("api/v1/transfers")]
public sealed class TransfersController(TransferTarget target) : ControllerBase
{
    /// <summary>
    /// <c>POST /transfers/preflight</c> — every reason this node would refuse the transfer, asked of the
    /// source's manifest and this node's engine. Takes no lock and does not use up the ticket. <c>200</c>
    /// <see cref="TransferPreflight"/>; a refusal is a verdict, not an error.
    /// </summary>
    [HttpPost("preflight")]
    [RequiresAction(ActionIds.ServerImport)]
    public async Task<IActionResult> Preflight([FromBody] TransferStartRequest? body, CancellationToken ct)
    {
        (TransferPreflight? verdict, TransferProblem? problem) =
            await target.PreflightAsync(body ?? new TransferStartRequest(null, null, null), User, ct).ConfigureAwait(false);
        return verdict is not null ? Ok(verdict) : Problem(problem!);
    }

    /// <summary>
    /// <c>POST /transfers</c> — starts the transfer as a job on this node. <c>202</c>
    /// <see cref="TransferAccepted"/>; the source's refusal answers with its code; <c>409
    /// transfer_in_progress</c> for a transfer already started here; <c>502 member_unreachable</c>.
    /// </summary>
    [HttpPost]
    [RequiresAction(ActionIds.ServerImport)]
    public async Task<IActionResult> Start([FromBody] TransferStartRequest? body, CancellationToken ct)
    {
        (TransferAccepted? accepted, TransferProblem? problem) =
            await target.StartAsync(body ?? new TransferStartRequest(null, null, null), User, ct).ConfigureAwait(false);
        return accepted is not null ? StatusCode(StatusCodes.Status202Accepted, accepted) : Problem(problem!);
    }

    private ObjectResult Problem(TransferProblem p) =>
        StatusCode(p.Status, new ErrorEnvelope(new ErrorBody(p.Code, p.Message)));
}

/// <summary>
/// What the two nodes of a transfer ask each other, under <c>/api/v1/members/self/transfers</c>.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous to the user pipeline and authenticated here with the caller's cluster service token, like
/// every <c>self/*</c> route (<see cref="ClusterCaller"/>). On the source the caller must be the
/// transfer's target and present the ticket in the <c>KGSM-Transfer-Ticket</c> header; on the target the
/// caller must be the transfer's source.
/// </para>
/// <para>
/// <b>Each action carries its own <see cref="AllowAnonymousAttribute"/></b>, as on the members routes.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/members/self/transfers/{transferId}")]
public sealed class MemberTransfersController(
    TransferSource source,
    TransferTarget target,
    IClusterTokenService clusterTokens,
    IClusterMemberGate memberGate) : ControllerBase
{
    /// <summary>On the target: <c>GET</c> — how the transfer stands here, for the source's recovery.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Outcome(string transferId, CancellationToken ct)
    {
        (string? caller, int status, ErrorEnvelope? refusal) = await Caller().ConfigureAwait(false);
        if (caller is null) return StatusCode(status, refusal);

        (TransferOutcome? outcome, TransferProblem? problem) =
            await target.OutcomeAsync(caller, transferId, ct).ConfigureAwait(false);
        return outcome is not null ? Ok(outcome) : Problem(problem!);
    }

    /// <summary>On the source: <c>GET manifest</c> — the manifest without a lock, for the preflight.</summary>
    [HttpGet("manifest")]
    [AllowAnonymous]
    public async Task<IActionResult> Manifest(string transferId, CancellationToken ct)
    {
        (Data.TransferEntity? r, IActionResult? refused) = await RedeemAsync(transferId, redeem: true, ct).ConfigureAwait(false);
        if (r is null) return refused!;

        (TransferManifest? manifest, TransferProblem? problem) = await source.ManifestAsync(r, ct).ConfigureAwait(false);
        return manifest is null ? Problem(problem!) : ManifestResult(manifest, r.GameName);
    }

    /// <summary>On the source: <c>POST lock</c> — takes the lock; the manifest as of the lock.</summary>
    [HttpPost("lock")]
    [AllowAnonymous]
    public async Task<IActionResult> Lock(string transferId, CancellationToken ct)
    {
        (Data.TransferEntity? r, IActionResult? refused) = await RedeemAsync(transferId, redeem: true, ct).ConfigureAwait(false);
        if (r is null) return refused!;

        (TransferManifest? manifest, TransferProblem? problem) = await source.LockAsync(r, ct).ConfigureAwait(false);
        return manifest is null ? Problem(problem!) : ManifestResult(manifest, r.GameName);
    }

    /// <summary>
    /// On the source: <c>GET parts/{partId}</c> — the part's bytes as the engine writes them,
    /// <c>application/octet-stream</c>. <c>409 transfer_not_locked</c> before the lock. A send the engine
    /// fails after the bytes began is cut off, so the target sees a broken stream rather than a short one.
    /// </summary>
    [HttpGet("parts/{partId}")]
    [AllowAnonymous]
    public async Task Part(string transferId, string partId, CancellationToken ct)
    {
        (Data.TransferEntity? r, IActionResult? refused) = await RedeemAsync(transferId, redeem: true, ct).ConfigureAwait(false);
        if (r is null)
        {
            await refused!.ExecuteResultAsync(ControllerContext).ConfigureAwait(false);
            return;
        }

        if (r.State != TransferState.Locked)
        {
            await Problem(new TransferProblem(409, TransferRefusalCodes.TransferNotLocked,
                "the source has not taken the lock for this transfer")).ExecuteResultAsync(ControllerContext).ConfigureAwait(false);
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/octet-stream";
        StreamResult? result = await source.StreamPartAsync(r, partId, Response.Body, ct).ConfigureAwait(false);
        if (result is { IsSuccess: true }) return;

        if (!Response.HasStarted)
        {
            TransferProblem problem = result is null
                ? new TransferProblem(503, "unavailable", "the kgsm engine is not provisioned on this host")
                : TransferSource.EngineProblem(new KgsmResult(result.ExitCode, "", result.Stderr), r.InstanceId);
            await Problem(problem).ExecuteResultAsync(ControllerContext).ConfigureAwait(false);
            return;
        }

        HttpContext.Abort();
    }

    /// <summary>
    /// On the source: <c>GET parts/{partId}/sha256</c> — <see cref="TransferPartHash"/> of the last
    /// complete send of the part; <c>404</c> when none completed.
    /// </summary>
    [HttpGet("parts/{partId}/sha256")]
    [AllowAnonymous]
    public async Task<IActionResult> PartHash(string transferId, string partId, CancellationToken ct)
    {
        (Data.TransferEntity? r, IActionResult? refused) = await RedeemAsync(transferId, redeem: false, ct).ConfigureAwait(false);
        if (r is null) return refused!;

        return source.PartHash(transferId, partId) is { } hex
            ? Ok(new TransferPartHash(hex))
            : Problem(new TransferProblem(404, "not_found", $"no complete send of part '{partId}'"));
    }

    /// <summary>On the source: <c>POST abort</c> — releases the lock, recording why; idempotent.</summary>
    [HttpPost("abort")]
    [AllowAnonymous]
    public async Task<IActionResult> Abort(string transferId, [FromBody] TransferAbortRequest? body, CancellationToken ct)
    {
        (Data.TransferEntity? r, IActionResult? refused) = await RedeemAsync(transferId, redeem: false, ct).ConfigureAwait(false);
        if (r is null) return refused!;

        TransferProblem? problem = await source.AbortAsync(r, body?.Reason, body?.Phase, ct).ConfigureAwait(false);
        return problem is null ? NoContent() : Problem(problem);
    }

    private async Task<(Data.TransferEntity? Record, IActionResult? Refused)> RedeemAsync(
        string transferId, bool redeem, CancellationToken ct)
    {
        (string? caller, int status, ErrorEnvelope? refusal) = await Caller().ConfigureAwait(false);
        if (caller is null) return (null, StatusCode(status, refusal));

        string? ticket = Request.Headers[TransferHeaders.Ticket];
        (Data.TransferEntity? record, TransferProblem? problem) =
            await source.AuthorizeAsync(caller, transferId, ticket, redeem, ct).ConfigureAwait(false);
        return record is null ? (null, Problem(problem!)) : (record, null);
    }

    private Task<(string? MemberId, int Status, ErrorEnvelope? Refusal)> Caller() =>
        ClusterCaller.AuthenticateAsync(Request, clusterTokens, memberGate);

    // The engine's manifest, written as the engine's own serialization, with the source's game name beside it.
    private ContentResult ManifestResult(TransferManifest manifest, string? gameName)
    {
        if (gameName is { Length: > 0 })
            Response.Headers[TransferHeaders.GameName] = gameName;
        return Content(System.Text.Json.JsonSerializer.Serialize(manifest, KgsmJsonContext.Default.TransferManifest),
            "application/json");
    }

    private ObjectResult Problem(TransferProblem p) =>
        StatusCode(p.Status, new ErrorEnvelope(new ErrorBody(p.Code, p.Message)));
}
