using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// In-memory cache for the kgsm blueprint catalog. Sits between consumers
/// (<see cref="LibraryAggregator"/>, <see cref="LibraryHydrationWorker"/>) and the kgsm engine's
/// <see cref="IBlueprintService"/> — a synchronous process spawn (~3s) that would otherwise run
/// on every <c>GET /library</c> request and every hydration sweep.
/// </summary>
/// <remarks>
/// <para>
/// Pattern follows <see cref="Leaves.MonitorClient"/>: a hand-rolled cache with a
/// <see cref="SemaphoreSlim"/> gate preventing concurrent refreshes. A background
/// <see cref="PeriodicTimer"/> refreshes every <see cref="ApiOptions.BlueprintCacheTtlSeconds"/>
/// seconds; the first call triggers an on-demand load so the catalog is populated before the first
/// request returns.
/// </para>
/// <para>
/// On refresh failure the stale data is kept (blueprints don't disappear from a transient kgsm
/// failure). An engine that was never configured yields an empty catalog — the same degrade as today.
/// </para>
/// </remarks>
public sealed class BlueprintCache : IHostedService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BlueprintCache> _logger;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyDictionary<string, Blueprint> _cached = new Dictionary<string, Blueprint>();
    private long _lastRefreshTicks;
    private PeriodicTimer? _timer;
    private CancellationToken _stoppingToken = CancellationToken.None;

    // Latch so a persistent engine misconfiguration is logged once, not on every refresh.
    private int _engineUnavailableLogged;

    public BlueprintCache(IServiceProvider services, ApiOptions options, ILogger<BlueprintCache> logger)
    {
        _services = services;
        _logger = logger;
        _ttl = TimeSpan.FromSeconds(Math.Max(10, options.BlueprintCacheTtlSeconds));
    }

    /// <summary>
    /// The cached blueprint catalog. Synchronous, lock-free read — safe on the hot path.
    /// Returns an empty dictionary until the first refresh completes.
    /// </summary>
    public IReadOnlyDictionary<string, Blueprint> GetAll() => _cached;

    /// <summary>
    /// Trigger an immediate, non-blocking refresh. Returns <c>false</c> if a refresh is already in
    /// flight (the background timer or a prior manual trigger).
    /// </summary>
    public bool TryRefresh()
    {
        if (!_refreshLock.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(_stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Manual blueprint cache refresh failed.");
            }
            finally { _refreshLock.Release(); }
        });
        return true;
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
                    _logger.LogWarning(ex, "Blueprint cache background refresh failed; will retry next cadence.");
                }
                finally { _refreshLock.Release(); }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var blueprints = _services.GetService(typeof(IBlueprintService)) as IBlueprintService;
        if (blueprints is null)
        {
            if (Interlocked.Exchange(ref _engineUnavailableLogged, 1) == 0)
                _logger.LogWarning(
                    "kgsm engine is not configured (KGSM_API_KGSM_PATH is empty) — blueprint cache stays empty.");
            return;
        }

        try
        {
            Dictionary<string, Blueprint> catalog = await Task.Run(() => blueprints.ListDetailed(), ct)
                .ConfigureAwait(false);

            _cached = catalog;
            _lastRefreshTicks = Environment.TickCount64;

            _logger.LogDebug("Blueprint cache refreshed: {Count} blueprint(s).", catalog.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // On failure keep stale data — a transient kgsm failure must not wipe the catalog.
            _logger.LogWarning(ex, "Blueprint cache refresh failed; keeping stale data ({Count} blueprint(s)).",
                _cached.Count);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _refreshLock.Dispose();
    }
}
