using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Commands;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <see cref="BatchWorker"/>'s concurrency window and its restart reconciliation, against a real
/// <see cref="BatchStore"/> and a fake executor that runs nothing.
/// </summary>
/// <remarks>
/// <b>The window is the only thing bounding how much work this host takes on at once</b>, and proving
/// it by starting four real game servers costs four game servers' worth of memory — the exact failure
/// the window exists to prevent. A fake executor that blocks until released makes the property
/// provable at no cost, which is why <see cref="ICommandExecutor"/> exists.
/// </remarks>
[Collection(BatchWorkerCollection.Name)]
public sealed class BatchWorkerTests
{
    [Fact]
    public async Task LifecycleVerbs_NeverExceedFourAtOnce()
    {
        await AssertWindowHolds(CommandVerb.Stop, members: 6, expectedWindow: 4);
    }

    [Fact]
    public async Task Update_NeverExceedsTwoAtOnce()
    {
        // Update is the expensive verb — steamcmd against one disk and one uplink — so it gets a
        // narrower window than the lifecycle verbs, and that difference is the whole point.
        await AssertWindowHolds(CommandVerb.Update, members: 4, expectedWindow: 2);
    }

    [Fact]
    public void TheTwoWindows_AreWhatTheyAreDocumentedToBe()
    {
        Assert.Equal(2, BatchWorker.WindowFor(CommandVerb.Update));
        Assert.Equal(4, BatchWorker.WindowFor(CommandVerb.Start));
        Assert.Equal(4, BatchWorker.WindowFor(CommandVerb.Stop));
        Assert.Equal(4, BatchWorker.WindowFor(CommandVerb.Restart));
    }

    private static async Task AssertWindowHolds(string verb, int members, int expectedWindow)
    {
        Harness h = Harness.New();
        await h.SeedBatchAsync("b1", verb, members);

        var executor = new BlockingExecutor(h.Registry);
        BatchWorker worker = h.Worker(executor);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Let the pump dispatch as much as it is willing to, then look at how much that was.
            await executor.WaitForConcurrentAsync(expectedWindow);
            await Task.Delay(250);
            Assert.Equal(expectedWindow, executor.Concurrent);

            executor.ReleaseAll();
            await h.WaitForSettledAsync("b1");

            // Every member ran exactly once, and the ceiling held for the whole run — not just at the
            // first pass, which is where an off-by-one in the harvest would show.
            Assert.Equal(members, executor.TotalRun);
            Assert.Equal(expectedWindow, executor.PeakConcurrent);

            BatchView? view = await h.Store.GetAsync("b1");
            Assert.Equal(BatchState.Settled, view!.State);
            Assert.Equal(members, view.Counts.Succeeded);
        }
        finally
        {
            executor.ReleaseAll();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restart_SettlesInterruptedMembersUnknown_AndResumesPendingOnes()
    {
        Harness h = Harness.New();
        await h.SeedBatchAsync("b1", CommandVerb.Stop, 3);
        // What an ended process leaves behind: one member it had started, two it had not reached.
        await h.Store.SetMemberStateAsync("b1", "srv-1", BatchMemberState.Running);

        var executor = new BlockingExecutor(h.Registry);
        BatchWorker worker = h.Worker(executor);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await executor.WaitForConcurrentAsync(2);
            executor.ReleaseAll();
            await h.WaitForSettledAsync("b1");

            BatchView? view = await h.Store.GetAsync("b1");
            BatchMember interrupted = view!.Members.Single(m => m.ServerId == "srv-1");

            // Nobody observed the outcome: the job record died with the process and the kgsm call was
            // its child. Calling it failed would claim a result that was never seen, and re-running it
            // could restart a server somebody deliberately stopped.
            Assert.Equal(BatchMemberState.Unknown, interrupted.State);
            Assert.Contains("restarted", interrupted.Error);
            Assert.Equal(2, view.Counts.Succeeded);
            // The interrupted member is NOT re-run — only the two that never started.
            Assert.Equal(2, executor.TotalRun);
            Assert.DoesNotContain("srv-1", executor.Ran);
        }
        finally
        {
            executor.ReleaseAll();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AFailedMember_CarriesTheEnginesOwnError_AndDoesNotStopTheBatch()
    {
        Harness h = Harness.New();
        await h.SeedBatchAsync("b1", CommandVerb.Stop, 3);

        var executor = new BlockingExecutor(h.Registry) { FailServer = "srv-2", FailError = "kgsm: exit 1" };
        BatchWorker worker = h.Worker(executor);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await executor.WaitForConcurrentAsync(3);
            executor.ReleaseAll();
            await h.WaitForSettledAsync("b1");

            BatchView? view = await h.Store.GetAsync("b1");
            Assert.Equal(2, view!.Counts.Succeeded);
            Assert.Equal(1, view.Counts.Failed);
            // The engine's real detail, kept as it was given rather than summarised into "failed".
            Assert.Equal("kgsm: exit 1", view.Members.Single(m => m.ServerId == "srv-2").Error);
        }
        finally
        {
            executor.ReleaseAll();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ACapacityRefusal_IsRecordedAsRefused_NotFailed()
    {
        Harness h = Harness.New();
        await h.SeedBatchAsync("b1", CommandVerb.Start, 3);

        var executor = new BlockingExecutor(h.Registry)
        {
            FailServer = "srv-2",
            FailError = "needs 8192MB, the node has 2048MB available",
            FailExitCode = 51, // kgsm's EC_INSUFFICIENT_MEMORY
        };
        BatchWorker worker = h.Worker(executor);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await executor.WaitForConcurrentAsync(3);
            executor.ReleaseAll();
            await h.WaitForSettledAsync("b1");

            BatchView? view = await h.Store.GetAsync("b1");

            // Nothing is wrong with the server — the node was full. Filing it as a failure reads as a
            // fault in the instance and invites a retry certain to be refused again.
            Assert.Equal(BatchMemberState.Refused, view!.Members.Single(m => m.ServerId == "srv-2").State);
            Assert.Equal(1, view.Counts.Refused);
            Assert.Equal(0, view.Counts.Failed);
            Assert.Equal(2, view.Counts.Succeeded);
        }
        finally
        {
            executor.ReleaseAll();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnyOtherEngineFailure_IsStillAFailure()
    {
        Harness h = Harness.New();
        await h.SeedBatchAsync("b1", CommandVerb.Start, 2);

        var executor = new BlockingExecutor(h.Registry)
        {
            FailServer = "srv-1",
            FailError = "port already in use",
            FailExitCode = 1,
        };
        BatchWorker worker = h.Worker(executor);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await executor.WaitForConcurrentAsync(2);
            executor.ReleaseAll();
            await h.WaitForSettledAsync("b1");

            // Only the capacity code is a refusal. Everything else the engine reports is a real fault,
            // and widening the rule would hide faults as "the node was busy".
            BatchView? view = await h.Store.GetAsync("b1");
            Assert.Equal(BatchMemberState.Failed, view!.Members.Single(m => m.ServerId == "srv-1").State);
            Assert.Equal(0, view.Counts.Refused);
        }
        finally
        {
            executor.ReleaseAll();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheBatchsForce_ReachesEveryMembersEngineCall()
    {
        Harness h = Harness.New();
        await h.SeedBatchAsync("b1", CommandVerb.Start, 3, force: true);

        var executor = new BlockingExecutor(h.Registry);
        BatchWorker worker = h.Worker(executor);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await executor.WaitForConcurrentAsync(3);
            executor.ReleaseAll();
            await h.WaitForSettledAsync("b1");

            // The override is the batch's, so every member carries it — a member that ran without it
            // would be refused for a reason its operator had already answered, minutes after they left.
            Assert.All(executor.Dispatched, d => Assert.True(d.Force));
            Assert.Equal(3, executor.Dispatched.Count);
        }
        finally
        {
            executor.ReleaseAll();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WithoutForce_NoMemberIsDispatchedWithIt()
    {
        Harness h = Harness.New();
        await h.SeedBatchAsync("b1", CommandVerb.Start, 2);

        var executor = new BlockingExecutor(h.Registry);
        BatchWorker worker = h.Worker(executor);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await executor.WaitForConcurrentAsync(2);
            executor.ReleaseAll();
            await h.WaitForSettledAsync("b1");

            // The protection is what a caller gets by not asking for the override.
            Assert.All(executor.Dispatched, d => Assert.False(d.Force));
        }
        finally
        {
            executor.ReleaseAll();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ---- harness --------------------------------------------------------------------------------

    private sealed record Harness(BatchStore Store, JobRegistry Registry, StreamHub Hub)
    {
        public static Harness New()
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"batchworkertest-{Guid.NewGuid():N}.db");
            ServiceProvider provider = new ServiceCollection()
                .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
                .BuildServiceProvider();
            using (IServiceScope scope = provider.CreateScope())
                scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

            return new Harness(
                new BatchStore(provider.GetRequiredService<IServiceScopeFactory>()),
                new JobRegistry(),
                new StreamHub(Options.Create(new JsonOptions())));
        }

        public BatchWorker Worker(ICommandExecutor executor) =>
            new(Store, Registry, executor, Hub, NullLogger<BatchWorker>.Instance);

        public Task SeedBatchAsync(string batchId, string verb, int members, bool force = false) =>
            Store.CreateAsync(
                new BatchEntity
                {
                    Id = batchId,
                    Verb = verb,
                    State = BatchState.Active,
                    Origin = "ui",
                    Force = force,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                Enumerable.Range(1, members).Select(i => new BatchMemberEntity
                {
                    BatchId = batchId,
                    ServerId = $"srv-{i}",
                    State = BatchMemberState.Pending,
                    JobId = $"job_{i}",
                    Position = i,
                }).ToList());

        public async Task WaitForSettledAsync(string batchId, int timeoutMs = 10_000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            while (!cts.IsCancellationRequested)
            {
                BatchView? v = await Store.GetAsync(batchId);
                if (v?.State == BatchState.Settled) return;
                await Task.Delay(25);
            }
            Assert.Fail($"batch {batchId} never settled");
        }
    }

    // An executor that runs nothing and holds every caller until released, so the worker's willingness
    // to dispatch is observable without any work actually happening.
    private sealed class BlockingExecutor(JobRegistry registry) : ICommandExecutor
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _sync = new();
        private int _concurrent;

        public string? FailServer { get; init; }
        public string? FailError { get; init; }

        /// <summary>The exit code the engine reports for the failing server — what the worker keys its
        /// refusal-vs-failure decision on.</summary>
        public int? FailExitCode { get; init; }

        /// <summary>Every (server, force) pair the worker dispatched, so a test can assert the batch's
        /// override actually reached the engine call rather than stopping at the store.</summary>
        public List<(string Server, bool Force)> Dispatched { get; } = [];

        public int Concurrent { get { lock (_sync) return _concurrent; } }
        public int PeakConcurrent { get; private set; }
        public int TotalRun { get; private set; }
        public List<string> Ran { get; } = [];

        public async Task<int?> RunAsync(Job job, string? actor = null, string? origin = null, bool force = false)
        {
            lock (_sync)
            {
                _concurrent++;
                if (_concurrent > PeakConcurrent) PeakConcurrent = _concurrent;
                TotalRun++;
                Ran.Add(job.ServerId);
                Dispatched.Add((job.ServerId, force));
            }

            await _gate.Task.ConfigureAwait(false);

            // Settle the job the way the real runner does — the worker reads the registry's verdict
            // rather than treating "the call returned" as success.
            bool fail = job.ServerId == FailServer;
            registry.Update(job with
            {
                State = fail ? JobState.Failed : JobState.Succeeded,
                SettledAt = DateTimeOffset.UtcNow,
                Error = fail ? FailError : null,
            });

            lock (_sync) { _concurrent--; }
            return fail ? FailExitCode : null;
        }

        public void ReleaseAll() => _gate.TrySetResult();

        public async Task WaitForConcurrentAsync(int n, int timeoutMs = 10_000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            while (!cts.IsCancellationRequested)
            {
                if (Concurrent >= n) return;
                await Task.Delay(25);
            }
            Assert.Fail($"never reached {n} concurrent (peak {PeakConcurrent})");
        }
    }
}

/// <summary>
/// Runs <see cref="BatchWorkerTests"/> alone, out of parallel with every other collection.
/// </summary>
/// <remarks>
/// These are the only tests here that stand up a real <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// and drive it against SQLite, and a run of one settles every member through two writes and a read.
/// Alongside the suites that bind an <c>HttpListener</c> or shell out to bash, that burst was enough to
/// tip a timing-sensitive one over roughly half the time — a different one each run, each passing on
/// its own. A test that proves a concurrency bound is worth having; one that makes an unrelated test
/// fail every other run is not, and the cheapest honest fix is to stop them overlapping.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BatchWorkerCollection
{
    public const string Name = "batch-worker";
}
