using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Leaves;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Services.Metrics;

/// <summary>
/// The metrics persistence pump (M9 Increment 1): fetches the latest monitor snapshot at the configured
/// persist cadence (<c>KGSM_API_METRICS_PERSIST_MS</c>, default 15s — decoupled from the 1 Hz live
/// stream) and writes raw sample rows to <c>metrics.db</c>. Reuses the existing monitor scrape
/// (<see cref="MonitorClient.GetLatestAsync"/>), never a second socket connection.
/// <para><b>Honest gaps:</b> a null snapshot (monitor down) or a null metric field (io not accounted)
/// produces <em>no row</em> — never a zero, never a carried-forward stale value.</para>
/// </summary>
public sealed class MetricsSampler : BackgroundService
{
    private readonly MonitorClient _monitor;
    private readonly MetricsHistoryStore _store;
    private readonly ApiOptions _options;
    private readonly ILogger<MetricsSampler> _logger;
    private readonly TimeSpan _interval;

    public MetricsSampler(
        MonitorClient monitor,
        MetricsHistoryStore store,
        ApiOptions options,
        ILogger<MetricsSampler> logger)
    {
        _monitor = monitor;
        _store = store;
        _options = options;
        _logger = logger;

        int ms = options.MetricsPersistMs;
        _interval = TimeSpan.FromMilliseconds(ms);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("metrics sampler: started (interval={IntervalMs}ms, db={Db})",
            (int)_interval.TotalMilliseconds, _options.MetricsHistoryDb);

        await _store.EnsureCreatedAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Snap.Snapshot? snap = await _monitor.GetLatestAsync(stoppingToken).ConfigureAwait(false);
                    if (snap is null)
                    {
                        _logger.LogDebug("metrics sampler: monitor returned null (down/absent), skipping");
                        continue;
                    }

                    // Use the current time, not snap.Ts: the monitor's timestamp is when it
                    // measured, but the persist cadence is decoupled (15s vs 1Hz). Using UtcNow
                    // keeps samples aligned to the persist interval and recent (a stale cached
                    // frame with an old ts would otherwise write outside query windows).
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var rows = new List<MetricSample>();

                    MapHostMetrics(rows, _options.HostId, snap, ts);

                    foreach (Snap.ServerMetrics sm in snap.Servers)
                        MapServerMetrics(rows, sm, ts);

                    if (rows.Count > 0)
                    {
                        await _store.WriteSamplesAsync(rows, stoppingToken).ConfigureAwait(false);
                        _logger.LogInformation("metrics sampler: persisted {Count} rows (ts={Ts})", rows.Count, ts);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "metrics sampler: tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    internal static void MapServerMetrics(List<MetricSample> rows, Snap.ServerMetrics sm, long ts)
    {
        rows.Add(new MetricSample { EntityKind = "server", EntityId = sm.Id, Metric = "cpuPctCore", Ts = ts, Value = Math.Round(sm.CpuPctCore, 1) });
        rows.Add(new MetricSample { EntityKind = "server", EntityId = sm.Id, Metric = "memBytes", Ts = ts, Value = sm.MemBytes });
        if (sm.IoReadBps is { } ioR)
            rows.Add(new MetricSample { EntityKind = "server", EntityId = sm.Id, Metric = "ioReadBps", Ts = ts, Value = ioR });
        if (sm.IoWriteBps is { } ioW)
            rows.Add(new MetricSample { EntityKind = "server", EntityId = sm.Id, Metric = "ioWriteBps", Ts = ts, Value = ioW });
        rows.Add(new MetricSample { EntityKind = "server", EntityId = sm.Id, Metric = "pids", Ts = ts, Value = sm.Pids });
        if (sm.DiskBytes is { } disk)
            rows.Add(new MetricSample { EntityKind = "server", EntityId = sm.Id, Metric = "diskBytes", Ts = ts, Value = disk });
        // Network rx/tx — same honest-null semantics as io: persisted only when the meter
        // sourced them (native eBPF), absent (never 0) for an unmetered server.
        if (sm.RxBps is { } rx)
            rows.Add(new MetricSample { EntityKind = "server", EntityId = sm.Id, Metric = "rxBps", Ts = ts, Value = rx });
        if (sm.TxBps is { } tx)
            rows.Add(new MetricSample { EntityKind = "server", EntityId = sm.Id, Metric = "txBps", Ts = ts, Value = tx });
    }

    internal static void MapHostMetrics(List<MetricSample> rows, string hostId, Snap.Snapshot s, long ts)
    {
        rows.Add(new MetricSample { EntityKind = "host", EntityId = hostId, Metric = "cpuTotalPct", Ts = ts, Value = Math.Round(s.Cpu.TotalPct, 1) });
        rows.Add(new MetricSample { EntityKind = "host", EntityId = hostId, Metric = "memUsedKb", Ts = ts, Value = s.Mem.UsedKb });
        rows.Add(new MetricSample { EntityKind = "host", EntityId = hostId, Metric = "memTotalKb", Ts = ts, Value = s.Mem.TotalKb });
        rows.Add(new MetricSample { EntityKind = "host", EntityId = hostId, Metric = "memAvailableKb", Ts = ts, Value = s.Mem.AvailableKb });
        rows.Add(new MetricSample { EntityKind = "host", EntityId = hostId, Metric = "swapUsedKb", Ts = ts, Value = s.Mem.SwapUsedKb });
        rows.Add(new MetricSample { EntityKind = "host", EntityId = hostId, Metric = "loadOne", Ts = ts, Value = s.Cpu.Load.One });
        rows.Add(new MetricSample { EntityKind = "host", EntityId = hostId, Metric = "loadFive", Ts = ts, Value = s.Cpu.Load.Five });
        rows.Add(new MetricSample { EntityKind = "host", EntityId = hostId, Metric = "loadFifteen", Ts = ts, Value = s.Cpu.Load.Fifteen });

        if (s.Disk.Io is { } io)
        {
            rows.Add(new MetricSample { EntityKind = "host", EntityId = hostId, Metric = "diskReadBps", Ts = ts, Value = io.ReadBps });
            rows.Add(new MetricSample { EntityKind = "host", EntityId = hostId, Metric = "diskWriteBps", Ts = ts, Value = io.WriteBps });
        }
    }
}
