using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The <c>starting</c> tri-state on <see cref="InstanceCache"/> — the "starting latch" that tracks the
/// window between an <c>instance_started</c> event (process spawned) and the matching
/// <c>instance_ready</c> event (the watchdog confirms the game finished booting). Both events observe
/// the process as "up" via the same boolean <see cref="Reading{T}"/>, so the latch is the ONLY thing
/// that can tell them apart.
/// <para>
/// The load-bearing test is <see cref="ReconcileStartingLatch_DoesNotPromote_WhileStillMeasuredUp"/> —
/// the background boolean reconcile (which re-derives run-state from kgsm every
/// <c>KGSM_API_INSTANCE_CACHE_TTL_SECONDS</c>) sees the same "process up" signal for both a starting AND
/// a running instance, and must never be allowed to flip <c>starting -&gt; running</c> on its own; only
/// <c>MarkReady</c> (the <c>instance_ready</c> event) may do that.
/// </para>
/// </summary>
public sealed class InstanceCacheStartingTests
{
    // --- (a) started -> starting -------------------------------------------------------------------

    [Fact]
    public void MarkStarting_OpensTheLatch_BooleanStatusIsUp()
    {
        InstanceCache cache = NewCache();

        cache.MarkStarting("factorio-1");

        Assert.True(cache.IsStarting("factorio-1"));
        Reading<InstanceRuntimeStatus> reading = cache.Statuses["factorio-1"];
        Assert.True(reading.IsMeasured);
        Assert.True(reading.Value!.Status); // the boolean flips true immediately, same as before
    }

    // --- (b) ready -> running -----------------------------------------------------------------------

    [Fact]
    public void MarkReady_ClosesTheLatch_StatusBecomesRunning()
    {
        InstanceCache cache = NewCache();

        cache.MarkStarting("factorio-1");
        Assert.True(cache.IsStarting("factorio-1"));

        cache.MarkReady("factorio-1");

        Assert.False(cache.IsStarting("factorio-1"));
        Assert.True(cache.Statuses["factorio-1"].Value!.Status); // still observed up
    }

    [Fact]
    public void MarkReady_WithNoPriorStarted_StillFlipsBooleanToRunning_DefensiveNeverStale()
    {
        // A ready event with no observed instance_started (e.g. the consumer wasn't listening for it) is
        // still real evidence the process is up — must not leave a stale/unset boolean.
        InstanceCache cache = NewCache();

        cache.MarkReady("mc-1");

        Assert.False(cache.IsStarting("mc-1"));
        Assert.True(cache.Statuses["mc-1"].Value!.Status);
    }

    // --- (d) started-then-ready in quick succession -> running (order-tolerant, idempotent) ---------

    [Fact]
    public void StartedThenReady_QuickSuccession_SettlesOnRunning()
    {
        // The "empty-regex games" case (requirement #6): the watchdog emits instance_ready immediately
        // after instance_started for a game with no readiness pattern. Two rapid events must resolve
        // cleanly to running, not get stuck starting or double-apply.
        InstanceCache cache = NewCache();

        cache.MarkStarting("terraria-1");
        cache.MarkReady("terraria-1");

        Assert.False(cache.IsStarting("terraria-1"));
        Assert.True(cache.Statuses["terraria-1"].Value!.Status);
    }

    [Fact]
    public void MarkReady_CalledTwice_Idempotent()
    {
        InstanceCache cache = NewCache();
        cache.MarkStarting("valheim-1");

        cache.MarkReady("valheim-1");
        cache.MarkReady("valheim-1"); // a duplicate/late-redelivered ready must not throw or misbehave

        Assert.False(cache.IsStarting("valheim-1"));
        Assert.True(cache.Statuses["valheim-1"].Value!.Status);
    }

    // --- (e) stop/crash/fail during starting clears the latch -> stopped ----------------------------

    [Theory]
    [InlineData(false)] // UpdateStatus(false) is what the stop/crashed/failed handlers all call
    public void UpdateStatus_Down_ClearsTheLatch_EvenMidStart(bool running)
    {
        InstanceCache cache = NewCache();
        cache.MarkStarting("rust-1");
        Assert.True(cache.IsStarting("rust-1"));

        cache.UpdateStatus("rust-1", running);

        Assert.False(cache.IsStarting("rust-1"));
        Assert.False(cache.Statuses["rust-1"].Value!.Status);
    }

    [Fact]
    public void UpdateStatus_Up_DoesNotOpenTheLatch_OnlyMarkStartingDoes()
    {
        // e.g. InstanceRestartedData still calls UpdateStatus(true) directly (out of scope for this
        // increment, see KgsmAuditConsumer) — it must NOT accidentally open a starting window.
        InstanceCache cache = NewCache();

        cache.UpdateStatus("ark-1", true);

        Assert.False(cache.IsStarting("ark-1"));
        Assert.True(cache.Statuses["ark-1"].Value!.Status);
    }

    // --- (c) THE KEY TEST: the reconcile hazard --------------------------------------------------

    [Fact]
    public void ReconcileStartingLatch_DoesNotPromote_WhileStillMeasuredUp()
    {
        // The background boolean reconcile reads GetAllStatuses(fast:true) fresh from the engine. While an
        // instance is genuinely still starting, the process IS up, so the fresh read says Status:true —
        // IDENTICAL to a fully running instance. This must NOT be treated as "done starting"; only
        // instance_ready (MarkReady) may close the latch. This is the main correctness trap the feature
        // exists to avoid.
        InstanceCache cache = NewCache();
        cache.MarkStarting("factorio-1");

        var freshStillUp = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "factorio-1", Status = true }),
        };

        cache.ReconcileStartingLatch(freshStillUp);

        Assert.True(cache.IsStarting("factorio-1")); // still starting — the reconcile must not win
    }

    [Fact]
    public void ReconcileStartingLatch_ManyTicks_StillDoesNotPromote()
    {
        // Simulate several reconcile passes in a row (as would happen over real background-refresh
        // cadence) while the process stays observed up — the latch must survive all of them.
        InstanceCache cache = NewCache();
        cache.MarkStarting("factorio-1");

        var freshStillUp = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "factorio-1", Status = true }),
        };

        for (int i = 0; i < 5; i++)
            cache.ReconcileStartingLatch(freshStillUp);

        Assert.True(cache.IsStarting("factorio-1"));
    }

    [Fact]
    public async Task FullRefreshPipeline_ReconcileDuringStartingWindow_DoesNotFlipToRunning()
    {
        // End-to-end through the actual public entry point (TryRefresh -> RefreshAsync ->
        // ReconcileStartingLatch -> _statuses swap), not just the internal method directly — proves the
        // wiring order (reconcile runs BEFORE the fresh statuses replace the cached ones) is correct.
        var fake = new MutableFakeInstanceService();
        fake.Roster["factorio-1"] = new Instance { Name = "factorio-1", BlueprintFile = "factorio.bp.yaml" };
        fake.Statuses["factorio-1"] = Reading<InstanceRuntimeStatus>.Measured(
            new InstanceRuntimeStatus { InstanceName = "factorio-1", Status = true });

        InstanceCache cache = NewCache(fake);
        await cache.StartAsync(CancellationToken.None);

        cache.MarkStarting("factorio-1");
        Assert.True(cache.IsStarting("factorio-1"));

        // The engine still reports the process up (fast poll can't distinguish starting from running).
        Assert.True(cache.TryRefresh());
        // TryRefresh's actual work is a fire-and-forget Task.Run against a synchronous, near-instant fake —
        // give it a short, generous settle window rather than asserting immediately after the call returns.
        await Task.Delay(200);

        Assert.True(cache.IsStarting("factorio-1")); // must NOT have been promoted to running by the reconcile
    }

    // --- (f) resolving a genuinely down/vanished/timed-out latch --------------------------------

    [Fact]
    public void ReconcileStartingLatch_ClosesWhenMeasuredDown_SilentDeathNoEventReached()
    {
        // A silent death (the process died without a crash/stop event reaching this consumer) is honest
        // NEW evidence the reconcile may act on: the fresh read now says Status:false.
        InstanceCache cache = NewCache();
        cache.MarkStarting("factorio-1");

        var freshDown = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "factorio-1", Status = false }),
        };

        cache.ReconcileStartingLatch(freshDown);

        Assert.False(cache.IsStarting("factorio-1"));
    }

    [Fact]
    public void ReconcileStartingLatch_ClosesWhenInstanceVanishedFromRoster()
    {
        InstanceCache cache = NewCache();
        cache.MarkStarting("factorio-1");

        cache.ReconcileStartingLatch(new Dictionary<string, Reading<InstanceRuntimeStatus>>()); // gone

        Assert.False(cache.IsStarting("factorio-1"));
    }

    [Fact]
    public void ReconcileStartingLatch_SafetyTimeout_ResolvesToRunning_NeverStuckForever()
    {
        InstanceCache cache = NewCache();
        // Seed a window that opened well past the safety bound (deterministic — no wall-clock sleep).
        cache.MarkStartingAt("factorio-1", DateTimeOffset.UtcNow - InstanceCache.StartingTimeout - TimeSpan.FromSeconds(1));
        Assert.True(cache.IsStarting("factorio-1"));

        var freshStillUp = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "factorio-1", Status = true }),
        };
        cache.ReconcileStartingLatch(freshStillUp);

        // Resolves honestly to running (the process IS observed up) — never fabricated, never stuck.
        Assert.False(cache.IsStarting("factorio-1"));
    }

    [Fact]
    public void ReconcileStartingLatch_WithinTimeout_NotYetExpired_StaysStarting()
    {
        InstanceCache cache = NewCache();
        // Well within the bound — must not be swept early.
        cache.MarkStartingAt("factorio-1", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5));

        var freshStillUp = new Dictionary<string, Reading<InstanceRuntimeStatus>>
        {
            ["factorio-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "factorio-1", Status = true }),
        };
        cache.ReconcileStartingLatch(freshStillUp);

        Assert.True(cache.IsStarting("factorio-1"));
    }

    // --- multiple instances don't cross-contaminate the latch ---------------------------------------

    [Fact]
    public void Latch_IsPerInstance_IndependentOfOthers()
    {
        InstanceCache cache = NewCache();
        cache.MarkStarting("a");
        cache.MarkStarting("b");

        cache.MarkReady("a");

        Assert.False(cache.IsStarting("a"));
        Assert.True(cache.IsStarting("b"));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static InstanceCache NewCache(MutableFakeInstanceService? fake = null)
    {
        fake ??= new MutableFakeInstanceService();
        IServiceProvider services = new ServiceCollection()
            .AddSingleton<IInstanceService>(fake)
            .BuildServiceProvider();
        ApiOptions options = ApiOptions.FromConfiguration(new ConfigurationBuilder().Build());
        return new InstanceCache(services, options, NullLogger<InstanceCache>.Instance);
    }

    /// <summary>
    /// Switch-on-input-friendly mutable fake (the project convention is normally stateless, but the
    /// starting-latch reconcile inherently needs a controllable "what does the next engine read say"
    /// seam) — only the two members <see cref="InstanceCache"/> actually calls are implemented.
    /// </summary>
    private sealed class MutableFakeInstanceService : IInstanceService
    {
        public readonly Dictionary<string, Instance> Roster = new();
        public readonly Dictionary<string, Reading<InstanceRuntimeStatus>> Statuses = new();

        public Dictionary<string, Instance>? GetAllOrNull() => new(Roster);
        public Dictionary<string, Instance> GetAll() => new(Roster);
        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new(Statuses);

        // --- not exercised by InstanceCache.RefreshAsync: honest NotImplemented ---
        public Instance? GetInstanceInfo(string instanceName) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetInstalledVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetLatestVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult CheckUpdate(string instanceName) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<TheKrystalShip.KGSM.Core.Models.LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TheKrystalShip.KGSM.Core.Models.LogSubscription> SubscribeToLogsAsync(string instanceName, TheKrystalShip.KGSM.Core.Models.Enums.LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
