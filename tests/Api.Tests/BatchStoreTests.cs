using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Commands;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <see cref="BatchStore"/> — the durable half of a batch, against a real temp-file SQLite
/// <see cref="AppDbContext"/> (the <c>PeersTableGateTests</c> <c>NewStore</c> pattern), no
/// <c>WebApplicationFactory</c>. What matters here is the bookkeeping a batch depends on to survive a
/// restart: which members the worker may pick up, in what order, when the batch is finished, and what a
/// cancel does and does not stop.
/// </summary>
public sealed class BatchStoreTests
{
    [Fact]
    public async Task PendingMembers_AreOrderedByBatchAge_ThenPosition()
    {
        BatchStore store = NewStore();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        // The younger batch is created first so the ordering under test cannot pass by insertion order.
        await store.CreateAsync(Batch("b_new", CommandVerb.Stop, t0.AddMinutes(5)),
            [Member("b_new", "srv-x", 1), Member("b_new", "srv-y", 2)]);
        await store.CreateAsync(Batch("b_old", CommandVerb.Stop, t0),
            [Member("b_old", "srv-a", 1), Member("b_old", "srv-b", 2)]);

        IReadOnlyList<PendingMember> pending = await store.GetPendingAsync();

        // A batch already waiting is never overtaken by one accepted after it, and within a batch the
        // members run in the order the client asked.
        Assert.Equal(["srv-a", "srv-b", "srv-x", "srv-y"], pending.Select(p => p.ServerId));
    }

    [Fact]
    public async Task PendingMembers_CarryTheBatchProvenance()
    {
        BatchStore store = NewStore();
        BatchEntity batch = Batch("b1", CommandVerb.Update, DateTimeOffset.UtcNow);
        batch.Actor = "discord:haru";
        batch.Origin = "ui";
        await store.CreateAsync(batch, [Member("b1", "srv-a", 1)]);

        PendingMember m = Assert.Single(await store.GetPendingAsync());

        // The worker runs the member with the batch's actor and origin, so each engine call — and the
        // audit row written from its echo — carries the same provenance a hand-issued command would.
        Assert.Equal(CommandVerb.Update, m.Verb);
        Assert.Equal("discord:haru", m.Actor);
        Assert.Equal("ui", m.Origin);
        Assert.Equal("job_srv-a", m.JobId);
    }

    [Fact]
    public async Task RefusedMembers_AreNeverPendingWork()
    {
        BatchStore store = NewStore();
        await store.CreateAsync(Batch("b1", CommandVerb.Start, DateTimeOffset.UtcNow), [
            Member("b1", "srv-a", 1),
            new BatchMemberEntity
            {
                BatchId = "b1", ServerId = "srv-refused", State = BatchMemberState.Refused,
                Error = "server is already running", SettledAt = DateTimeOffset.UtcNow,
            },
        ]);

        PendingMember only = Assert.Single(await store.GetPendingAsync());
        Assert.Equal("srv-a", only.ServerId);
    }

    [Fact]
    public async Task Batch_Settles_OnlyWhenEveryMemberIsTerminal()
    {
        BatchStore store = NewStore();
        await store.CreateAsync(Batch("b1", CommandVerb.Stop, DateTimeOffset.UtcNow),
            [Member("b1", "srv-a", 1), Member("b1", "srv-b", 2)]);

        await store.SetMemberStateAsync("b1", "srv-a", BatchMemberState.Succeeded);
        BatchView? mid = await store.GetAsync("b1");
        Assert.Equal(BatchState.Active, mid!.State);
        Assert.Null(mid.SettledAt);

        await store.SetMemberStateAsync("b1", "srv-b", BatchMemberState.Failed, "engine said no");
        BatchView? done = await store.GetAsync("b1");
        Assert.Equal(BatchState.Settled, done!.State);
        Assert.NotNull(done.SettledAt);
        Assert.Equal(1, done.Counts.Succeeded);
        Assert.Equal(1, done.Counts.Failed);
        // The failure detail is the engine's own words, kept verbatim rather than summarised.
        Assert.Equal("engine said no", done.Members.Single(m => m.ServerId == "srv-b").Error);
    }

    [Fact]
    public async Task Batch_WithOnlyRefusedMembers_IsSettledWithNoPendingWork()
    {
        BatchStore store = NewStore();
        BatchEntity batch = Batch("b1", CommandVerb.Start, DateTimeOffset.UtcNow);
        batch.State = BatchState.Settled;
        batch.SettledAt = DateTimeOffset.UtcNow;
        await store.CreateAsync(batch, [
            new BatchMemberEntity
            {
                BatchId = "b1", ServerId = "srv-a", State = BatchMemberState.Refused,
                Error = "server is already running", SettledAt = DateTimeOffset.UtcNow,
            },
        ]);

        Assert.Empty(await store.GetPendingAsync());
        BatchView? view = await store.GetAsync("b1");
        Assert.Equal(BatchState.Settled, view!.State);
    }

    [Fact]
    public async Task Cancel_StopsPending_LeavesRunning_AndNamesBoth()
    {
        BatchStore store = NewStore();
        await store.CreateAsync(Batch("b1", CommandVerb.Update, DateTimeOffset.UtcNow),
            [Member("b1", "srv-a", 1), Member("b1", "srv-b", 2), Member("b1", "srv-c", 3)]);
        await store.SetMemberStateAsync("b1", "srv-a", BatchMemberState.Running);

        (IReadOnlyList<string> cancelled, IReadOnlyList<string> stillRunning, IReadOnlyList<string> jobIds) =
            await store.CancelPendingAsync("b1");

        // A kgsm invocation under way is not interruptible, so the running member is reported as such
        // rather than implying a clean halt.
        Assert.Equal(["srv-b", "srv-c"], cancelled);
        Assert.Equal(["srv-a"], stillRunning);
        Assert.Equal(["job_srv-b", "job_srv-c"], jobIds);

        // The batch is still active: something is genuinely still running in it.
        BatchView? view = await store.GetAsync("b1");
        Assert.Equal(BatchState.Active, view!.State);
        Assert.Equal(2, view.Counts.Cancelled);
        Assert.Equal(1, view.Counts.Running);
        Assert.Empty(await store.GetPendingAsync());
    }

    [Fact]
    public async Task OrphanedRunning_IsWhatARestartHasToSettle()
    {
        BatchStore store = NewStore();
        await store.CreateAsync(Batch("b1", CommandVerb.Stop, DateTimeOffset.UtcNow),
            [Member("b1", "srv-a", 1), Member("b1", "srv-b", 2)]);
        await store.SetMemberStateAsync("b1", "srv-a", BatchMemberState.Running);

        // Exactly the members whose outcome nobody observed — a pending member simply resumes and is
        // not in this set.
        PendingMember orphan = Assert.Single(await store.GetOrphanedRunningAsync());
        Assert.Equal("srv-a", orphan.ServerId);
    }

    [Fact]
    public async Task List_ActiveOnly_AndByRunId()
    {
        BatchStore store = NewStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        BatchEntity a = Batch("b_active", CommandVerb.Stop, now);
        a.RunId = "run_1";
        await store.CreateAsync(a, [Member("b_active", "srv-a", 1)]);

        BatchEntity b = Batch("b_settled", CommandVerb.Stop, now.AddMinutes(-1));
        b.RunId = "run_1";
        await store.CreateAsync(b, [Member("b_settled", "srv-b", 1)]);
        await store.SetMemberStateAsync("b_settled", "srv-b", BatchMemberState.Succeeded);

        BatchEntity c = Batch("b_other", CommandVerb.Stop, now.AddMinutes(-2));
        c.RunId = "run_2";
        await store.CreateAsync(c, [Member("b_other", "srv-c", 1)]);
        await store.SetMemberStateAsync("b_other", "srv-c", BatchMemberState.Succeeded);

        // What a client polls on reconnect: only batches with work left in them, whichever run they
        // belong to.
        Assert.Equal(["b_active"], (await store.ListAsync(activeOnly: true, null, 50)).Select(v => v.Id));

        // A run's share of THIS node — what a client fans across the cluster and groups to reassemble
        // one person's cluster-wide action.
        IReadOnlyList<BatchView> run1 = await store.ListAsync(activeOnly: false, "run_1", 50);
        Assert.Equal(["b_active", "b_settled"], run1.Select(v => v.Id));
    }

    private static BatchEntity Batch(string id, string verb, DateTimeOffset createdAt) => new()
    {
        Id = id,
        Verb = verb,
        State = BatchState.Active,
        Origin = "ui",
        CreatedAt = createdAt,
    };

    private static BatchMemberEntity Member(string batchId, string serverId, int position) => new()
    {
        BatchId = batchId,
        ServerId = serverId,
        State = BatchMemberState.Pending,
        JobId = "job_" + serverId,
        Position = position,
    };

    private static BatchStore NewStore()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"batchstoretest-{Guid.NewGuid():N}.db");
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .BuildServiceProvider();
        using (IServiceScope scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        return new BatchStore(provider.GetRequiredService<IServiceScopeFactory>());
    }
}
