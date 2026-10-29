using System.Security.Claims;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Transfers;

/// <summary>
/// The target's half of a transfer: the preflight, the job that pulls the server across, the commit, and
/// the answer the source's recovery asks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The job runs here and the browser only watches it.</b> Once started it needs nobody's session: it
/// redeems the ticket at the source for each step, and the source checks the person's access for its own
/// half each time. Its progress is the <c>transfer</c> block on the job, published on the <c>jobs</c>
/// topic.
/// </para>
/// <para>
/// <b>Every part is compared before going on.</b> Both nodes hash the bytes that crossed; a part whose
/// hashes differ, or whose send the source did not finish, is fetched again from the start, a bounded
/// number of times.
/// </para>
/// <para>
/// <b>The phase is written before each step</b>, so a process that stops mid-transfer leaves a record
/// that says how far it got. Recovery aborts a transfer that stopped before the import finished; one that
/// stopped inside <c>import finish</c> cannot say which instance the engine minted, so it is left running
/// for a person, and the source keeps its lock meanwhile.
/// </para>
/// </remarks>
[PerformedFor(ActionIds.ServerImport)]
public sealed class TransferTarget(
    IServiceScopeFactory scopeFactory,
    TransferStore store,
    TransferPeerClient peers,
    MembersStore members,
    IClusterBus bus,
    ApiOptions options,
    InstanceCache cache,
    JobRegistry jobs,
    StreamHub hub,
    ApiJournal journal,
    DomainPump pump,
    IHostApplicationLifetime lifetime,
    TransferClock time,
    ILogger<TransferTarget> logger)
{
    /// <summary>How many times one part is fetched before the transfer fails.</summary>
    public const int MaxPartAttempts = 3;

    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(500);

    // ---- the person's routes ----------------------------------------------------------------------

    /// <summary>The verdict on a transfer before anything starts. Nothing is written.</summary>
    public async Task<(TransferPreflight? Verdict, TransferProblem? Problem)> PreflightAsync(
        TransferStartRequest body, ClaimsPrincipal user, CancellationToken ct)
    {
        if (Validate(body) is { } invalid) return (null, invalid);

        using IServiceScope scope = scopeFactory.CreateScope();
        if (Engine(scope) is not { } engine)
            return (null, new TransferProblem(503, "unavailable", "the kgsm engine is not provisioned on this host"));

        if (string.Equals(body.SourceMemberId, options.NodeId, StringComparison.Ordinal))
            return (Refused(TransferRefusalCodes.TargetNotNode, "a server cannot be transferred to the node it is on"), null);

        ManifestAnswer answer = await peers.ManifestAsync(body.SourceMemberId!, body.TransferId!, body.Ticket!, ct)
            .ConfigureAwait(false);
        if (answer.Manifest is not { } manifest)
            return (Refused(PeerCode(answer), answer.Message ?? "the source refused"), null);

        ImportVerdict verdict = await Task.Run(() => engine.ImportCheck(manifest, Library(body), Suffix(body.SourceMemberId!),
            AuditPrincipal.ActorString(user), AuditOrigin.Api), ct).ConfigureAwait(false);

        List<TransferRefusal> refusals = [.. verdict.Refusals.Select(r => new TransferRefusal(r.Code, r.Detail))];
        return (new TransferPreflight(verdict.Ok && refusals.Count == 0, refusals,
            TransferSource.Summary(manifest, answer.GameName)), null);
    }

    /// <summary>Starts the transfer job; the job's id, or why it could not start.</summary>
    public async Task<(TransferAccepted? Accepted, TransferProblem? Problem)> StartAsync(
        TransferStartRequest body, ClaimsPrincipal user, CancellationToken ct)
    {
        if (Validate(body) is { } invalid) return (null, invalid);

        using (IServiceScope scope = scopeFactory.CreateScope())
        {
            if (Engine(scope) is null)
                return (null, new TransferProblem(503, "unavailable", "the kgsm engine is not provisioned on this host"));
        }

        if (string.Equals(body.SourceMemberId, options.NodeId, StringComparison.Ordinal))
            return (null, new TransferProblem(422, TransferRefusalCodes.TargetNotNode,
                "a server cannot be transferred to the node it is on"));

        string transferId = body.TransferId!;
        string source = body.SourceMemberId!;
        if (await store.GetAsync(transferId, TransferRole.Target, ct).ConfigureAwait(false) is not null)
            return (null, new TransferProblem(409, TransferRefusalCodes.TransferInProgress,
                $"transfer '{transferId}' has already been started on this node"));

        ManifestAnswer answer = await peers.ManifestAsync(source, transferId, body.Ticket!, ct).ConfigureAwait(false);
        if (answer.Manifest is not { } manifest)
        {
            string code = PeerCode(answer);
            int status = answer.Unreachable ? 502 : answer.Status;
            return (null, new TransferProblem(status, code, answer.Message ?? "the source refused"));
        }

        string jobId = "job_" + Guid.NewGuid().ToString("N")[..8];
        DateTimeOffset now = time.GetUtcNow();
        Job? job = jobs.TryStart(jobId, manifest.Instance.Id, CommandVerb.Transfer, now);
        if (job is null)
            return (null, new TransferProblem(409, TransferRefusalCodes.TransferInProgress,
                $"a job is already in flight for '{manifest.Instance.Id}' on this node"));

        string? actor = AuditPrincipal.ActorString(user);
        bool added = await store.TryAddAsync(new TransferEntity
        {
            TransferId = transferId,
            Role = TransferRole.Target,
            InstanceId = manifest.Instance.Id,
            PeerMemberId = source,
            GameName = answer.GameName,
            State = TransferState.Running,
            Phase = TransferPhase.Preflight,
            Library = Library(body),
            JobId = jobId,
            Actor = actor,
            Origin = AuditOrigin.Api,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct).ConfigureAwait(false);
        if (!added)
        {
            jobs.Update(job with { State = JobState.Cancelled, SettledAt = now });
            return (null, new TransferProblem(409, TransferRefusalCodes.TransferInProgress,
                $"transfer '{transferId}' has already been started on this node"));
        }

        await journal.TransferStartedAsync(transferId, source, manifest.Instance.Id, actor, AuditOrigin.Api, ct)
            .ConfigureAwait(false);

        var run = new Run(transferId, source, body.Ticket!, manifest, Library(body), actor,
            jobs.Update(job with
            {
                State = JobState.Running,
                Transfer = new JobTransfer(TransferPhase.Preflight, null, 0, manifest.Parts.Sum(p => p.Bytes), null),
            }));
        Publish(run.Job);

        _ = Task.Run(() => RunAsync(run, lifetime.ApplicationStopping));
        logger.LogInformation("transfer {TransferId}: started bringing {Instance} over from {Source} (job {JobId})",
            transferId, manifest.Instance.Id, source, jobId);
        return (new TransferAccepted(jobId, transferId), null);
    }

    /// <summary>How a transfer stands here, for the source; null when this node never started it.</summary>
    public async Task<(TransferOutcome? Outcome, TransferProblem? Problem)> OutcomeAsync(
        string callerMemberId, string transferId, CancellationToken ct)
    {
        TransferEntity? r = await store.GetAsync(transferId, TransferRole.Target, ct).ConfigureAwait(false);
        if (r is null)
            return (null, new TransferProblem(404, "not_found", $"no transfer '{transferId}' on this node"));
        if (!string.Equals(callerMemberId, r.PeerMemberId, StringComparison.Ordinal))
            return (null, new TransferProblem(403, TransferRefusalCodes.TicketInvalid,
                "this transfer's source is another member"));

        return (new TransferOutcome(r.State, r.State == TransferState.Committed ? r.TargetInstanceId : null,
            r.State == TransferState.Aborted ? r.Reason : null), null);
    }

    // ---- the job --------------------------------------------------------------------------------------

    private sealed class Run(
        string transferId, string source, string ticket, TransferManifest manifest, string? library, string? actor, Job job)
    {
        public string TransferId { get; } = transferId;
        public string Source { get; } = source;
        public string Ticket { get; } = ticket;
        public TransferManifest Manifest { get; set; } = manifest;
        public string? Library { get; } = library;
        public string? Actor { get; } = actor;
        public Job Job { get; set; } = job;
        public string Phase { get; set; } = TransferPhase.Preflight;
        public string? ImportId { get; set; }
        public long BytesBefore { get; set; }
        public DateTimeOffset LastPublish { get; set; }
    }

    // A refusal the job ends on: the code, the words for the log and the person.
    private sealed class TransferFailed(string code, string detail) : Exception(detail)
    {
        public string Code { get; } = code;
    }

    private async Task RunAsync(Run run, CancellationToken stopping)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            IInstanceService engine = Engine(scope)
                ?? throw new TransferFailed("unavailable", "the kgsm engine is not provisioned on this host");

            // Preflight: the target's own answer, before the source is asked to hold anything.
            ImportVerdict check = await Task.Run(() => engine.ImportCheck(run.Manifest, run.Library,
                Suffix(run.Source), run.Actor, AuditOrigin.Api), stopping).ConfigureAwait(false);
            if (!check.Ok || check.Refusals.Count > 0)
                throw Refusal(check);

            // Lock: the source holds the server stopped from here to the commit, and answers with the
            // manifest as of the lock, which is the one the import uses.
            await EnterAsync(run, TransferPhase.Locking, stopping).ConfigureAwait(false);
            ManifestAnswer locked = await peers.LockAsync(run.Source, run.TransferId, run.Ticket, stopping).ConfigureAwait(false);
            if (locked.Manifest is not { } manifest)
                throw new TransferFailed(PeerCode(locked), locked.Message ?? "the source refused the lock");
            run.Manifest = manifest;

            ImportBeginResult begun = await Task.Run(() => engine.ImportBegin(manifest, run.Library,
                Suffix(run.Source), run.Actor, AuditOrigin.Api), stopping).ConfigureAwait(false);
            if (!begun.Began)
                throw Refusal(begun.Verdict);
            run.ImportId = begun.ImportId;
            await store.UpdateAsync(run.TransferId, TransferRole.Target, r => r.ImportId = begun.ImportId, stopping)
                .ConfigureAwait(false);

            // Parts, each compared before the next.
            await EnterAsync(run, TransferPhase.Transferring, stopping).ConfigureAwait(false);
            foreach (TransferPart part in manifest.Parts)
                await PullPartAsync(engine, run, part, stopping).ConfigureAwait(false);

            // Finish: the engine verifies, installs, starts once and stops.
            await EnterAsync(run, TransferPhase.Importing, stopping).ConfigureAwait(false);
            TransferOutcome<ImportedInstance> finished = await Task.Run(
                () => engine.ImportFinish(run.ImportId!, run.Actor, AuditOrigin.Api), stopping).ConfigureAwait(false);
            if (finished.Value is not { } imported)
                throw new TransferFailed(FinishCode(finished.Result.ExitCode), Detail(finished.Result));

            // Commit: recorded here first, with the instance the engine minted, then told to the source.
            await store.UpdateAsync(run.TransferId, TransferRole.Target, r =>
            {
                r.State = TransferState.Committed;
                r.Phase = TransferPhase.Committing;
                r.TargetInstanceId = imported.Instance;
            }, stopping).ConfigureAwait(false);
            run.Phase = TransferPhase.Committing;
            Progress(run, partId: null, bytesDone: run.BytesBefore, targetInstance: imported.Instance, force: true);
            cache.TryRefresh();
            pump.Nudge();

            await EnqueueCommitAsync(run.TransferId, run.Manifest.Instance.Id, imported.Instance, run.Source, stopping)
                .ConfigureAwait(false);

            await store.UpdateAsync(run.TransferId, TransferRole.Target, r => r.Phase = TransferPhase.Done, stopping)
                .ConfigureAwait(false);
            Settle(run, JobState.Succeeded, TransferPhase.Done, error: null, imported.Instance);
            logger.LogInformation("transfer {TransferId}: {Instance} arrived from {Source} as {Target}",
                run.TransferId, run.Manifest.Instance.Id, run.Source, imported.Instance);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The process is stopping. The record stays running at its phase; recovery ends it on the next
            // start, and the source keeps its lock until then.
            logger.LogWarning("transfer {TransferId}: interrupted by shutdown at {Phase}", run.TransferId, run.Phase);
        }
        catch (Exception ex)
        {
            string code = ex is TransferFailed tf ? tf.Code : TransferRefusalCodes.StartFailed;
            if (ex is not TransferFailed)
                logger.LogError(ex, "transfer {TransferId}: failed at {Phase}", run.TransferId, run.Phase);
            await FailAsync(run, code, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task PullPartAsync(IInstanceService engine, Run run, TransferPart part, CancellationToken ct)
    {
        string detail = "";
        for (int attempt = 1; attempt <= MaxPartAttempts; attempt++)
        {
            (PeerAnswer opened, HttpResponseMessage? response) =
                await peers.OpenPartAsync(run.Source, run.TransferId, run.Ticket, part.Id, ct).ConfigureAwait(false);
            if (response is null)
            {
                // A refusal is the source's word and is final; an unreachable source may come back.
                if (!opened.Unreachable)
                    throw new TransferFailed(PeerCode(opened), opened.Message ?? "the source refused the part");
                detail = opened.Message ?? "the source could not be reached";
                continue;
            }

            string? received;
            StreamResult result;
            using (response)
            {
                try
                {
                    await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var hashing = new HashingStream(body, write: false,
                        progress: bytes => Progress(run, part.Id, run.BytesBefore + bytes, null, force: false));
                    result = await engine.ImportPartAsync(run.ImportId!, part.Id, hashing, run.Actor, AuditOrigin.Api, ct)
                        .ConfigureAwait(false);

                    // The engine stops reading where its format ends; what follows still crossed the wire.
                    await hashing.DrainAsync(ct).ConfigureAwait(false);
                    received = hashing.Hex();
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    detail = ex.Message;
                    logger.LogWarning(ex, "transfer {TransferId}: part {PartId} broke off (attempt {Attempt})",
                        run.TransferId, part.Id, attempt);
                    continue;
                }
            }

            if (!result.IsSuccess)
            {
                if (result.ExitCode != TransferExitCodes.TransferIntegrity)
                    throw new TransferFailed(FinishCode(result.ExitCode), result.Stderr);
                detail = result.Stderr;
                continue;
            }

            (PeerAnswer asked, string? sent) =
                await peers.PartHashAsync(run.Source, run.TransferId, run.Ticket, part.Id, ct).ConfigureAwait(false);
            if (sent is not null && string.Equals(sent, received, StringComparison.Ordinal))
            {
                run.BytesBefore += part.Bytes;
                Progress(run, part.Id, run.BytesBefore, null, force: true);
                return;
            }

            detail = sent is null
                ? $"the source finished no send of part {part.Id} ({asked.Message ?? asked.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
                : $"part {part.Id} arrived with a different hash than it was sent with";
            logger.LogWarning("transfer {TransferId}: {Detail} (attempt {Attempt})", run.TransferId, detail, attempt);
        }

        throw new TransferFailed(TransferRefusalCodes.IntegrityFailed, detail);
    }

    private async Task FailAsync(Run run, string code, string detail)
    {
        string phase = run.Phase;
        logger.LogWarning("transfer {TransferId}: failed at {Phase} ({Code}): {Detail}", run.TransferId, phase, code, detail);

        // Best effort throughout: every step below has a recovery that covers its miss.
        try
        {
            if (run.ImportId is { } importId)
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                if (Engine(scope) is { } engine)
                    await Task.Run(() => engine.ImportAbort(importId, run.Actor, AuditOrigin.Api)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "transfer {TransferId}: the staged import could not be aborted", run.TransferId);
        }

        if (phase != TransferPhase.Preflight)
        {
            PeerAnswer told = await peers.AbortAsync(run.Source, run.TransferId, run.Ticket, code, phase, CancellationToken.None)
                .ConfigureAwait(false);
            if (!told.Ok)
                logger.LogWarning("transfer {TransferId}: the source was not told to abort ({Status} {Code}); its recovery asks",
                    run.TransferId, told.Status, told.Code);
        }

        await store.UpdateAsync(run.TransferId, TransferRole.Target, r =>
        {
            r.State = TransferState.Aborted;
            r.Reason = code;
        }, CancellationToken.None).ConfigureAwait(false);
        await journal.TransferFailedAsync(run.TransferId, null, run.Manifest.Instance.Id, code, phase, run.Actor,
            AuditOrigin.Api, CancellationToken.None).ConfigureAwait(false);
        pump.Nudge();
        Settle(run, JobState.Failed, TransferPhase.Failed, code, targetInstance: null);
    }

    // ---- recovery -------------------------------------------------------------------------------------

    /// <summary>
    /// Ends what a stopped process left behind: a transfer recorded running is aborted (its staged import
    /// removed), a committed one whose commit never reached the outbox is sent again, and a staged import no
    /// record knows is removed. Run once at startup.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IInstanceService? engine = Engine(scope);
        IReadOnlyList<TransferEntity> records = await store.ListAsync(TransferRole.Target, ct).ConfigureAwait(false);

        List<StagedImport>? staged = null;
        if (engine is not null)
        {
            TransferOutcome<List<StagedImport>> listed = await Task.Run(() => engine.ImportList(), ct).ConfigureAwait(false);
            staged = listed.Value;
            if (staged is null)
                logger.LogWarning("transfer recovery: the staged imports could not be listed (exit {ExitCode}): {Stderr}",
                    listed.Result.ExitCode, listed.Result.Stderr);
        }

        foreach (TransferEntity r in records)
        {
            if (r.State == TransferState.Committed && !r.CommitEnqueued && r.TargetInstanceId is { Length: > 0 } target)
            {
                await EnqueueCommitAsync(r.TransferId, r.InstanceId, target, r.PeerMemberId, ct).ConfigureAwait(false);
                continue;
            }

            if (r.State != TransferState.Running) continue;

            if (r.Phase == TransferPhase.Importing && r.ImportId is { } finishing)
            {
                if (staged is null) continue;
                if (!staged.Any(s => s.ImportId == finishing))
                {
                    // The finish consumed the staging, so the engine holds an instance this record cannot
                    // name. The source keeps its lock while this stays running; a person decides.
                    logger.LogError("transfer {TransferId}: the API stopped while import {ImportId} was finishing and the "
                        + "import is no longer staged; the instance it minted is unrecorded. {Source} keeps its lock "
                        + "until a person ends the transfer there", r.TransferId, finishing, r.PeerMemberId);
                    await store.UpdateAsync(r.TransferId, TransferRole.Target, x =>
                    {
                        x.Phase = TransferPhase.Failed;
                        x.Reason = TransferRefusalCodes.TransferInterrupted;
                    }, ct).ConfigureAwait(false);
                    continue;
                }
            }

            if (r.ImportId is { } importId && engine is not null)
                await Task.Run(() => engine.ImportAbort(importId, r.Actor, AuditOrigin.Api), ct).ConfigureAwait(false);

            await store.UpdateAsync(r.TransferId, TransferRole.Target, x =>
            {
                x.State = TransferState.Aborted;
                x.Reason = TransferRefusalCodes.TransferInterrupted;
            }, ct).ConfigureAwait(false);
            await journal.TransferFailedAsync(r.TransferId, null, r.InstanceId, TransferRefusalCodes.TransferInterrupted,
                r.Phase ?? TransferPhase.Preflight, r.Actor, r.Origin, ct).ConfigureAwait(false);
            logger.LogWarning("transfer {TransferId}: aborted on restart at {Phase}; the source's recovery releases its lock",
                r.TransferId, r.Phase);
        }

        if (engine is null || staged is null) return;

        HashSet<string> known = [.. (await store.ListAsync(TransferRole.Target, ct).ConfigureAwait(false))
            .Where(r => r.State == TransferState.Running && r.ImportId is not null)
            .Select(r => r.ImportId!)];
        foreach (StagedImport s in staged.Where(s => !known.Contains(s.ImportId)))
        {
            logger.LogWarning("transfer recovery: staged import {ImportId} ({Instance}) belongs to no running transfer; aborting it",
                s.ImportId, s.Instance);
            await Task.Run(() => engine.ImportAbort(s.ImportId, null, AuditOrigin.System), ct).ConfigureAwait(false);
        }

        pump.Nudge();
    }

    // ---- shared ---------------------------------------------------------------------------------------

    private async Task EnqueueCommitAsync(string transferId, string sourceInstance, string targetInstance, string source,
        CancellationToken ct)
    {
        MemberRow? row = await members.GetByMemberIdAsync(source, ct).ConfigureAwait(false);
        if (row is null || string.IsNullOrWhiteSpace(row.Url))
        {
            logger.LogWarning("transfer {TransferId}: {Source} has no address here; the commit waits for its recovery to ask",
                transferId, source);
            return;
        }

        try
        {
            await bus.EnqueueAsync(TransferMessageTypes.Commit,
                new TransferCommit(transferId, sourceInstance, targetInstance),
                ApiContractsJson.Default.TransferCommit,
                [new ClusterTarget(row.MemberId, row.Url)], ct).ConfigureAwait(false);
            await store.UpdateAsync(transferId, TransferRole.Target, r => r.CommitEnqueued = true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "transfer {TransferId}: the commit could not be queued; it is queued on the next start "
                + "and the source's recovery asks for it meanwhile", transferId);
        }
    }

    private async Task EnterAsync(Run run, string phase, CancellationToken ct)
    {
        run.Phase = phase;
        await store.UpdateAsync(run.TransferId, TransferRole.Target, r => r.Phase = phase, ct).ConfigureAwait(false);
        Progress(run, partId: null, run.BytesBefore, targetInstance: null, force: true);
    }

    private void Progress(Run run, string? partId, long bytesDone, string? targetInstance, bool force)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (!force && now - run.LastPublish < ProgressEvery) return;
        run.LastPublish = now;

        JobTransfer current = run.Job.Transfer!;
        run.Job = jobs.Update(run.Job with
        {
            Transfer = current with
            {
                Phase = run.Phase,
                PartId = run.Phase == TransferPhase.Transferring ? partId : null,
                BytesDone = bytesDone,
                BytesTotal = run.Manifest.Parts.Sum(p => p.Bytes),
                TargetInstanceId = targetInstance ?? current.TargetInstanceId,
            },
        });
        Publish(run.Job);
    }

    private void Settle(Run run, string state, string phase, string? error, string? targetInstance)
    {
        JobTransfer current = run.Job.Transfer!;
        run.Job = jobs.Update(run.Job with
        {
            State = state,
            SettledAt = time.GetUtcNow(),
            Error = error,
            Transfer = current with
            {
                Phase = phase,
                PartId = null,
                TargetInstanceId = targetInstance ?? current.TargetInstanceId,
            },
        });
        Publish(run.Job);
    }

    private void Publish(Job job) =>
        hub.Publish(StreamProtocol.JobsTopic, StreamProtocol.JobEntityKey(job.Id),
            new StreamMessage(StreamProtocol.JobsTopic, StreamProtocol.JobPatch, job), job.ServerId);

    private static TransferProblem? Validate(TransferStartRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.SourceMemberId) || string.IsNullOrWhiteSpace(body.TransferId)
            || string.IsNullOrWhiteSpace(body.Ticket))
        {
            return new TransferProblem(400, "bad_request", "sourceMemberId, transferId and ticket are required");
        }

        return null;
    }

    private static TransferPreflight Refused(string code, string detail) =>
        new(false, [new TransferRefusal(code, detail)], null);

    private static TransferFailed Refusal(ImportVerdict? verdict)
    {
        ImportRefusal? first = verdict?.Refusals.FirstOrDefault();
        return first is null
            ? new TransferFailed(TransferRefusalCodes.StartFailed, "the engine refused the import without saying why")
            : new TransferFailed(first.Code, string.Join("; ", verdict!.Refusals.Select(r => r.Detail)));
    }

    // A source's refusal carries its own code; one it could not be asked for is unreachable.
    private static string PeerCode(PeerAnswer answer) =>
        answer.Code is { Length: > 0 } code ? code
        : answer.Unreachable ? TransferRefusalCodes.MemberUnreachable
        : answer.Status is 401 or 403 ? TransferRefusalCodes.TicketInvalid
        : TransferRefusalCodes.MemberUnreachable;

    private static string FinishCode(int exitCode) => exitCode switch
    {
        TransferExitCodes.TransferIntegrity or TransferExitCodes.ImportNotFound => TransferRefusalCodes.IntegrityFailed,
        TransferExitCodes.PortConflict => TransferRefusalCodes.PortConflict,
        TransferExitCodes.RequirementMissing => TransferRefusalCodes.RequirementMissing,
        TransferExitCodes.EngineVersionMismatch => TransferRefusalCodes.EngineVersionMismatch,
        TransferExitCodes.InstanceLocked => TransferRefusalCodes.InstanceLocked,
        EngineExit.InsufficientDisk => TransferRefusalCodes.InsufficientDisk,
        _ => TransferRefusalCodes.StartFailed,
    };

    private static string Detail(KgsmResult result) =>
        string.IsNullOrWhiteSpace(result.Stderr) ? $"the engine failed (exit {result.ExitCode})" : result.Stderr.Trim();

    private static string? Library(TransferStartRequest body) =>
        string.IsNullOrWhiteSpace(body.Library) ? null : body.Library.Trim();

    // The label a travelling custom blueprint takes when the target holds a different one of its name: the
    // source member's id, so `7dtd` from `walter` arrives as `7dtd-walter`.
    private static string Suffix(string sourceMemberId) => sourceMemberId;

    private static IInstanceService? Engine(IServiceScope scope) =>
        scope.ServiceProvider.GetService<IInstanceService>();
}
