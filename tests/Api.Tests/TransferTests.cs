using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Transfers;
using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Dns.Member;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// A server moving between two nodes, proven on two real API pipelines in one process
/// (<see cref="TransferPair"/>): every member call, every part stream and the commit on the bus cross the
/// HTTP pipeline, and only the engine is faked.
/// </summary>
public sealed class TransferTests
{
    private const string Instance = TransferPair.Instance;

    // ── the whole flow ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transfer_MovesTheServer_AndBothNodesRecordTheirHalf()
    {
        await using TransferPair pair = await TransferPair.StartAsync();

        TransferTicket ticket = await MintAsync(pair);
        Assert.Equal(TransferPair.SourceId, ticket.SourceMemberId);
        Assert.Equal(Instance, ticket.Summary.Instance);
        Assert.Equal(256 * 1024 + 64 * 1024, ticket.Summary.Bytes);

        // The source's server carries the transfer from the ticket on.
        ServerTransfer? held = (await ServerAsync(pair.Source, Instance))?.Transfer;
        Assert.NotNull(held);
        Assert.Equal(TransferDirection.Out, held!.Direction);
        Assert.Equal(TransferState.Ticketed, held.State);
        Assert.Equal(TransferPair.TargetId, held.PeerMemberId);

        // A preflight takes nothing and does not use the ticket up.
        TransferPreflight verdict = await PreflightAsync(pair, ticket);
        Assert.True(verdict.Ok);
        Assert.Empty(verdict.Refusals);
        Assert.Equal(Instance, verdict.Summary!.Instance);
        Assert.Null(pair.SourceEngine.LockOf(Instance));

        TransferAccepted accepted = await StartAsync(pair, ticket);
        Job job = await SettledJobAsync(pair, accepted.JobId);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(CommandVerb.Transfer, job.Verb);
        Assert.Equal(Instance, job.ServerId);
        Assert.Equal(TransferPhase.Done, job.Transfer!.Phase);
        Assert.Equal(Instance + "-arrived", job.Transfer.TargetInstanceId);
        Assert.Equal(job.Transfer.BytesTotal, job.Transfer.BytesDone);

        // The commit reaches the source over the bus and removes its copy.
        await TransferPair.Until(async () =>
            (await pair.SourceStore.GetAsync(ticket.TransferId, TransferRole.Source))?.State == TransferState.Committed,
            "the source applies the commit");
        Assert.False(pair.SourceEngine.Has(Instance));
        Assert.Equal(1, pair.SourceEngine.Count("commit"));

        TransferEntity target = (await pair.TargetStore.GetAsync(ticket.TransferId, TransferRole.Target))!;
        Assert.Equal(TransferState.Committed, target.State);
        Assert.Equal(Instance + "-arrived", target.TargetInstanceId);
        Assert.True(target.CommitEnqueued);

        // Each node wrote its own half to its own journal.
        Assert.Contains(ApiJournal.TransferStartedEvent, JournalText(pair.Target), StringComparison.Ordinal);
        Assert.Contains(ApiJournal.TransferCompletedEvent, JournalText(pair.Source), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiJournal.TransferFailedEvent, JournalText(pair.Source), StringComparison.Ordinal);

        // Single-transfer: the ticket starts nothing more on either side.
        HttpResponseMessage again = await PostStartAsync(pair, ticket);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(TransferRefusalCodes.TransferInProgress, await CodeAsync(again));

        TransferPreflight after = await PreflightAsync(pair, ticket);
        Assert.False(after.Ok);
        Assert.Equal(TransferRefusalCodes.TransferCommitted, after.Refusals.Single().Code);
    }

    [Fact]
    public async Task Transfer_JobProgress_ReportsEveryPhaseOnTheJobsRegistry()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        TransferTicket ticket = await MintAsync(pair);

        TransferAccepted accepted = await StartAsync(pair, ticket);
        Job job = await SettledJobAsync(pair, accepted.JobId);

        // GET /jobs/{id} reads the same record the stream publishes.
        HttpResponseMessage read = await pair.Client(pair.Target).GetAsync($"/api/v1/jobs/{accepted.JobId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        string json = await read.Content.ReadAsStringAsync();
        Assert.Contains("\"transfer\":{\"phase\":\"done\"", json, StringComparison.Ordinal);
        Assert.Contains("\"targetInstanceId\":\"terraria-arrived\"", json, StringComparison.Ordinal);
        Assert.Equal(JobState.Succeeded, job.State);
    }

    // ── the mint ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mint_RunningServer_409InstanceRunning()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        HttpResponseMessage resp = await PostMintAsync(pair, "running-1", TransferPair.TargetId);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal(TransferRefusalCodes.InstanceRunning, await CodeAsync(resp));
    }

    [Fact]
    public async Task Mint_TargetNotANodeOfTheCluster_422TargetNotNode()
    {
        await using TransferPair pair = await TransferPair.StartAsync();

        HttpResponseMessage unknown = await PostMintAsync(pair, Instance, "node-nobody");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal(TransferRefusalCodes.TargetNotNode, await CodeAsync(unknown));

        HttpResponseMessage self = await PostMintAsync(pair, Instance, TransferPair.SourceId);
        Assert.Equal(TransferRefusalCodes.TargetNotNode, await CodeAsync(self));
    }

    [Fact]
    public async Task Mint_WhileLocked_409TransferInProgress_ButATicketAloneIsSuperseded()
    {
        await using TransferPair pair = await TransferPair.StartAsync();

        TransferTicket first = await MintAsync(pair);
        TransferTicket second = await MintAsync(pair);
        Assert.Equal(TransferState.Aborted, (await pair.SourceStore.GetAsync(first.TransferId, TransferRole.Source))!.State);

        pair.SourceEngine.Lock(Instance, second.TransferId);
        await pair.SourceStore.UpdateAsync(second.TransferId, TransferRole.Source, r => r.State = TransferState.Locked);

        HttpResponseMessage third = await PostMintAsync(pair, Instance, TransferPair.TargetId);
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal(TransferRefusalCodes.TransferInProgress, await CodeAsync(third));
    }

    [Fact]
    public async Task Mint_IsGatedOnTheExportAction()
    {
        await using TransferPair pair = await TransferPair.StartAsync();

        HttpResponseMessage anonymous = await pair.Client(pair.Source, persona: null).PostAsJsonAsync(
            $"/api/v1/servers/{Instance}/transfers", new TransferCreateRequest(TransferPair.TargetId));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        HttpResponseMessage runner = await pair.Client(pair.Source, Persona.Runner).PostAsJsonAsync(
            $"/api/v1/servers/{Instance}/transfers", new TransferCreateRequest(TransferPair.TargetId));
        Assert.Equal(HttpStatusCode.Forbidden, runner.StatusCode);

        HttpResponseMessage preflight = await pair.Client(pair.Target, Persona.Runner).PostAsJsonAsync(
            "/api/v1/transfers/preflight", new TransferStartRequest(TransferPair.SourceId, "x", "y"));
        Assert.Equal(HttpStatusCode.Forbidden, preflight.StatusCode);
    }

    [Fact]
    public async Task OperationsManifest_PublishesTheThreePersonFacingRoutes()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string ops = await pair.Client(pair.Source).GetStringAsync("/api/v1/operations");

        Assert.Contains("/servers/{id}/transfers", ops, StringComparison.Ordinal);
        Assert.Contains("/servers/{id}/transfers/current", ops, StringComparison.Ordinal);
        Assert.Contains("/transfers", ops, StringComparison.Ordinal);
        Assert.Contains("kgsm:server.export", ops, StringComparison.Ordinal);
        Assert.Contains("kgsm:server.import", ops, StringComparison.Ordinal);
    }

    // ── the ticket ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ticket_Wrong_IsTicketInvalid_OnPreflightAndStart()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        TransferTicket ticket = await MintAsync(pair) with { Ticket = new string('0', 64) };

        TransferPreflight verdict = await PreflightAsync(pair, ticket);
        Assert.Equal(TransferRefusalCodes.TicketInvalid, verdict.Refusals.Single().Code);

        HttpResponseMessage start = await PostStartAsync(pair, ticket);
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        Assert.Equal(TransferRefusalCodes.TicketInvalid, await CodeAsync(start));
    }

    [Fact]
    public async Task Ticket_ExpiresTenMinutesAfterMinting_UnlessLocked()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        TransferTicket ticket = await MintAsync(pair);

        pair.SourceTime.Advance(TimeSpan.FromMinutes(9));
        Assert.True((await PreflightAsync(pair, ticket)).Ok);

        pair.SourceTime.Advance(TimeSpan.FromMinutes(2));
        TransferPreflight late = await PreflightAsync(pair, ticket);
        Assert.Equal(TransferRefusalCodes.TicketInvalid, late.Refusals.Single().Code);

        // The source's recovery drops a ticket that expired without a lock.
        await pair.SourceSide.RecoverAsync(CancellationToken.None);
        Assert.Equal(TransferState.Aborted, (await pair.SourceStore.GetAsync(ticket.TransferId, TransferRole.Source))!.State);
    }

    [Fact]
    public async Task Ticket_AfterTheLock_DoesNotExpire()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        TransferTicket ticket = await MintAsync(pair);

        HttpResponseMessage locked = await MemberCallAsync(pair, HttpMethod.Post, ticket, "lock");
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Equal(ticket.TransferId, pair.SourceEngine.LockOf(Instance));

        pair.SourceTime.Advance(TimeSpan.FromHours(3));
        HttpResponseMessage part = await MemberCallAsync(pair, HttpMethod.Get, ticket, "parts/tree");
        Assert.Equal(HttpStatusCode.OK, part.StatusCode);
        await part.Content.ReadAsByteArrayAsync();

        HttpResponseMessage hash = await MemberCallAsync(pair, HttpMethod.Get, ticket, "parts/tree/sha256");
        TransferPartHash? sent = await hash.Content.ReadFromJsonAsync<TransferPartHash>();
        Assert.Equal(pair.SourceEngine.PartHash("tree"), sent!.Sha256);
    }

    // ── the member routes ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MemberRoutes_RefuseACallerThatIsNotTheTransfersOtherNode()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        TransferTicket ticket = await MintAsync(pair);

        // A token the source minted itself: valid in the cluster, but not the transfer's target.
        string selfToken = pair.Source.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
        HttpResponseMessage wrongIssuer = await RawMemberCallAsync(pair.Source, selfToken, HttpMethod.Get,
            ticket.TransferId, ticket.Ticket, "manifest");
        Assert.Equal(HttpStatusCode.Forbidden, wrongIssuer.StatusCode);
        Assert.Equal(TransferRefusalCodes.TicketInvalid, await CodeAsync(wrongIssuer));

        HttpResponseMessage noToken = await pair.Source.CreateClient().GetAsync(
            $"/api/v1/members/self/transfers/{ticket.TransferId}/manifest");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        // And on the target, the outcome answers only the transfer's source.
        TransferAccepted accepted = await StartAsync(pair, ticket);
        await SettledJobAsync(pair, accepted.JobId);
        string targetSelf = pair.Target.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
        HttpResponseMessage outcome = await RawMemberCallAsync(pair.Target, targetSelf, HttpMethod.Get,
            ticket.TransferId, null, null);
        Assert.Equal(HttpStatusCode.Forbidden, outcome.StatusCode);
    }

    [Fact]
    public async Task Part_BeforeTheLock_409TransferNotLocked()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        TransferTicket ticket = await MintAsync(pair);

        HttpResponseMessage part = await MemberCallAsync(pair, HttpMethod.Get, ticket, "parts/tree");
        Assert.Equal(HttpStatusCode.Conflict, part.StatusCode);
        Assert.Equal(TransferRefusalCodes.TransferNotLocked, await CodeAsync(part));

        HttpResponseMessage hash = await MemberCallAsync(pair, HttpMethod.Get, ticket, "parts/tree/sha256");
        Assert.Equal(HttpStatusCode.NotFound, hash.StatusCode);
    }

    [Fact]
    public async Task Abort_ReleasesTheLock_AndIsIdempotent()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        TransferTicket ticket = await MintAsync(pair);
        await MemberCallAsync(pair, HttpMethod.Post, ticket, "lock");
        Assert.NotNull(pair.SourceEngine.LockOf(Instance));

        for (int i = 0; i < 2; i++)
        {
            HttpResponseMessage abort = await MemberCallAsync(pair, HttpMethod.Post, ticket, "abort",
                JsonSerializer.Serialize(new TransferAbortRequest(TransferRefusalCodes.PortConflict, TransferPhase.Locking),
                    ApiContractsJson.Default.TransferAbortRequest));
            Assert.Equal(HttpStatusCode.NoContent, abort.StatusCode);
        }

        Assert.Null(pair.SourceEngine.LockOf(Instance));
        TransferEntity r = (await pair.SourceStore.GetAsync(ticket.TransferId, TransferRole.Source))!;
        Assert.Equal(TransferState.Aborted, r.State);
        Assert.Equal(TransferRefusalCodes.PortConflict, r.Reason);
        Assert.Equal(1, Occurrences(JournalText(pair.Source), ApiJournal.TransferFailedEvent));
        Assert.Null((await ServerAsync(pair.Source, Instance))?.Transfer);
    }

    // ── refusals and failures in the job ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Preflight_ListsEveryEngineRefusal()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        pair.TargetEngine.Refusals.Add(new ImportRefusal { ExitCode = 60, Code = "engine_version_mismatch", Detail = "3.18.0-rc32 here" });
        pair.TargetEngine.Refusals.Add(new ImportRefusal { ExitCode = 62, Code = "port_conflict", Detail = "7777/tcp" });
        pair.TargetEngine.Refusals.Add(new ImportRefusal { ExitCode = 61, Code = "requirement_missing", Detail = "steamcmd" });
        pair.TargetEngine.Refusals.Add(new ImportRefusal { ExitCode = 56, Code = "insufficient_disk", Detail = "short" });

        TransferPreflight verdict = await PreflightAsync(pair, await MintAsync(pair));

        Assert.False(verdict.Ok);
        Assert.Equal(
            [TransferRefusalCodes.EngineVersionMismatch, TransferRefusalCodes.PortConflict,
             TransferRefusalCodes.RequirementMissing, TransferRefusalCodes.InsufficientDisk],
            verdict.Refusals.Select(r => r.Code).ToArray());
        Assert.NotNull(verdict.Summary);
    }

    [Fact]
    public async Task Job_RefusedAtPreflight_FailsWithTheCode_AndTakesNoLock()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        pair.TargetEngine.Refusals.Add(new ImportRefusal { ExitCode = 62, Code = "port_conflict", Detail = "7777/tcp" });
        TransferTicket ticket = await MintAsync(pair);

        Job job = await SettledJobAsync(pair, (await StartAsync(pair, ticket)).JobId);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(TransferRefusalCodes.PortConflict, job.Error);
        Assert.Equal(TransferPhase.Failed, job.Transfer!.Phase);
        Assert.Null(pair.SourceEngine.LockOf(Instance));
        Assert.Contains(ApiJournal.TransferFailedEvent, JournalText(pair.Target), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Job_APartDamagedOnce_IsFetchedAgain_AndTheTransferSucceeds()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        pair.Router.TamperParts = 1;

        Job job = await SettledJobAsync(pair, (await StartAsync(pair, await MintAsync(pair))).JobId);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(3, pair.TargetEngine.Count("import-part"));
    }

    [Fact]
    public async Task Job_APartThatNeverArrivesIntact_FailsIntegrity_AndBothSidesLetGo()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        pair.Router.TamperParts = TransferTarget.MaxPartAttempts;
        TransferTicket ticket = await MintAsync(pair);

        Job job = await SettledJobAsync(pair, (await StartAsync(pair, ticket)).JobId);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(TransferRefusalCodes.IntegrityFailed, job.Error);
        Assert.Single(pair.TargetEngine.Aborted);
        Assert.Null(pair.SourceEngine.LockOf(Instance));
        Assert.True(pair.SourceEngine.Has(Instance));
        TransferEntity source = (await pair.SourceStore.GetAsync(ticket.TransferId, TransferRole.Source))!;
        Assert.Equal(TransferState.Aborted, source.State);
        Assert.Equal(TransferRefusalCodes.IntegrityFailed, source.Reason);
        Assert.Equal(TransferState.Aborted,
            (await pair.TargetStore.GetAsync(ticket.TransferId, TransferRole.Target))!.State);
    }

    [Fact]
    public async Task Job_FinishFails_StartFailed_AndTheSourceKeepsItsServer()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        pair.TargetEngine.FinishExit = 1;
        TransferTicket ticket = await MintAsync(pair);

        Job job = await SettledJobAsync(pair, (await StartAsync(pair, ticket)).JobId);

        Assert.Equal(TransferRefusalCodes.StartFailed, job.Error);
        Assert.Null(pair.SourceEngine.LockOf(Instance));
        Assert.Equal(0, pair.SourceEngine.Count("commit"));
    }

    [Fact]
    public async Task Start_SourceUnreachable_502MemberUnreachable()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        TransferTicket ticket = await MintAsync(pair);
        pair.Router.Down[TransferPair.SourceId] = true;

        HttpResponseMessage start = await PostStartAsync(pair, ticket);
        Assert.Equal(HttpStatusCode.BadGateway, start.StatusCode);
        Assert.Equal(TransferRefusalCodes.MemberUnreachable, await CodeAsync(start));

        TransferPreflight verdict = await PreflightAsync(pair, ticket);
        Assert.Equal(TransferRefusalCodes.MemberUnreachable, verdict.Refusals.Single().Code);
        Assert.Null(verdict.Summary);
    }

    // ── the commit handler ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Commit_Locked_IsApplied_AndARedeliveryChangesNothing()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);

        var commit = new TransferCommit(id, Instance, "terraria-new");
        await pair.SourceSide.ApplyCommitAsync(commit, TransferPair.TargetId, CancellationToken.None);
        await pair.SourceSide.ApplyCommitAsync(commit, TransferPair.TargetId, CancellationToken.None);

        TransferEntity r = (await pair.SourceStore.GetAsync(id, TransferRole.Source))!;
        Assert.Equal(TransferState.Committed, r.State);
        Assert.Equal("terraria-new", r.TargetInstanceId);
        Assert.Equal(1, pair.SourceEngine.Count("commit"));
        Assert.False(pair.SourceEngine.Has(Instance));
        Assert.Equal(1, Occurrences(JournalText(pair.Source), ApiJournal.TransferCompletedEvent));
    }

    [Fact]
    public async Task Commit_TransientEngineFailure_Throws_SoTheBusRetries()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);
        pair.SourceEngine.CommitFailures = 1;

        var commit = new TransferCommit(id, Instance, "terraria-new");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.SourceSide.ApplyCommitAsync(commit, TransferPair.TargetId, CancellationToken.None));
        Assert.Equal(TransferState.Locked, (await pair.SourceStore.GetAsync(id, TransferRole.Source))!.State);

        await pair.SourceSide.ApplyCommitAsync(commit, TransferPair.TargetId, CancellationToken.None);
        Assert.Equal(TransferState.Committed, (await pair.SourceStore.GetAsync(id, TransferRole.Source))!.State);
    }

    [Fact]
    public async Task Commit_ForAnAbortedTransfer_RecordsTheConflict_Once_AndRemovesNothing()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Aborted);

        var commit = new TransferCommit(id, Instance, "terraria-new");
        await pair.SourceSide.ApplyCommitAsync(commit, TransferPair.TargetId, CancellationToken.None);
        await pair.SourceSide.ApplyCommitAsync(commit, TransferPair.TargetId, CancellationToken.None);

        TransferEntity r = (await pair.SourceStore.GetAsync(id, TransferRole.Source))!;
        Assert.Equal(TransferState.Aborted, r.State);
        Assert.Equal(TransferRefusalCodes.TransferCommitted, r.Reason);
        Assert.True(pair.SourceEngine.Has(Instance));
        Assert.Equal(0, pair.SourceEngine.Count("commit"));
        Assert.Equal(1, Occurrences(JournalText(pair.Source), ApiJournal.TransferFailedEvent));
    }

    [Fact]
    public async Task Commit_ForAnUnknownTransfer_RecordsTheConflict()
    {
        await using TransferPair pair = await TransferPair.StartAsync();

        await pair.SourceSide.ApplyCommitAsync(new TransferCommit("xfer_unknown", Instance, "terraria-new"),
            TransferPair.TargetId, CancellationToken.None);

        TransferEntity r = (await pair.SourceStore.GetAsync("xfer_unknown", TransferRole.Source))!;
        Assert.Equal(TransferState.Aborted, r.State);
        Assert.Equal(TransferRefusalCodes.TransferCommitted, r.Reason);
        Assert.True(pair.SourceEngine.Has(Instance));
    }

    [Fact]
    public async Task Commit_FromAMemberThatIsNotTheTarget_IsIgnored()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);

        await pair.SourceSide.ApplyCommitAsync(new TransferCommit(id, Instance, "x"), "node-other", CancellationToken.None);

        Assert.Equal(TransferState.Locked, (await pair.SourceStore.GetAsync(id, TransferRole.Source))!.State);
        Assert.True(pair.SourceEngine.Has(Instance));
    }

    // ── recovery ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SourceRecovery_TargetUnreachable_KeepsTheLock()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);
        pair.Router.Down[TransferPair.TargetId] = true;

        await pair.SourceSide.RecoverAsync(CancellationToken.None);

        Assert.Equal(TransferState.Locked, (await pair.SourceStore.GetAsync(id, TransferRole.Source))!.State);
        Assert.Equal(id, pair.SourceEngine.LockOf(Instance));
        Assert.Equal(0, pair.SourceEngine.Count("unlock"));
    }

    [Fact]
    public async Task SourceRecovery_TargetCommitted_AppliesTheCommit()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);
        await SeedTargetAsync(pair, id, TransferState.Committed, targetInstance: "terraria-there");

        await pair.SourceSide.RecoverAsync(CancellationToken.None);

        TransferEntity r = (await pair.SourceStore.GetAsync(id, TransferRole.Source))!;
        Assert.Equal(TransferState.Committed, r.State);
        Assert.Equal("terraria-there", r.TargetInstanceId);
        Assert.False(pair.SourceEngine.Has(Instance));
    }

    [Fact]
    public async Task SourceRecovery_TargetAbortedOrUnaware_ReleasesTheLock()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string aborted = await SeedSourceAsync(pair, TransferState.Locked);
        await SeedTargetAsync(pair, aborted, TransferState.Aborted, reason: TransferRefusalCodes.StartFailed);

        await pair.SourceSide.RecoverAsync(CancellationToken.None);

        TransferEntity r = (await pair.SourceStore.GetAsync(aborted, TransferRole.Source))!;
        Assert.Equal(TransferState.Aborted, r.State);
        Assert.Equal(TransferRefusalCodes.StartFailed, r.Reason);
        Assert.Null(pair.SourceEngine.LockOf(Instance));

        // A transfer the target never heard of holds nothing there either.
        string unaware = await SeedSourceAsync(pair, TransferState.Locked);
        await pair.SourceSide.RecoverAsync(CancellationToken.None);
        Assert.Equal(TransferState.Aborted, (await pair.SourceStore.GetAsync(unaware, TransferRole.Source))!.State);
        Assert.Null(pair.SourceEngine.LockOf(Instance));
    }

    [Fact]
    public async Task SourceRecovery_TargetStillRunning_KeepsTheLock()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);
        await SeedTargetAsync(pair, id, TransferState.Running);

        await pair.SourceSide.RecoverAsync(CancellationToken.None);

        Assert.Equal(TransferState.Locked, (await pair.SourceStore.GetAsync(id, TransferRole.Source))!.State);
        Assert.Equal(id, pair.SourceEngine.LockOf(Instance));
    }

    [Fact]
    public async Task TargetRecovery_RunningMidPart_AbortsTheImport_AndTheSourceThenLetsGo()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);
        pair.TargetEngine.Stage("imp-000000000042");
        await SeedTargetAsync(pair, id, TransferState.Running, importId: "imp-000000000042", phase: TransferPhase.Transferring);

        await pair.TargetSide.RecoverAsync(CancellationToken.None);

        TransferEntity t = (await pair.TargetStore.GetAsync(id, TransferRole.Target))!;
        Assert.Equal(TransferState.Aborted, t.State);
        Assert.Equal(TransferRefusalCodes.TransferInterrupted, t.Reason);
        Assert.Contains("imp-000000000042", pair.TargetEngine.Aborted);

        await pair.SourceSide.RecoverAsync(CancellationToken.None);
        Assert.Null(pair.SourceEngine.LockOf(Instance));
        Assert.Equal(TransferRefusalCodes.TransferInterrupted,
            (await pair.SourceStore.GetAsync(id, TransferRole.Source))!.Reason);
    }

    [Fact]
    public async Task TargetRecovery_CommittedButNeverEnqueued_SendsTheCommit_AndTheSourceAppliesIt()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);
        await SeedTargetAsync(pair, id, TransferState.Committed, targetInstance: "terraria-there");

        await pair.TargetSide.RecoverAsync(CancellationToken.None);

        Assert.True((await pair.TargetStore.GetAsync(id, TransferRole.Target))!.CommitEnqueued);
        await TransferPair.Until(async () =>
            (await pair.SourceStore.GetAsync(id, TransferRole.Source))?.State == TransferState.Committed,
            "the bus delivers the commit");
        Assert.False(pair.SourceEngine.Has(Instance));
    }

    [Fact]
    public async Task TargetRecovery_AStagedImportNoRecordKnows_IsAborted()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        pair.TargetEngine.Stage("imp-000000000099");

        await pair.TargetSide.RecoverAsync(CancellationToken.None);

        Assert.False(pair.TargetEngine.IsStaged("imp-000000000099"));
    }

    [Fact]
    public async Task TargetRecovery_StoppedInsideTheFinish_LeavesItRunning_SoTheSourceKeepsItsLock()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);
        await SeedTargetAsync(pair, id, TransferState.Running, importId: "imp-000000000007", phase: TransferPhase.Importing);

        await pair.TargetSide.RecoverAsync(CancellationToken.None);
        await pair.SourceSide.RecoverAsync(CancellationToken.None);

        Assert.Equal(TransferState.Running, (await pair.TargetStore.GetAsync(id, TransferRole.Target))!.State);
        Assert.Equal(id, pair.SourceEngine.LockOf(Instance));
    }

    // ── the way out ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_TargetUnreachable_502_UnlessForced()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);
        pair.Router.Down[TransferPair.TargetId] = true;

        HttpResponseMessage refused = await pair.Client(pair.Source).DeleteAsync($"/api/v1/servers/{Instance}/transfers/current");
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Equal(TransferRefusalCodes.MemberUnreachable, await CodeAsync(refused));
        Assert.Equal(id, pair.SourceEngine.LockOf(Instance));

        HttpResponseMessage forced = await pair.Client(pair.Source).DeleteAsync($"/api/v1/servers/{Instance}/transfers/current?force=true");
        Assert.Equal(HttpStatusCode.NoContent, forced.StatusCode);
        Assert.Null(pair.SourceEngine.LockOf(Instance));
        Assert.Equal(TransferRefusalCodes.TransferCancelled, (await pair.SourceStore.GetAsync(id, TransferRole.Source))!.Reason);
    }

    [Fact]
    public async Task Cancel_TargetCommitted_409TransferCommitted()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        string id = await SeedSourceAsync(pair, TransferState.Locked);
        await SeedTargetAsync(pair, id, TransferState.Committed, targetInstance: "terraria-there");

        HttpResponseMessage resp = await pair.Client(pair.Source).DeleteAsync($"/api/v1/servers/{Instance}/transfers/current?force=true");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal(TransferRefusalCodes.TransferCommitted, await CodeAsync(resp));
        Assert.Equal(id, pair.SourceEngine.LockOf(Instance));
    }

    [Fact]
    public async Task Cancel_TargetUnaware_ReleasesTheLock_AndClearsTheServersTransfer()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        await SeedSourceAsync(pair, TransferState.Locked);
        Assert.Equal(TransferState.Locked, (await ServerAsync(pair.Source, Instance))?.Transfer?.State);

        HttpResponseMessage resp = await pair.Client(pair.Source).DeleteAsync($"/api/v1/servers/{Instance}/transfers/current");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Null(pair.SourceEngine.LockOf(Instance));
        Assert.Null((await ServerAsync(pair.Source, Instance))?.Transfer);

        HttpResponseMessage none = await pair.Client(pair.Source).DeleteAsync($"/api/v1/servers/{Instance}/transfers/current");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    // ── the game name ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DnsInstances_ACommittedArrival_InheritsTheSourcesName_ForAsLongAsTheRecordExists()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        pair.TargetEngine.AddInstance("terraria-there");
        pair.TargetEngine.AddInstance("factorio");
        await SeedTargetAsync(pair, "xfer_dns", TransferState.Committed, targetInstance: "terraria-there", gameName: "terraria.play.example");
        await RefreshRosterAsync(pair.Target);

        IReadOnlyList<DnsInstance> told = (await DnsSource(pair.Target).ReadAsync(CancellationToken.None))!;

        DnsInstance arrived = told.Single(i => i.Instance == "terraria-there");
        Assert.Equal(new DnsInheritance(TransferPair.SourceId, Instance, "terraria.play.example"), arrived.Inherits);
        Assert.Null(told.Single(i => i.Instance == "factorio").Inherits);
    }

    [Fact]
    public async Task DnsInstances_WhileAnImportIsFinishing_SendNothing()
    {
        await using TransferPair pair = await TransferPair.StartAsync();
        await SeedTargetAsync(pair, "xfer_finishing", TransferState.Running, importId: "imp-1", phase: TransferPhase.Importing);

        Assert.Null(await DnsSource(pair.Target).ReadAsync(CancellationToken.None));

        await pair.TargetStore.UpdateAsync("xfer_finishing", TransferRole.Target, r =>
        {
            r.State = TransferState.Committed;
            r.TargetInstanceId = "terraria";
        });
        Assert.NotNull(await DnsSource(pair.Target).ReadAsync(CancellationToken.None));
    }

    // ── the audit rows ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuditRows_NameTheOtherNodeByItsLabel_AndCarryItsId()
    {
        AuditWrite started = AuditMapping.FromTransferStartedEvent(new TransferStartedEventData
        {
            TransferId = "xfer_1", SourceMember = "hotrod", SourceInstance = "terraria",
        }, "host", id => id == "hotrod" ? "walter" : null);
        Assert.Equal("server.transfer.started", started.Action);
        Assert.Contains("walter", started.Summary, StringComparison.Ordinal);
        Assert.Equal("hotrod", started.Meta!["sourceMember"]);
        Assert.Null(started.ServerId);

        AuditWrite completed = AuditMapping.FromTransferCompletedEvent(new TransferCompletedEventData
        {
            TransferId = "xfer_1", InstanceName = "terraria", TargetMember = "jessie", TargetInstance = "terraria-2",
        }, "host");
        Assert.Equal("server.transfer.completed", completed.Action);
        Assert.Contains("jessie", completed.Summary, StringComparison.Ordinal);
        Assert.Equal("terraria", completed.ServerId);

        AuditWrite failedOnTarget = AuditMapping.FromTransferFailedEvent(new TransferFailedEventData
        {
            TransferId = "xfer_1", SourceInstance = "terraria", Reason = "port_conflict", Phase = "preflight",
        }, "host");
        Assert.Equal("server.transfer.failed", failedOnTarget.Action);
        Assert.Null(failedOnTarget.ServerId);
        Assert.Equal("port_conflict", failedOnTarget.Meta!["reason"]);
        Assert.Equal("preflight", failedOnTarget.Meta["phase"]);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> PostMintAsync(TransferPair pair, string instance, string target) =>
        await pair.Client(pair.Source).PostAsJsonAsync($"/api/v1/servers/{instance}/transfers", new TransferCreateRequest(target));

    private static async Task<TransferTicket> MintAsync(TransferPair pair)
    {
        HttpResponseMessage resp = await PostMintAsync(pair, Instance, TransferPair.TargetId);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<TransferTicket>())!;
    }

    private static async Task<TransferPreflight> PreflightAsync(TransferPair pair, TransferTicket t)
    {
        HttpResponseMessage resp = await pair.Client(pair.Target).PostAsJsonAsync("/api/v1/transfers/preflight",
            new TransferStartRequest(t.SourceMemberId, t.TransferId, t.Ticket));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<TransferPreflight>())!;
    }

    private static Task<HttpResponseMessage> PostStartAsync(TransferPair pair, TransferTicket t) =>
        pair.Client(pair.Target).PostAsJsonAsync("/api/v1/transfers",
            new TransferStartRequest(t.SourceMemberId, t.TransferId, t.Ticket));

    private static async Task<TransferAccepted> StartAsync(TransferPair pair, TransferTicket t)
    {
        HttpResponseMessage resp = await PostStartAsync(pair, t);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<TransferAccepted>())!;
    }

    private static async Task<Job> SettledJobAsync(TransferPair pair, string jobId)
    {
        Services.Commands.JobRegistry jobs = pair.Target.Services.GetRequiredService<Services.Commands.JobRegistry>();
        await TransferPair.Until(() => Task.FromResult(jobs.Get(jobId) is { } j && JobState.IsTerminal(j.State)),
            "the transfer job settles");
        return jobs.Get(jobId)!;
    }

    private static async Task<Server?> ServerAsync(ClusterNodeFactory node, string id)
    {
        await RefreshRosterAsync(node);
        List<Server>? servers = await new TransferPairClient(node).Get<List<Server>>("/api/v1/servers");
        return servers?.FirstOrDefault(s => s.Id == id);
    }

    private static async Task RefreshRosterAsync(ClusterNodeFactory node)
    {
        Services.Aggregation.InstanceCache cache = node.Services.GetRequiredService<Services.Aggregation.InstanceCache>();
        await TransferPair.Until(() => Task.FromResult(cache.TryRefresh()), "a roster refresh starts");
        await TransferPair.Until(() => Task.FromResult(cache.TryRefresh()), "the roster refresh finishes");
    }

    private static Task<HttpResponseMessage> MemberCallAsync(
        TransferPair pair, HttpMethod method, TransferTicket t, string path, string? json = null)
    {
        string token = pair.Target.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
        return RawMemberCallAsync(pair.Source, token, method, t.TransferId, t.Ticket, path, json);
    }

    private static async Task<HttpResponseMessage> RawMemberCallAsync(
        ClusterNodeFactory node, string token, HttpMethod method, string transferId, string? ticket, string? path,
        string? json = null)
    {
        using var request = new HttpRequestMessage(method,
            $"/api/v1/members/self/transfers/{transferId}" + (path is null ? "" : "/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ticket is not null) request.Headers.Add(TransferHeaders.Ticket, ticket);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await node.CreateClient().SendAsync(request);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ErrorEnvelope>())?.Error.Code;

    private static async Task<string> SeedSourceAsync(TransferPair pair, string state)
    {
        string id = "xfer_" + Guid.NewGuid().ToString("N")[..12];
        if (state == TransferState.Locked) pair.SourceEngine.Lock(Instance, id);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await pair.SourceStore.TryAddAsync(new TransferEntity
        {
            TransferId = id, Role = TransferRole.Source, InstanceId = Instance, PeerMemberId = TransferPair.TargetId,
            State = state, TicketHash = new string('a', 64), TicketExpiresAt = now.AddMinutes(10),
            CreatedAt = now, UpdatedAt = now,
        });
        return id;
    }

    private static async Task SeedTargetAsync(
        TransferPair pair, string id, string state, string? targetInstance = null, string? importId = null,
        string? phase = null, string? reason = null, string? gameName = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await pair.TargetStore.TryAddAsync(new TransferEntity
        {
            TransferId = id, Role = TransferRole.Target, InstanceId = Instance, PeerMemberId = TransferPair.SourceId,
            State = state, TargetInstanceId = targetInstance, ImportId = importId, Phase = phase, Reason = reason,
            GameName = gameName, CreatedAt = now, UpdatedAt = now,
        });
    }

    private static IDnsInstanceSource DnsSource(ClusterNodeFactory node) =>
        node.Services.GetRequiredService<IDnsInstanceSource>();

    // Every line this node's API wrote to its own journal.
    private static string JournalText(ClusterNodeFactory node)
    {
        string dir = node.Services.GetRequiredService<ApiOptions>().EventJournalDir;
        return Directory.Exists(dir)
            ? string.Concat(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText))
            : "";
    }

    private static int Occurrences(string text, string type) =>
        text.Split('\n').Count(line => line.Contains($"\"{type}\"", StringComparison.Ordinal));

    private sealed class TransferPairClient(ClusterNodeFactory node)
    {
        public async Task<T?> Get<T>(string path)
        {
            HttpClient c = node.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", node.AccessToken(Persona.Owner));
            return await c.GetFromJsonAsync<T>(path);
        }
    }
}
