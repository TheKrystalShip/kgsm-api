namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The cluster bus's retention/GC worker (<c>docs/cluster-message-bus-plan.md §7</c> "Retention / GC")
/// — mirrors <see cref="Auth.SessionCleanupWorker"/> exactly: inert (early return, no timer) when
/// <see cref="ApiOptions.ClusterEnabled"/> is false, a startup catch-up pass, then a
/// <see cref="PeriodicTimer"/> loop (<see cref="ApiOptions.ClusterGcMs"/>) with a per-tick
/// <c>try/catch</c> swallowing non-cancellation exceptions (one bad tick must never kill the worker).
/// </summary>
/// <remarks>
/// <b>Inert-when-disabled, not "runs regardless."</b> The plan leaves this open either way — a disabled
/// cluster host would just find nothing to prune, so running anyway is harmless. This worker chooses the
/// inert posture purely for consistency with <see cref="OutboxDrainer"/> and
/// <see cref="Auth.SessionCleanupWorker"/> (an opt-in feature that isn't configured shouldn't spin a timer
/// that will always no-op), not because running unconditionally would be unsafe.
/// </remarks>
public sealed class ClusterBusGcWorker : BackgroundService
{
    private readonly ClusterBus _bus;
    private readonly ApiOptions _options;
    private readonly ILogger<ClusterBusGcWorker> _logger;

    public ClusterBusGcWorker(ClusterBus bus, ApiOptions options, ILogger<ClusterBusGcWorker> logger)
    {
        _bus = bus;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ClusterEnabled)
        {
            _logger.LogInformation("cluster bus GC inert — cluster not configured");
            return;
        }

        _logger.LogInformation(
            "cluster bus GC: started (interval={IntervalMs}ms, retention={RetentionDays}d)",
            _options.ClusterGcMs, _options.ClusterRetentionDays);

        // Startup catch-up pass, same reasoning as SessionCleanupWorker's.
        await RunGcAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.ClusterGcMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunGcAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "cluster bus GC: tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private Task RunGcAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset cutoff = now.AddDays(-_options.ClusterRetentionDays);
        return _bus.PruneAsync(cutoff, now, ct);
    }
}
