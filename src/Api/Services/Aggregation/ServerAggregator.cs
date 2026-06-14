using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// Builds this host's <see cref="Server"/> list (architecture §3) for the M1·b read surface — the
/// project's central join: the kgsm engine's domain + run-state (via kgsm-lib) ⋈ the per-instance
/// metrics (via kgsm-monitor), keyed on the instance id. The roster is authoritative from kgsm-lib
/// (<see cref="IInstanceService.GetAll"/>); run-state/version come from
/// <see cref="IInstanceService.GetAllStatuses"/>; metrics are looked up by id in the monitor
/// snapshot. A server with no metrics row (monitor absent, or simply not running) gets
/// <c>metrics: null</c> — never a fabricated zero (the honesty invariant).
/// </summary>
/// <remarks>
/// The two kgsm-lib calls are synchronous process spawns (they shell <c>kgsm.sh</c>), so they run
/// on the thread pool concurrently with the async monitor scrape and never block the request thread.
/// <see cref="IInstanceService"/> is a <em>transient</em> kgsm-lib service, so it is resolved
/// per-call from the provider (the same optional-resolve pattern <c>HostAggregator</c> uses for the
/// watchdog client) rather than captured in this singleton. The engine is base, not a leaf: if it is
/// unconfigured/unregistered the list is empty and a misconfiguration is logged once — there is no
/// §4·b "engine" capability to render absent.
/// </remarks>
public sealed class ServerAggregator
{
    private readonly ApiOptions _options;
    private readonly MonitorClient _monitor;
    private readonly IServiceProvider _services;
    private readonly ILogger<ServerAggregator> _logger;

    // Latch so a persistent engine misconfiguration is logged once, not on every poll.
    private int _engineUnavailableLogged;

    public ServerAggregator(
        ApiOptions options,
        MonitorClient monitor,
        IServiceProvider services,
        ILogger<ServerAggregator> logger)
    {
        _options = options;
        _monitor = monitor;
        _services = services;
        _logger = logger;
    }

    /// <summary>Build the full server list for this host (the <c>GET /servers</c> body).</summary>
    public async Task<IReadOnlyList<Server>> GetServersAsync(CancellationToken ct)
    {
        // Domain read (blocking process spawns) and the metrics scrape run concurrently.
        Task<DomainReadout> domainTask = Task.Run(ReadDomain, ct);
        Task<Snap.Snapshot?> snapshotTask = _monitor.GetLatestAsync(ct);
        await Task.WhenAll(domainTask, snapshotTask).ConfigureAwait(false);

        return Join(domainTask.Result, snapshotTask.Result);
    }

    /// <summary>The kgsm-lib domain read: the instance roster + each instance's status reading.</summary>
    private DomainReadout ReadDomain()
    {
        // IInstanceService is transient and only registered when the engine is provisioned; resolve
        // optionally so an unconfigured engine degrades to an empty list rather than throwing.
        var instances = _services.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
        {
            if (Interlocked.Exchange(ref _engineUnavailableLogged, 1) == 0)
                _logger.LogWarning(
                    "kgsm engine is not configured (KGSM_API_KGSM_PATH is empty) — /servers will be empty.");
            return DomainReadout.Empty;
        }

        try
        {
            Dictionary<string, Instance> roster = instances.GetAll();
            // fast: skip the per-instance network update-check (~20x faster). Version.Current is still
            // reported; only Latest/UpdatesAvailable go null — which this DTO does not emit anyway.
            Dictionary<string, Reading<InstanceRuntimeStatus>> statuses = instances.GetAllStatuses(fast: true);

            if (roster.Count == 0)
                _logger.LogDebug("kgsm reported no instances (empty roster or a failed list command).");

            return new DomainReadout(roster, statuses);
        }
        catch (Exception ex)
        {
            // A list endpoint failing closed to empty is honest (it reports nothing, not a fabricated
            // value); surface it so an operator can see the engine read broke.
            _logger.LogWarning(ex, "kgsm domain read failed; serving an empty server list.");
            return DomainReadout.Empty;
        }
    }

    private IReadOnlyList<Server> Join(DomainReadout domain, Snap.Snapshot? snapshot)
    {
        // Index per-instance metrics by id (the monitor guarantees unique ids per tick).
        Dictionary<string, Snap.ServerMetrics> metricsById = new(StringComparer.Ordinal);
        if (snapshot is not null)
            foreach (Snap.ServerMetrics sm in snapshot.Servers)
                metricsById[sm.Id] = sm;

        var servers = new List<Server>(domain.Instances.Count);
        foreach ((string id, Instance instance) in domain.Instances)
        {
            // Run-state + version from the status reading; a non-measured/missing reading is "unknown".
            string status = ServerStatus.Unknown;
            string? version = null;
            if (domain.Statuses.TryGetValue(id, out Reading<InstanceRuntimeStatus>? reading)
                && reading is { IsMeasured: true, Value: { } runtimeStatus })
            {
                status = runtimeStatus.Status ? ServerStatus.Running : ServerStatus.Stopped;
                version = string.IsNullOrWhiteSpace(runtimeStatus.Version.Current)
                    ? null
                    : runtimeStatus.Version.Current;
            }

            // Metrics only when the monitor produced a row for this id; otherwise honest null.
            ServerMetricsDto? metrics = metricsById.TryGetValue(id, out Snap.ServerMetrics? m)
                ? new ServerMetricsDto(Math.Round(m.CpuPctCore, 1), m.MemBytes, m.IoReadBps, m.IoWriteBps, m.Pids)
                : null;

            servers.Add(new Server(
                Id: id,
                Name: string.IsNullOrWhiteSpace(instance.Name) ? id : instance.Name,
                Blueprint: CleanBlueprintId(instance),
                Status: status,
                Version: version,
                Runtime: instance.Runtime == InstanceRuntime.Container ? "container" : "native",
                HostId: _options.HostId,
                Metrics: metrics));
        }

        // Deterministic order so polling/diffing is stable.
        servers.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return servers;
    }

    // The clean blueprint id, e.g. "factorio" from ".../factorio.bp.yaml". Unified blueprints are
    // "<name>.bp.yaml", so strip that compound suffix deliberately — Path.GetFileNameWithoutExtension
    // (what Instance.Blueprint uses) only drops the last extension and would leave "factorio.bp".
    private static string CleanBlueprintId(Instance instance)
    {
        string file = Path.GetFileName(instance.BlueprintFile);
        foreach (string suffix in BlueprintSuffixes)
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return file[..^suffix.Length];

        // Fallback for an unexpected shape: the lib's own derivation (drops the last extension).
        return string.IsNullOrEmpty(instance.Blueprint) ? file : instance.Blueprint;
    }

    private static readonly string[] BlueprintSuffixes = [".bp.yaml", ".bp.yml"];

    /// <summary>The kgsm-lib domain read result: the instance roster and the per-instance status readings.</summary>
    private sealed record DomainReadout(
        IReadOnlyDictionary<string, Instance> Instances,
        IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> Statuses)
    {
        public static readonly DomainReadout Empty = new(
            new Dictionary<string, Instance>(),
            new Dictionary<string, Reading<InstanceRuntimeStatus>>());
    }
}
