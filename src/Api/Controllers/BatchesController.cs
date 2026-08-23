using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Commands;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The <c>/batches</c> resource — reading and cancelling the batches this host is running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without these reads the durability would be real but invisible</b>, which is the same defect in a
/// nicer shape: work that survives a closed tab is only useful if the next client can find it. Every
/// row carries its <c>runId</c>, so a client that fans this read across the cluster can reassemble a
/// person's whole cluster-wide run — including one it never dispatched, from a different browser or a
/// different person — by grouping on that id. No node knows about any other; the correlation is the
/// client's, and this endpoint just returns what was stored.
/// </para>
/// <para>
/// Reads are viewer-gated like the rest of the domain; cancel is a mutation and takes operator.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/batches")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class BatchesController(
    BatchStore batches,
    BatchWorker worker,
    JobRegistry jobs,
    StreamHub hub,
    ApiJournal journal,
    ILogger<BatchesController> logger) : ControllerBase
{
    /// <summary>The most batches one listing returns. A client following live work wants the active
    /// set, which is small; the cap only bounds a history read.</summary>
    private const int MaxLimit = 200;

    /// <summary>
    /// This host's batches, newest first. <c>?active=true</c> is what a client polls on reconnect;
    /// <c>?runId=</c> narrows to one cluster-wide run's share of this node.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<BatchList>> GetAll(
        [FromQuery] bool? active, [FromQuery] string? runId, [FromQuery] int? limit, CancellationToken ct)
    {
        int take = Math.Clamp(limit ?? 50, 1, MaxLimit);
        IReadOnlyList<BatchView> data = await batches.ListAsync(active == true, runId, take, ct);
        return new BatchList(data);
    }

    /// <summary>One batch and every member's standing.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<BatchView>> Get(string id, CancellationToken ct)
    {
        BatchView? batch = await batches.GetAsync(id, ct);
        return batch is null ? NotFound() : batch;
    }

    /// <summary>
    /// Cancel a batch's <b>pending</b> members.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A member already running is left alone and named in the response. A kgsm invocation under way is
    /// not interruptible, and a body implying a clean halt would be describing something that did not
    /// happen — an operator who reads "cancelled" and then watches a server stop anyway has been
    /// misled about the one thing they were trying to prevent.
    /// </para>
    /// <para>
    /// <b>Each cancelled member gets its own audit row.</b> Nothing ran, so the engine emits nothing and
    /// this is the only record the fleet keeps of it — and the question it answers ("why did this server
    /// never get its update?") is asked on one server's feed, which a single batch-level row carrying no
    /// <c>serverId</c> would never appear on. <c>meta.batchId</c> is what ties them back together.
    /// </para>
    /// </remarks>
    [HttpDelete("{id}")]
    [Authorize(Policy = AuthPolicy.Operator)]
    public async Task<ActionResult<BatchCancelled>> Cancel(
        string id, [FromQuery] string? origin, CancellationToken ct)
    {
        // A DELETE carries no body, so the driving surface rides the query string — the same vocabulary
        // and the same refusal every other mutation uses. The audit row must not be able to claim an
        // origin nobody can declare, whichever verb produced it.
        if (!TryResolveOrigin(origin, out string resolvedOrigin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        BatchView? existing = await batches.GetAsync(id, ct);
        if (existing is null) return NotFound();

        (IReadOnlyList<CancelledMember> cancelled, IReadOnlyList<string> stillRunning) =
            await batches.CancelPendingAsync(id, ct);

        // Settle the queued jobs too, so the per-server in-flight slot is released and the surfaces that
        // read a server's activeJob stop showing work that will never run.
        string? actor = AuditPrincipal.ActorString(User);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (CancelledMember member in cancelled)
        {
            // Recorded from the store's row rather than from the job, so a member whose job record this
            // process no longer holds — the registry is memory, and a restart empties it — still leaves
            // the record of having been called off.
            await journal.CommandOutcomeAsync(
                ApiJournal.CommandCancelledEvent, member.ServerId, existing.Verb, member.JobId, id,
                error: null, exitCode: null, actor, resolvedOrigin, ct: ct);

            if (member.JobId is null) continue;
            Job? job = jobs.Get(member.JobId);
            if (job is null) continue;
            Job settled = jobs.Update(job with
            {
                State = JobState.Cancelled,
                SettledAt = now,
                Error = "cancelled before it ran",
            });
            hub.Publish(StreamProtocol.JobsTopic, StreamProtocol.JobEntityKey(settled.Id),
                new StreamMessage(StreamProtocol.JobsTopic, StreamProtocol.JobPatch, settled));
        }

        await worker.PublishBatchAsync(id, ct);

        logger.LogInformation(
            "batch {BatchId} cancelled: {Cancelled} pending member(s) stopped, {Running} already running",
            id, cancelled.Count, stillRunning.Count);

        return new BatchCancelled(id, [.. cancelled.Select(m => m.ServerId)], stillRunning);
    }

    private static bool TryResolveOrigin(string? raw, out string origin)
    {
        origin = raw?.Trim().ToLowerInvariant() is { Length: > 0 } o ? o : AuditOrigin.Api;
        return AuditOrigin.IsCallerDeclarable(origin);
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
