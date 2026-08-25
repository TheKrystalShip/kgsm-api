using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Models;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The metric-threshold alert source: <see cref="AlertEngine.TickConditions"/> driven with crafted monitor
/// frames. kgsm-monitor decides which values are over their line and for how long — every dwell, deadband
/// and hysteresis case is pinned in that repo's <c>ConditionEvaluatorTests</c>, against the samples it took.
/// What is pinned HERE is the half this API owns: a condition becomes an alert with the right source,
/// severity, anchor and words; a condition that stops appearing resolves at once, because the monitor
/// already verified the clear; and a monitor that says nothing at all changes nothing, which is a different
/// answer from a monitor that says all-clear.
/// </summary>
public sealed class MetricsThresholdAlertTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // --- 1. a condition becomes a firing alert with the honest shape ---------------------------------
    [Fact]
    public void HostCondition_Raises_WithHostSourceAndAnchor()
    {
        ApiOptions options = ApiOpts();
        AlertEngine engine = Engine(options);

        engine.TickConditions(With(MemCondition(95)), T0);

        Alert a = Assert.Single(engine.Firing);
        Assert.Equal("metric:host-mem", a.Id);
        Assert.Equal(AlertSeverity.Warn, a.Severity);
        Assert.Equal(AlertSource.HostMonitor, a.Source);
        Assert.Null(a.ServerId);
        Assert.False(a.Escalated);          // metrics never escalate — severity carries how bad it is
        Assert.Equal(0, a.Attempts);
        Assert.Equal(AlertStatus.Firing, a.Status);
        Assert.Equal(options.HostId, a.HostId);
        Assert.NotNull(a.Anchor);
        Assert.Equal(AlertSurface.Host, a.Anchor!.Surface);
        Assert.Equal("resources", a.Anchor.Tab);
        Assert.Contains("memory at 95%", a.Title);
    }

    [Fact]
    public void ServerCondition_Raises_WithMetricsSourceAndServerAnchor()
    {
        AlertEngine engine = Engine(ApiOpts());

        engine.TickConditions(With(Condition("srv-pids", "ServerPids", scope: "server",
            serverId: "factorio-test", value: 1500, windowMax: 1500, threshold: 1000)), T0);

        Alert a = Assert.Single(engine.Firing);
        Assert.Equal("metric:srv-pids:factorio-test", a.Id);
        Assert.Equal(AlertSource.Metrics, a.Source);
        Assert.Equal("factorio-test", a.ServerId);
        Assert.Equal(AlertSurface.Server, a.Anchor!.Surface);
        Assert.Equal("performance", a.Anchor.Tab);
        Assert.Contains("factorio-test processes at 1500 pids", a.Title);
    }

    // --- 2. the API adds no dwell of its own ---------------------------------------------------------
    [Fact]
    public void Raise_IsImmediate_TheDwellAlreadyHappenedUpstream()
    {
        AlertEngine engine = Engine(ApiOpts());

        // The monitor only publishes a condition once its own fire dwell is satisfied. Re-running a
        // probation here would delay every alert by a second dwell nobody configured.
        engine.TickConditions(With(MemCondition(95)), T0);
        Assert.Single(engine.Firing);
    }

    [Fact]
    public void Resolve_IsImmediate_OnAbsence()
    {
        AlertEngine engine = Engine(ApiOpts());
        engine.TickConditions(With(MemCondition(95)), T0);

        // Absent from the frame = the monitor ran its clear dwell and closed it. Holding it open here
        // would delay a recovery that was already verified against every sample.
        engine.TickConditions(Empty(), T0 + Secs(5));

        Assert.Empty(engine.Firing);
        Alert resolved = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.Equal("metric:host-mem", resolved.Id);
        Assert.Equal(AlertStatus.Resolved, resolved.Status);
        Assert.Equal(T0 + Secs(5), resolved.ResolvedAt);
    }

    // --- 3. resolution provenance --------------------------------------------------------------------
    [Fact]
    public void Resolution_IsBySystem_WithNoActionId()
    {
        AlertEngine engine = Engine(ApiOpts());
        engine.TickConditions(With(MemCondition(95)), T0);
        engine.TickConditions(Empty(), T0 + Secs(5));

        Alert resolved = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.NotNull(resolved.Resolution);
        Assert.Equal(AlertResolvedBy.System, resolved.Resolution!.By);
        Assert.Equal(AlertSource.HostMonitor, resolved.Resolution.Source);

        // The actionId bridge is crash-specific: a threshold clears because the value receded, not
        // because anybody did anything. Never a fabricated link.
        Assert.Null(resolved.Resolution.ActionId);
    }

    // --- 4. band changes upsert, steady state does not re-push ---------------------------------------
    [Fact]
    public void CrossingIntoDanger_RePushes_SameRecord_KeepingRaisedAt()
    {
        AlertEngine engine = Engine(ApiOpts());

        engine.TickConditions(With(MemCondition(95)), T0);
        Alert warn = Assert.Single(engine.Firing);
        Assert.Equal(AlertSeverity.Warn, warn.Severity);

        engine.TickConditions(With(MemCondition(98, band: "danger", threshold: 97)), T0 + Secs(5));
        Alert danger = Assert.Single(engine.Firing);

        Assert.Equal(AlertSeverity.Danger, danger.Severity);
        Assert.Equal(warn.Id, danger.Id);
        Assert.Equal(warn.RaisedAt, danger.RaisedAt);   // the same condition getting worse, not a new one
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    [Fact]
    public void AnUnchangedCondition_DoesNotRePush()
    {
        AlertEngine engine = Engine(ApiOpts());

        engine.TickConditions(With(MemCondition(95)), T0);
        Alert first = Assert.Single(engine.Firing);

        engine.TickConditions(With(MemCondition(95)), T0 + Secs(5));
        engine.TickConditions(With(MemCondition(95)), T0 + Secs(10));
        Alert last = Assert.Single(engine.Firing);

        // The engine builds a candidate every tick but only stores-and-publishes a changed one, so the
        // record surviving by reference IS the no-re-push. It polls every few seconds and a condition can
        // last hours; a frame per scrape to every open browser is what this guard prevents.
        Assert.Same(first, last);
    }

    [Fact]
    public void AChangedValue_RePushes()
    {
        AlertEngine engine = Engine(ApiOpts());

        engine.TickConditions(With(MemCondition(95)), T0);
        Alert first = Assert.Single(engine.Firing);

        engine.TickConditions(With(MemCondition(96)), T0 + Secs(5));
        Alert second = Assert.Single(engine.Firing);

        Assert.NotSame(first, second);           // the headline number moved, so the card has to
        Assert.Equal(first.RaisedAt, second.RaisedAt);
    }

    // --- 5. honest-unknown ---------------------------------------------------------------------------
    [Fact]
    public void MonitorDown_NullSnapshot_HoldsFiring()
    {
        AlertEngine engine = Engine(ApiOpts());
        engine.TickConditions(With(MemCondition(95)), T0);

        // No frame at all is not all-clear. Resolving here would report a recovery nobody measured.
        engine.TickConditions(null, T0 + Secs(5));

        Assert.Single(engine.Firing);
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    // --- 6. the detail line reports the peak, not just the latest reading -----------------------------
    [Fact]
    public void Detail_NamesThePeak_WhenItDiffersFromTheCurrentValue()
    {
        AlertEngine engine = Engine(ApiOpts());

        engine.TickConditions(With(MemCondition(92, windowMax: 99)), T0);

        Alert a = Assert.Single(engine.Firing);
        Assert.Contains("92%", a.Title);            // what it reads now
        Assert.Contains("peaking at 99%", a.Detail); // what actually justified the alarm

        // The distinction the whole source exists for: a scrape at 92 would never have known about 99.
        Assert.DoesNotContain("99", a.Title);
    }

    [Fact]
    public void Detail_OmitsThePeak_WhenItIsTheSameNumber()
    {
        AlertEngine engine = Engine(ApiOpts());
        engine.TickConditions(With(MemCondition(95, windowMax: 95)), T0);

        Alert a = Assert.Single(engine.Firing);
        Assert.DoesNotContain("peaking", a.Detail);
    }

    // --- 7. fan-out targets are independent alerts ---------------------------------------------------
    [Fact]
    public void FanOut_YieldsOneAlertPerTarget()
    {
        AlertEngine engine = Engine(ApiOpts());

        engine.TickConditions(With(
            Condition("host-disk", "HostDiskUsedPct", refKey: "/", value: 94, windowMax: 94, threshold: 90),
            Condition("host-disk", "HostDiskUsedPct", refKey: "/data", value: 96, windowMax: 96, threshold: 90)), T0);

        Assert.Equal(2, engine.Firing.Count);
        Assert.Contains(engine.Firing, a => a.Id == "metric:host-disk:/");
        Assert.Contains(engine.Firing, a => a.Id == "metric:host-disk:/data");
        Assert.Contains(engine.Firing, a => a.Anchor!.Ref == "/data");
    }

    [Fact]
    public void OneFanOutTargetResolving_LeavesTheOtherFiring()
    {
        AlertEngine engine = Engine(ApiOpts());
        engine.TickConditions(With(
            Condition("host-disk", "HostDiskUsedPct", refKey: "/", value: 94, windowMax: 94, threshold: 90),
            Condition("host-disk", "HostDiskUsedPct", refKey: "/data", value: 96, windowMax: 96, threshold: 90)), T0);

        engine.TickConditions(With(
            Condition("host-disk", "HostDiskUsedPct", refKey: "/data", value: 96, windowMax: 96, threshold: 90)), T0 + Secs(5));

        Alert still = Assert.Single(engine.Firing);
        Assert.Equal("metric:host-disk:/data", still.Id);
        Assert.Equal("metric:host-disk:/", Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1))).Id);
    }

    // --- 8. the alert id survives a condition clearing and recurring ---------------------------------
    [Fact]
    public void AlertId_IsStable_AcrossANewEpisode()
    {
        AlertEngine engine = Engine(ApiOpts());

        engine.TickConditions(With(MemCondition(95, episodeId: "host-mem::1000")), T0);
        engine.TickConditions(Empty(), T0 + Secs(5));
        engine.TickConditions(With(MemCondition(95, episodeId: "host-mem::9999")), T0 + Secs(10));

        // The monitor's episode id changes; this feed's id must not. An operator looking at "metric:host-mem"
        // is looking at the host's memory, and a recurrence upserts that card rather than opening a second.
        Alert a = Assert.Single(engine.Firing);
        Assert.Equal("metric:host-mem", a.Id);
        Assert.Equal(T0 + Secs(10), a.RaisedAt);   // but it IS a new occurrence, dated as one
    }

    // --- 9. the shared firing set stays partitioned by source ----------------------------------------
    [Fact]
    public void CrashAndMetric_Coexist_NeitherTickDisturbsTheOther()
    {
        AlertEngine engine = Engine(ApiOpts());

        engine.Tick([Crashing("factorio-test")], T0);
        engine.TickConditions(With(MemCondition(95)), T0);
        Assert.Equal(2, engine.Firing.Count);

        // A crash tick sees no metric target in the watchdog's state and must not read that as a reason to
        // retract the metric alert; a condition tick must not touch the crash alert either.
        engine.Tick([Crashing("factorio-test")], T0 + Secs(5));
        engine.TickConditions(With(MemCondition(95)), T0 + Secs(5));

        Assert.Equal(2, engine.Firing.Count);
        Assert.Contains(engine.Firing, a => a.Id == "crash:factorio-test");
        Assert.Contains(engine.Firing, a => a.Id == "metric:host-mem");
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    // --- 10. a frame with no conditions at all --------------------------------------------------------
    [Fact]
    public void AnEmptyConditionsArray_IsAllClear_NotUnknown()
    {
        AlertEngine engine = Engine(ApiOpts());
        engine.TickConditions(Empty(), T0);
        Assert.Empty(engine.Firing);
    }

    // --- helpers --------------------------------------------------------------------------------------

    private static TimeSpan Secs(int s) => TimeSpan.FromSeconds(s);

    private static Snap.ConditionReading MemCondition(
        double value, double? windowMax = null, string band = "warn", double threshold = 90,
        string episodeId = "host-mem::1000") =>
        Condition("host-mem", "HostMemUsedPct", value: value, windowMax: windowMax ?? value,
            band: band, threshold: threshold, episodeId: episodeId);

    private static Snap.ConditionReading Condition(
        string ruleKey, string metric, double value, double windowMax, double threshold,
        string scope = "host", string? refKey = null, string? serverId = null, string band = "warn",
        string episodeId = "ep") =>
        new(EpisodeId: episodeId, RuleKey: ruleKey, Metric: metric, Scope: scope, Ref: refKey,
            ServerId: serverId, Band: band, Value: value, WindowMax: windowMax, Threshold: threshold,
            Since: 1_000);

    private static Snap.Snapshot Empty() => With();

    private static Snap.Snapshot With(params Snap.ConditionReading[] conditions) => new(
        Ts: 1_000, IntervalMs: 1000, Hostname: "hotrod", UptimeSec: 100,
        Cpu: new Snap.CpuMetrics(TotalPct: 5, PerCore: [5, 5],
            Load: new Snap.LoadAvg(0.1, 0.1, 0.1),
            Info: new Snap.CpuInfo("Test CPU", 8, 16, 3.5)),
        Mem: new Snap.MemoryMetrics(TotalKb: 1_000, AvailableKb: 900, UsedKb: 100, UsedPct: 10,
            SwapTotalKb: 1_000, SwapUsedKb: 0, CachedKb: 0, BuffersKb: 0),
        Disk: new Snap.DiskMetrics(
            Mounts: [new Snap.MountUsage("/", "ext4", 10, 100, 10.0, Device: null)],
            Io: new Snap.DiskIo(0, 0)),
        Net: new Snap.NetworkMetrics(Ifaces: []),
        Sensors: [new Snap.SensorReading("k10temp/0000:00:18.3/temp1", "k10temp", "Tctl", 30.0, "cpu", "CPU temperature")],
        Servers: [],
        Leaves: [],
        Conditions: conditions);

    // --- engine wiring (mirrors AlertEngineTests; the MonitorClient is never invoked — TickConditions
    //     takes the frame as an argument) -------------------------------------------------------------
    private static AlertEngine Engine(ApiOptions options, StreamHub? hub = null) =>
        new(options, new StubProvider(), Monitor(options), Instances(options), hub ?? Hub(),
            NullLogger<AlertEngine>.Instance);

    // Inert, like the MonitorClient beside it — TickConditions never reads the instance cache.
    private static InstanceCache Instances(ApiOptions options) =>
        new(new StubProvider(), options, NullLogger<InstanceCache>.Instance);

    private static ApiOptions ApiOpts() =>
        ApiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:HostId"] = "hotrod" })
            .Build());

    private static StreamHub Hub() => new(Options.Create(new JsonOptions()));

    private static MonitorClient Monitor(ApiOptions options)
    {
        IServiceScopeFactory scopeFactory =
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var registry = new LeafRegistry(scopeFactory, options, NullLogger<LeafRegistry>.Instance);
        return new MonitorClient(options, registry, NullLogger<MonitorClient>.Instance);
    }

    private static WatchdogInstanceState Crashing(string name) =>
        new() { Name = name, Desired = "running", Phase = "restart-pending", Restarts = 1, Reason = "" };

    private sealed class StubProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
