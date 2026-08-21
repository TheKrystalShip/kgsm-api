using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
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
    /// A member already running is left alone and named in the response. A kgsm invocation under way is
    /// not interruptible, and a body implying a clean halt would be describing something that did not
    /// happen — an operator who reads "cancelled" and then watches a server stop anyway has been
    /// misled about the one thing they were trying to prevent.
    /// </remarks>
    [HttpDelete("{id}")]
    [Authorize(Policy = AuthPolicy.Operator)]
    public async Task<ActionResult<BatchCancelled>> Cancel(string id, CancellationToken ct)
    {
        BatchView? existing = await batches.GetAsync(id, ct);
        if (existing is null) return NotFound();

        (IReadOnlyList<string> cancelled, IReadOnlyList<string> stillRunning, IReadOnlyList<string> jobIds) =
            await batches.CancelPendingAsync(id, ct);

        // Settle the queued jobs too, so the per-server in-flight slot is released and the surfaces that
        // read a server's activeJob stop showing work that will never run.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string jobId in jobIds)
        {
            Job? job = jobs.Get(jobId);
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

        return new BatchCancelled(id, cancelled, stillRunning);
    }
}
