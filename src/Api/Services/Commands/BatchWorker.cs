using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// Runs accepted batches: picks up pending members, holds the host's concurrency window, and settles
/// each one against what actually happened.
/// </summary>
/// <remarks>
/// <para>
/// <b>The window is here, not in a client.</b> Nothing else limits how much this host runs at once —
/// <see cref="CommandRunner"/> fires each job on its own task and <see cref="JobRegistry"/> caps
/// in-flight work per <em>server</em>, not per host. Holding it in the worker also makes it a property
/// of the machine: two operators batching at the same time share one window, which no client-side
/// pacer could arrange.
/// </para>
/// <para>
/// <b>What is paced is the work, not the invocation.</b> A kgsm call costs a fraction of a second and
/// four in parallel cost no more than one; a stop drains and saves a game world, and an update runs
/// steamcmd against one disk and one uplink. That is what the numbers below are about.
/// </para>
/// </remarks>
public sealed class BatchWorker(
    BatchStore store,
    JobRegistry registry,
    ICommandExecutor runner,
    StreamHub hub,
    ILogger<BatchWorker> logger) : BackgroundService
{
    /// <summary>
    /// How many members of the same verb class may run at once.
    /// </summary>
    /// <remarks>
    /// Update is the expensive verb — steamcmd against one disk and one uplink — so it runs two at a
    /// time rather than four. Two rather than one because a six-server patch run should not take six
    /// times as long as a single one; the disk contention that sets that ceiling is not measured, so
    /// measure a real update batch before moving it.
    /// </remarks>
    internal static int WindowFor(string verb) => verb == CommandVerb.Update ? 2 : 4;

    /// <summary>How long the loop sleeps when nothing woke it. The pump is signalled on every accepted
    /// batch, so this only bounds how long a member freed by an outside change waits to be noticed.</summary>
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _signal = new(0);

    // Every dispatch still in flight, mapped to the verb whose window it occupies. Only the pump loop
    // touches this — dispatch adds, the next pass removes what completed — so it needs no lock, and the
    // window is always recounted from it rather than tracked as a separate number that could drift.
    private readonly Dictionary<Task, string> _running = [];

    /// <summary>Wake the pump now. Called when a batch is accepted so its first members start
    /// immediately rather than at the next idle poll.</summary>
    public void Signal()
    {
        // A pump that is already awake needs no second wake-up; the release is capped at one pending
        // permit so a burst of accepts cannot queue a burst of empty passes.
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    /// <summary>
    /// Announce where a batch now stands on the <c>batches</c> topic.
    /// </summary>
    /// <remarks>
    /// The roll-up is published rather than left for a client to assemble from member job frames,
    /// because a client that joined halfway through has not seen the earlier ones. Every path that
    /// changes a batch calls this — accept, each member's settle, cancel — so one frame shape carries
    /// every reason a batch can move.
    /// </remarks>
    public async Task PublishBatchAsync(string batchId, CancellationToken ct = default)
    {
        try
        {
            BatchView? view = await store.GetAsync(batchId, ct).ConfigureAwait(false);
            if (view is null) return;
            hub.Publish(StreamProtocol.BatchesTopic, StreamProtocol.BatchEntityKey(batchId),
                new StreamMessage(StreamProtocol.BatchesTopic, StreamProtocol.BatchPatch, view));
        }
        catch (Exception ex)
        {
            // Telling somebody about the batch is not the batch. A failed publish leaves the REST read
            // as the way to find out, and must never fail the work itself.
            logger.LogDebug(ex, "publishing batch {BatchId} failed", batchId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileAfterRestartAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PumpAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A pump that throws must not take the worker down — every batch after it would sit
                // pending forever with nothing to say why.
                logger.LogError(ex, "batch pump failed; retrying at the next tick");
            }

            try { await _signal.WaitAsync(IdlePoll, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Settle what an ended process left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A member left <c>pending</c> is simply resumed — nothing ran, and its job needs re-registering
    /// because the registry is memory that this start does not have. The job keeps its original id so
    /// a client that saw it queued before the restart sees the same job afterwards.
    /// </para>
    /// <para>
    /// A member left <c>running</c> is a different thing entirely. Its job record is gone and the kgsm
    /// invocation was a child of the process that died, so <b>nobody observed the outcome</b>. It
    /// settles <see cref="BatchMemberState.Unknown"/>: calling it failed would claim a result that was
    /// never seen, and re-running it could start a server somebody deliberately stopped or restart one
    /// mid-update. The engine remains the only authority on what happened, and the audit log — written
    /// from the engine's own events — is where the answer lives if there is one.
    /// </para>
    /// </remarks>
    private async Task ReconcileAfterRestartAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<PendingMember> orphaned = await store.GetOrphanedRunningAsync(ct).ConfigureAwait(false);
            foreach (PendingMember m in orphaned)
            {
                await store.SetMemberStateAsync(m.BatchId, m.ServerId, BatchMemberState.Unknown,
                    "the API restarted while this was running; the engine never reported the outcome", ct)
                    .ConfigureAwait(false);
                await PublishBatchAsync(m.BatchId, ct).ConfigureAwait(false);
                logger.LogWarning(
                    "batch {BatchId} member {ServerId} ({Verb}) settled unknown: interrupted by a restart",
                    m.BatchId, m.ServerId, m.Verb);
            }

            if (orphaned.Count > 0)
                logger.LogWarning("{Count} batch member(s) settled unknown after a restart", orphaned.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "batch restart reconciliation failed; pending work still resumes");
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        // Drop finished dispatches and release their window slots before deciding what else fits.
        Harvest();

        IReadOnlyList<PendingMember> pending = await store.GetPendingAsync(ct).ConfigureAwait(false);
        if (pending.Count == 0) return;

        foreach (PendingMember m in pending)
        {
            if (ct.IsCancellationRequested) return;

            // Full for this verb class — but a later member of a different verb may still fit, so keep
            // walking rather than stopping at the first blocked one.
            if (InFlightFor(m.Verb) >= WindowFor(m.Verb)) continue;

            Job? job = ClaimJob(m);
            if (job is null) continue;

            await store.SetMemberStateAsync(m.BatchId, m.ServerId, BatchMemberState.Running, null, ct)
                .ConfigureAwait(false);
            await PublishBatchAsync(m.BatchId, ct).ConfigureAwait(false);

            _running[RunMemberAsync(m, job)] = m.Verb;
        }
    }

    private int InFlightFor(string verb) => _running.Values.Count(v => v == verb);

    // Take the per-server in-flight slot for a member and hand back the job to run.
    //
    // The job normally already exists: it was created queued when the batch was accepted. After a
    // restart the registry is empty, so it is re-registered here under its original id. Either way the
    // claim can fail — the engine may have started something on that instance in the meantime — and a
    // member that cannot be claimed simply stays pending for the next pass.
    private Job? ClaimJob(PendingMember m)
    {
        if (m.JobId is null) return null;

        Job? existing = registry.Get(m.JobId);
        if (existing is not null) return existing;

        Job? revived = registry.TryStart(m.JobId, m.ServerId, m.Verb, m.BatchCreatedAt);
        if (revived is null)
        {
            logger.LogDebug(
                "batch {BatchId} member {ServerId} waits: the server is busy with another command",
                m.BatchId, m.ServerId);
            return null;
        }

        Job queued = registry.Update(revived with { BatchId = m.BatchId, QueuedPosition = m.Position });
        Publish(queued);
        return queued;
    }

    private async Task RunMemberAsync(PendingMember m, Job job)
    {
        string state = BatchMemberState.Failed;
        string? error = null;
        try
        {
            int? exitCode = await runner.RunAsync(job, m.Actor, m.Origin, m.Force).ConfigureAwait(false);

            // The runner settled the job; read its own verdict rather than inferring one from the fact
            // that the call returned.
            Job? settled = registry.Get(job.Id);
            if (settled is null)
            {
                state = BatchMemberState.Unknown;
                error = "the job record was lost before its outcome could be read";
            }
            else if (settled.State == JobState.Succeeded)
            {
                state = BatchMemberState.Succeeded;
            }
            else
            {
                // A capacity refusal is a refusal, not a failure. Nothing is wrong with the server — the
                // node was full — so recording it as failed both reads as a fault in the instance and
                // invites a retry that will refuse identically. Keyed on the exit code the engine
                // defines rather than on its message, which is prose and free to be reworded.
                state = exitCode == EngineExit.InsufficientMemory
                    ? BatchMemberState.Refused
                    : BatchMemberState.Failed;
                error = settled.Error;
            }
        }
        catch (Exception ex)
        {
            state = BatchMemberState.Failed;
            error = ex.Message;
            logger.LogError(ex, "batch {BatchId} member {ServerId} ({Verb}) threw", m.BatchId, m.ServerId, m.Verb);
        }
        finally
        {
            try
            {
                await store.SetMemberStateAsync(m.BatchId, m.ServerId, state, error).ConfigureAwait(false);
                await PublishBatchAsync(m.BatchId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The member ran; failing to record how is a reporting failure, not a reason to lose the
                // window slot or stall the batch.
                logger.LogError(ex, "recording batch {BatchId} member {ServerId} outcome failed",
                    m.BatchId, m.ServerId);
            }
            // Something finished, so something else may now fit.
            Signal();
        }
    }

    // Drop every dispatch that has completed, freeing its share of the window. Each is released on its
    // own, so three of four finishing lets three more in rather than waiting for the fourth. Faulted
    // tasks are already handled inside RunMemberAsync's finally, so nothing here observes an exception.
    private void Harvest()
    {
        foreach (Task t in _running.Keys.Where(t => t.IsCompleted).ToList())
            _running.Remove(t);
    }

    private void Publish(Job job) =>
        hub.Publish(StreamProtocol.JobsTopic, StreamProtocol.JobEntityKey(job.Id),
            new StreamMessage(StreamProtocol.JobsTopic, StreamProtocol.JobPatch, job));
}
