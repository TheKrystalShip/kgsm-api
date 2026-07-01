using System.Globalization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Models;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Increment-1 coverage for the metrics-threshold alert source (<c>metrics-threshold-alerts-plan.md</c>) —
/// the <see cref="AlertEngine.TickMetrics"/> reconcile driven with crafted monitor snapshots + controlled
/// time. Load-bearing invariants: a one-tick spike never fires (the fire-dwell); a sustained breach fires
/// once with the honest source/anchor; crossing the danger band re-pushes (never auto-resolves on severity);
/// a value in the hysteresis deadband never flaps; a resolution is always <c>by:system</c> with
/// <c>actionId:null</c> (no audit bridge for metrics); a not-evaluable field holds; a down monitor
/// (<c>null</c> snapshot) changes nothing; a vanished server row resolves only when the monitor is up; a
/// fan-out rule yields one alert per target; and the metric pass never disturbs a crash alert in the shared
/// firing set (the namespacing the crash <c>Tick</c> guards).
/// </summary>
public sealed class MetricsThresholdAlertTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // --- 1. a one-tick spike never fires (the fire-dwell kills it) ---------------------------------
    [Fact]
    public void Spike_UnderFireDwell_NeverFires()
    {
        AlertEngine engine = Engine(OptionsWith(Mem(warn: 90, fireForSec: 60)));

        engine.TickMetrics(MemAt(95), T0);            // breach begins — dwell 60s not yet met
        Assert.Empty(engine.Firing);

        engine.TickMetrics(MemAt(10), T0 + Secs(5));  // recovered well before the dwell elapsed
        Assert.Empty(engine.Firing);
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    // --- 2. a sustained breach fires once after FireForSec, with the honest shape ------------------
    [Fact]
    public void SustainedBreach_Fires_AfterDwell_WithHostAnchor()
    {
        ApiOptions options = OptionsWith(Mem(warn: 90, fireForSec: 60));
        AlertEngine engine = Engine(options);

        engine.TickMetrics(MemAt(95), T0);            // pending
        Assert.Empty(engine.Firing);

        engine.TickMetrics(MemAt(95), T0 + Secs(61)); // dwell met → fire
        Alert a = Assert.Single(engine.Firing);
        Assert.Equal("metric:t-mem", a.Id);
        Assert.Equal(AlertSeverity.Warn, a.Severity);
        Assert.Equal(AlertSource.HostMonitor, a.Source);     // host-scope rule → host-monitor source
        Assert.Null(a.ServerId);                              // host-scope → no server
        Assert.False(a.Escalated);                           // metrics never escalate
        Assert.Equal(AlertStatus.Firing, a.Status);
        Assert.NotNull(a.Anchor);
        Assert.Equal(AlertSurface.Host, a.Anchor!.Surface);
        Assert.Equal(options.HostId, a.HostId);
    }

    // --- 3. crossing the danger band re-pushes the SAME record (raisedAt preserved) ---------------
    [Fact]
    public void CrossingDanger_RePushes_SameRecord_NeverResolvesOnSeverity()
    {
        AlertEngine engine = Engine(OptionsWith(Mem(warn: 90, danger: 97, fireForSec: 0)));

        engine.TickMetrics(MemAt(92), T0);            // fires warn immediately (dwell 0)
        Alert warn = Assert.Single(engine.Firing);
        Assert.Equal(AlertSeverity.Warn, warn.Severity);

        engine.TickMetrics(MemAt(98), T0 + Secs(1));  // crosses danger → re-push
        Alert danger = Assert.Single(engine.Firing);
        Assert.Equal(AlertSeverity.Danger, danger.Severity);
        Assert.Equal(warn.RaisedAt, danger.RaisedAt);        // upsert, not a fresh raise
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1))); // a severity change is not a resolve
    }

    // --- 4. a value in the hysteresis deadband never flaps the feed --------------------------------
    [Fact]
    public void Deadband_HoldsFiring_NoFlap()
    {
        AlertEngine engine = Engine(OptionsWith(Mem(warn: 90, clearMargin: 5, clearForSec: 30, fireForSec: 0)));

        engine.TickMetrics(MemAt(92), T0);                   // fires
        Assert.Single(engine.Firing);

        engine.TickMetrics(MemAt(88), T0 + Secs(1));         // 88 in (85,90] deadband — not cleared
        engine.TickMetrics(MemAt(88), T0 + Secs(40));        // still deadband, 40s > clearForSec, still NO resolve
        Assert.Single(engine.Firing);
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));

        engine.TickMetrics(MemAt(80), T0 + Secs(41));        // drops past the margin (<=85) → clear arms
        engine.TickMetrics(MemAt(80), T0 + Secs(72));        // held clear >= 30s → resolves
        Assert.Empty(engine.Firing);
        Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    // --- 5. a metric resolution is always by:system with actionId:null (no audit bridge) ----------
    [Fact]
    public void Resolution_IsHonestNull_BySystem()
    {
        AlertEngine engine = Engine(OptionsWith(Mem(warn: 90, clearMargin: 5, clearForSec: 0, fireForSec: 0)));

        engine.TickMetrics(MemAt(95), T0);                   // fire
        engine.TickMetrics(MemAt(80), T0 + Secs(1));         // clear arms
        engine.TickMetrics(MemAt(80), T0 + Secs(2));         // resolves

        Alert resolved = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.Equal(AlertStatus.Resolved, resolved.Status);
        Assert.NotNull(resolved.Resolution);
        Assert.Equal(AlertResolvedBy.System, resolved.Resolution!.By);
        Assert.Null(resolved.Resolution.ActionId);           // the actionId↔audit bridge is crash-only
        Assert.Equal(AlertSource.HostMonitor, resolved.Resolution.Source);
    }

    // --- 6. a not-evaluable field holds (never fires) ----------------------------------------------
    [Fact]
    public void NotEvaluableField_Holds_NeverFires()
    {
        // HostLoadPerCore needs Cpu.Info.Cores; with Info null it is not evaluable even at a huge load.
        AlertEngine engine = Engine(OptionsWith(Load(warn: 1.0, fireForSec: 0)));
        Snap.Snapshot b = Base();
        Snap.Snapshot noCpuInfo = b with
        {
            Cpu = b.Cpu with { Info = null, Load = new Snap.LoadAvg(100, 100, 100) },
        };

        engine.TickMetrics(noCpuInfo, T0);
        Assert.Empty(engine.Firing);                         // Observe skipped it → no fabricated breach
    }

    // --- 7. a down monitor (null snapshot) changes nothing -----------------------------------------
    [Fact]
    public void MonitorDown_NullSnapshot_HoldsFiring()
    {
        AlertEngine engine = Engine(OptionsWith(Mem(warn: 90, fireForSec: 0)));
        engine.TickMetrics(MemAt(95), T0);                   // fire
        Assert.Single(engine.Firing);

        engine.TickMetrics(null, T0 + Secs(10));             // monitor down → honest-unknown
        Assert.Single(engine.Firing);                        // not resolved, not retracted
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    // --- 8. a vanished server row resolves — but NOT on a monitor blackout -------------------------
    [Fact]
    public void VanishedServerRow_Resolves_ButNotOnBlackout()
    {
        AlertEngine engine = Engine(OptionsWith(Pids(warn: 1000, clearForSec: 30, fireForSec: 0)));

        engine.TickMetrics(WithServers(Server("factorio-test", pids: 1500)), T0); // fire server-scope
        Alert a = Assert.Single(engine.Firing);
        Assert.Equal("metric:t-pids:factorio-test", a.Id);
        Assert.Equal(AlertSource.Metrics, a.Source);         // per-server rule → metrics source
        Assert.Equal("factorio-test", a.ServerId);
        Assert.Equal(AlertSurface.Server, a.Anchor!.Surface);

        engine.TickMetrics(null, T0 + Secs(60));             // a blackout must NOT resolve a vanished-looking row
        Assert.Single(engine.Firing);

        engine.TickMetrics(WithServers(), T0 + Secs(61));    // monitor UP, row gone → clear arms
        engine.TickMetrics(WithServers(), T0 + Secs(92));    // held >= 30s → resolves
        Assert.Empty(engine.Firing);
        Alert resolved = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.Null(resolved.Resolution!.ActionId);
    }

    // --- 9. a fan-out rule yields one alert per target ---------------------------------------------
    [Fact]
    public void DiskRule_FansOut_OneAlertPerMount()
    {
        AlertEngine engine = Engine(OptionsWith(Disk(warn: 90, fireForSec: 0)));
        Snap.Snapshot b = Base();
        Snap.Snapshot twoMountsOver = b with
        {
            Disk = new Snap.DiskMetrics(
                Mounts:
                [
                    new Snap.MountUsage("/", "ext4", 92, 100, 92.0, Device: null),
                    new Snap.MountUsage("/data", "ext4", 96, 100, 96.0, Device: null),
                ],
                Io: new Snap.DiskIo(0, 0)),
        };

        engine.TickMetrics(twoMountsOver, T0);
        Assert.Equal(2, engine.Firing.Count);
        Assert.Contains(engine.Firing, x => x.Id == "metric:t-disk:/");
        Assert.Contains(engine.Firing, x => x.Id == "metric:t-disk:/data");
    }

    // --- 10. the metric pass and a crash alert coexist; a crash poll never disturbs a metric alert -
    [Fact]
    public void CrashAndMetric_Coexist_CrashTickDoesNotRetractMetric()
    {
        AlertEngine engine = Engine(OptionsWith(Mem(warn: 90, fireForSec: 0)));

        engine.Tick([Crashing("srv1")], T0);                 // crash alert
        engine.TickMetrics(MemAt(95), T0);                   // metric alert (shared firing set)
        Assert.Equal(2, engine.Firing.Count);
        Assert.Contains(engine.Firing, x => x.Id == "crash:srv1");
        Assert.Contains(engine.Firing, x => x.Id == "metric:t-mem");

        // A subsequent crash poll (srv1 still crashing) must NOT retract/resolve the metric alert even though
        // its id is not in the watchdog's present/firingNow sets — the crash pass owns only crash: ids.
        engine.Tick([Crashing("srv1")], T0 + Secs(1));
        Assert.Equal(2, engine.Firing.Count);
        Assert.Contains(engine.Firing, x => x.Id == "metric:t-mem");
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    // --- rules -------------------------------------------------------------------------------------
    private static ThresholdRule Mem(double warn, double? danger = null, int fireForSec = 60,
        int clearForSec = 120, double clearMargin = 5) =>
        new("t-mem", ThresholdMetric.HostMemUsedPct, warn, danger, fireForSec, clearForSec, clearMargin, Enabled: true);

    private static ThresholdRule Load(double warn, int fireForSec = 60) =>
        new("t-load", ThresholdMetric.HostLoadPerCore, warn, Danger: null, fireForSec, ClearForSec: 120,
            ClearMargin: 0.1, Enabled: true);

    private static ThresholdRule Disk(double warn, int fireForSec = 60) =>
        new("t-disk", ThresholdMetric.HostDiskUsedPct, warn, Danger: null, fireForSec, ClearForSec: 120,
            ClearMargin: 3, Enabled: true);

    private static ThresholdRule Pids(double warn, int clearForSec = 120, int fireForSec = 60) =>
        new("t-pids", ThresholdMetric.ServerPids, warn, Danger: null, fireForSec, clearForSec,
            ClearMargin: 50, Enabled: true);

    // --- snapshot builders -------------------------------------------------------------------------
    // A benign baseline: every metric well below any threshold. Tests override only the field under test.
    private static Snap.Snapshot Base() => new(
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
        Sensors: [new Snap.SensorReading("k10temp", "Tctl", 30.0)],
        Servers: []);

    private static Snap.Snapshot MemAt(double usedPct)
    {
        Snap.Snapshot b = Base();
        return b with { Mem = b.Mem with { UsedPct = usedPct } };
    }

    private static Snap.Snapshot WithServers(params Snap.ServerMetrics[] servers)
    {
        Snap.Snapshot b = Base();
        return b with { Servers = servers };
    }

    private static Snap.ServerMetrics Server(string id, int pids) =>
        new(id, id, "native", CpuPctCore: 10, MemBytes: 100, IoReadBps: null, IoWriteBps: null,
            Pids: pids, DiskBytes: null, RxBps: null, TxBps: null);

    private static TimeSpan Secs(int s) => TimeSpan.FromSeconds(s);

    // --- engine wiring (mirrors AlertEngineTests; the MonitorClient is never invoked — TickMetrics takes
    //     the snapshot as an argument) ---------------------------------------------------------------
    private static AlertEngine Engine(ApiOptions options) =>
        new(options, new StubProvider(), Monitor(options), Hub(), NullLogger<AlertEngine>.Instance);

    private static ApiOptions OptionsWith(params ThresholdRule[] rules)
    {
        var dict = new Dictionary<string, string?> { ["KGSM_API_HOST_ID"] = "hotrod" };
        for (int i = 0; i < rules.Length; i++)
        {
            ThresholdRule r = rules[i];
            string p = $"MetricsThresholds:Rules:{i}:";
            dict[p + "Key"] = r.Key;
            dict[p + "Metric"] = r.Metric.ToString();
            dict[p + "Warn"] = r.Warn.ToString(CultureInfo.InvariantCulture);
            // Always emit Danger (null when warn-only) — mirrors appsettings.json, which always carries the
            // key. Omitting it entirely makes the positional-record list binder fall back to Default.
            dict[p + "Danger"] = r.Danger is { } d ? d.ToString(CultureInfo.InvariantCulture) : null;
            dict[p + "FireForSec"] = r.FireForSec.ToString(CultureInfo.InvariantCulture);
            dict[p + "ClearForSec"] = r.ClearForSec.ToString(CultureInfo.InvariantCulture);
            dict[p + "ClearMargin"] = r.ClearMargin.ToString(CultureInfo.InvariantCulture);
            dict[p + "Enabled"] = r.Enabled ? "true" : "false";
        }
        return ApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

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
