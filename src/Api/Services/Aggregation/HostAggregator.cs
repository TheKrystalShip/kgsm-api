using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;
// Disambiguate from Microsoft.Extensions.Hosting.Host (pulled in by ImplicitUsings).
using Host = TheKrystalShip.Api.Contracts.Host;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// Builds this host's <see cref="Host"/> view (architecture §4·a/§4·b) for the M1·a read
/// surface. It performs ONE monitor scrape and derives both the capacity figures and the
/// metrics capability from that single snapshot (so they are always coherent), then probes
/// the watchdog and assistant capabilities concurrently. Every probe is independently bounded
/// — a hung or absent leaf degrades only its own capability and never stalls or 500s the
/// request (the degrade-gracefully invariant). No join with kgsm-lib domain data yet; that is
/// M1·b (<c>GET /servers</c>).
/// </summary>
public sealed class HostAggregator : IDisposable
{
    // Per-leaf probe budget. Independent of the watchdog client's own (150s) timeout, which is
    // sized for command drains — we never inherit it for a liveness probe on the /hosts path.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly ApiOptions _options;
    private readonly MonitorClient _monitor;
    private readonly IServiceProvider _services;
    private readonly ILogger<HostAggregator> _logger;
    private readonly HttpClient? _assistant;

    public HostAggregator(
        ApiOptions options,
        MonitorClient monitor,
        IServiceProvider services,
        ILogger<HostAggregator> logger)
    {
        _options = options;
        _monitor = monitor;
        _services = services;
        _logger = logger;

        if (options.AssistantProvisioned && Uri.TryCreate(options.AssistantBaseUrl, UriKind.Absolute, out Uri? baseUri))
            _assistant = new HttpClient { BaseAddress = baseUri, Timeout = ProbeTimeout };
    }

    /// <summary>Build the single host this api serves.</summary>
    public async Task<Host> GetHostAsync(CancellationToken ct)
    {
        Task<Snap.Snapshot?> snapshotTask = _monitor.GetLatestAsync(ct);
        Task<Capability> watchdogTask = ProbeWatchdogAsync(ct);
        Task<Capability> assistantTask = ProbeAssistantAsync(ct);
        await Task.WhenAll(snapshotTask, watchdogTask, assistantTask).ConfigureAwait(false);

        Snap.Snapshot? snapshot = snapshotTask.Result;

        var capabilities = new HostCapabilities(
            Metrics: MetricsCapability(snapshot),
            Assistant: assistantTask.Result,
            Watchdog: watchdogTask.Result);

        // Capacity is present iff we have a fresh snapshot; otherwise honest null ("not
        // measurable now"), which is exactly when the metrics capability is not operational.
        return new Host(
            Id: _options.HostId,
            Label: _options.HostLabel,
            Status: "online",
            CpuPct: snapshot is null ? null : Math.Round(snapshot.Cpu.TotalPct, 1),
            Mem: snapshot is null ? null : new MemCapacity(KibToGib(snapshot.Mem.UsedKb), KibToGib(snapshot.Mem.TotalKb)),
            Disks: snapshot is null ? null : MapDisks(snapshot.Disk.Mounts),
            Capabilities: capabilities);
    }

    private Capability MetricsCapability(Snap.Snapshot? snapshot)
    {
        if (!_options.MetricsProvisioned)
            return Capability.Absent;

        return snapshot is null
            ? Capability.Down(message: "Monitor unreachable or no sample produced yet.")
            : new Capability(
                Provisioned: true,
                Status: CapabilityStatus.Operational,
                Info: new MetricsCapabilityInfo(snapshot.IntervalMs));
    }

    private async Task<Capability> ProbeWatchdogAsync(CancellationToken ct)
    {
        if (!_options.WatchdogProvisioned)
            return Capability.Absent;

        // Registered only when provisioned (see Startup); resolve optionally to stay safe.
        var watchdog = _services.GetService(typeof(IWatchdogClient)) as IWatchdogClient;
        if (watchdog is null)
            return Capability.Absent;

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ProbeTimeout);
        try
        {
            bool ready = await watchdog.IsReadyAsync(timed.Token).ConfigureAwait(false);
            return ready
                ? new Capability(Provisioned: true, Status: CapabilityStatus.Operational)
                : Capability.Down(message: "Watchdog is not ready.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("watchdog readiness probe timed out after {Timeout}", ProbeTimeout);
            return Capability.Down(message: "Watchdog readiness probe timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "watchdog readiness probe failed");
            return Capability.Down(message: "Watchdog readiness probe failed.");
        }
    }

    private async Task<Capability> ProbeAssistantAsync(CancellationToken ct)
    {
        if (!_options.AssistantProvisioned || _assistant is null)
            return Capability.Absent;

        // Provisional liveness probe: any HTTP response means the assistant process is
        // reachable. The real readiness/health contract is defined with the SSE relay at M7.
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ProbeTimeout);
        try
        {
            using HttpResponseMessage _ = await _assistant.GetAsync("", timed.Token).ConfigureAwait(false);
            return new Capability(Provisioned: true, Status: CapabilityStatus.Operational);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("assistant probe timed out after {Timeout}", ProbeTimeout);
            return Capability.Down(message: "Assistant probe timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "assistant probe failed");
            return Capability.Down(message: "Assistant unreachable.");
        }
    }

    private static IReadOnlyList<DiskCapacity> MapDisks(Snap.MountUsage[] mounts)
    {
        var disks = new List<DiskCapacity>(mounts.Length);
        foreach (Snap.MountUsage m in mounts)
            disks.Add(new DiskCapacity(m.Mount, BytesToGib(m.UsedBytes), BytesToGib(m.TotalBytes)));
        return disks;
    }

    private static double KibToGib(long kib) => Math.Round(kib / 1048576.0, 2);

    private static double BytesToGib(long bytes) => Math.Round(bytes / 1073741824.0, 2);

    public void Dispose() => _assistant?.Dispose();
}
