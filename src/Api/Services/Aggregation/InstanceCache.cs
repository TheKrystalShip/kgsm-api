using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// In-memory cache for the kgsm instance roster + run-state. Sits between consumers
/// (<see cref="ServerAggregator"/>, <see cref="Realtime.DomainPump"/>, <see cref="NetworkAggregator"/>)
/// and the kgsm engine's <see cref="IInstanceService"/> — two synchronous process spawns (~100ms each)
/// that would otherwise run on every <c>GET /servers</c> request, every DomainPump tick, and every
/// command-verify read.
/// </summary>
/// <remarks>
/// <para>
/// Pattern follows <see cref="Library.BlueprintCache"/>: a hand-rolled singleton
/// <see cref="IHostedService"/> with a <see cref="SemaphoreSlim"/> gate preventing concurrent refreshes.
/// A background <see cref="PeriodicTimer"/> refreshes every <see cref="ApiOptions.InstanceCacheTtlSeconds"/>
/// seconds; the first call triggers a synchronous initial load so the cache is populated before the first
/// request arrives.
/// </para>
/// <para>
/// Between background refreshes, kgsm lifecycle events (via <see cref="Audit.KgsmAuditConsumer"/>)
/// update the runtime status in-place — started/stopped/restarted/crashed/failed flip the
/// <see cref="Reading{T}"/> state without a process spawn. Install/uninstall events trigger an immediate
/// full refresh via <see cref="ScheduleRefresh"/>. The 60-second background refresh is the reconciliation
/// point that re-declares authoritative truth and fills in fields events cannot carry (PID, start time,
/// version, etc.).
/// </para>
/// <para>
/// On refresh failure the stale data is kept — a transient kgsm failure must not wipe the roster
/// (instances don't disappear from a transient read failure). An engine that was never configured
/// yields an empty cache — the same degrade as today.
/// </para>
/// </remarks>
public sealed class InstanceCache : IHostedService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<InstanceCache> _logger;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyDictionary<string, Instance> _roster = new Dictionary<string, Instance>();
    private IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> _statuses =
        new Dictionary<string, Reading<InstanceRuntimeStatus>>();
    private bool _engineRead = true;
    private PeriodicTimer? _timer;
    private CancellationToken _stoppingToken = CancellationToken.None;

    // Latch so a persistent engine misconfiguration is logged once, not on every refresh.
    private int _engineUnavailableLogged;

    public InstanceCache(IServiceProvider services, ApiOptions options, ILogger<InstanceCache> logger)
    {
        _services = services;
        _logger = logger;
        _ttl = TimeSpan.FromSeconds(Math.Max(10, options.InstanceCacheTtlSeconds));
    }

    /// <summary>
    /// The cached instance roster. Synchronous, lock-free read — safe on the hot path.
    /// Returns an empty dictionary until the first refresh completes.
    /// </summary>
    public IReadOnlyDictionary<string, Instance> Roster => _roster;

    /// <summary>
    /// The cached per-instance run-state readings. Synchronous, lock-free read.
    /// Returns an empty dictionary until the first refresh completes.
    /// </summary>
    public IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> Statuses => _statuses;

    /// <summary>
    /// Whether the last background refresh successfully read the engine. Callers that must distinguish
    /// "couldn't read" from "genuinely empty" (the 503/skip-tick decision) check this.
    /// </summary>
    public bool EngineRead => _engineRead;

    /// <summary>
    /// Trigger an immediate, non-blocking refresh. Returns <c>false</c> if a refresh is already in
    /// flight (the background timer or a prior manual trigger). Used by event handlers on
    /// install/uninstall to reconcile the roster immediately.
    /// </summary>
    public bool TryRefresh()
    {
        if (!_refreshLock.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(_stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Instance cache manual refresh failed.");
            }
            finally { _refreshLock.Release(); }
        });
        return true;
    }

    /// <summary>
    /// Update an instance's runtime status in-place from a kgsm lifecycle event. Preserves the
    /// existing reading's version/process/resource data when present; creates a minimal measured
    /// reading when none exists yet. This is the event-driven fast path — no process spawn.
    /// </summary>
    public void UpdateStatus(string instanceName, bool running)
    {
        if (string.IsNullOrEmpty(instanceName)) return;

        lock (_statuses)
        {
            var mutable = new Dictionary<string, Reading<InstanceRuntimeStatus>>(
                (Dictionary<string, Reading<InstanceRuntimeStatus>>)_statuses, StringComparer.Ordinal);

            if (mutable.TryGetValue(instanceName, out Reading<InstanceRuntimeStatus>? existing)
                && existing is { IsMeasured: true, Value: { } value })
            {
                // Preserve the existing reading's data; flip only the status bool.
                mutable[instanceName] = Reading<InstanceRuntimeStatus>.Measured(
                    value with { Status = running });
            }
            else
            {
                // No existing measured reading — create a minimal one with just the status.
                mutable[instanceName] = Reading<InstanceRuntimeStatus>.Measured(
                    new InstanceRuntimeStatus { InstanceName = instanceName, Status = running });
            }

            _statuses = mutable;
        }

        _logger.LogDebug("Instance cache: {Instance} status updated to {Status} (event-driven).",
            instanceName, running ? "running" : "stopped");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;
        _timer = new PeriodicTimer(_ttl);

        // Initial refresh — synchronous so the cache is populated before the first request arrives.
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RefreshAsync(cancellationToken).ConfigureAwait(false); }
        finally { _refreshLock.Release(); }

        // Background timer loop — runs for the process lifetime.
        _ = RunTimerAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    private async Task RunTimerAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_refreshLock.Wait(0)) continue; // skip if a refresh is already in flight
                try { await RefreshAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Instance cache background refresh failed; will retry next cadence.");
                }
                finally { _refreshLock.Release(); }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var instances = _services.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
        {
            if (Interlocked.Exchange(ref _engineUnavailableLogged, 1) == 0)
                _logger.LogWarning(
                    "kgsm engine is not configured (KGSM_API_KGSM_PATH is empty) — instance cache stays empty.");
            _engineRead = true; // honest empty roster, not a failed read
            return;
        }

        try
        {
            // GetAllOrNull distinguishes a FAILED read (null) from a genuine empty roster.
            // A failed read must NOT replace the cache — keep stale data.
            Dictionary<string, Instance>? roster = await Task.Run(() => instances.GetAllOrNull(), ct)
                .ConfigureAwait(false);
            if (roster is null)
            {
                _logger.LogWarning(
                    "Instance cache refresh: kgsm instance-roster read failed — keeping stale data.");
                _engineRead = false;
                return;
            }

            // fast: skip the per-instance network update-check (~20x faster).
            Dictionary<string, Reading<InstanceRuntimeStatus>> statuses =
                await Task.Run(() => instances.GetAllStatuses(fast: true), ct).ConfigureAwait(false);

            _roster = roster;
            _statuses = statuses;
            _engineRead = true;

            _logger.LogDebug("Instance cache refreshed: {Count} instance(s).", roster.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // On failure keep stale data — a transient kgsm failure must not wipe the roster.
            _logger.LogWarning(ex, "Instance cache refresh failed; keeping stale data ({Count} instance(s)).",
                _roster.Count);
            _engineRead = false;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _refreshLock.Dispose();
    }
}
