namespace TheKrystalShip.Api.Services.Metrics;

/// <summary>
/// Periodic rollup + retention maintenance (M9 Increment 2): rolls up complete Tier-1 buckets into
/// Tier-2, prunes expired rows from both tiers, and reclaims disk via incremental vacuum. Runs once
/// at startup (catch-up after downtime — gaps are honest, not backfilled) then on a timer.
/// </summary>
public sealed class MetricsMaintenanceService : BackgroundService
{
    private readonly MetricsHistoryStore _store;
    private readonly ApiOptions _options;
    private readonly ILogger<MetricsMaintenanceService> _logger;

    public MetricsMaintenanceService(
        MetricsHistoryStore store,
        ApiOptions options,
        ILogger<MetricsMaintenanceService> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "metrics maintenance: started (interval={IntervalMs}ms, raw={RawH}h, rollup={RollD}d, step={StepM}min)",
            _options.MetricsMaintenanceMs, _options.MetricsRawRetentionHours,
            _options.MetricsRollupRetentionDays, _options.MetricsRollupStepMin);

        await _store.EnsureCreatedAsync(stoppingToken).ConfigureAwait(false);

        // Catch-up pass on startup (downtime = honest gaps, not backfilled).
        await RunMaintenanceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.MetricsMaintenanceMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunMaintenanceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "metrics maintenance: tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task RunMaintenanceAsync(CancellationToken ct)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _store.RollupAsync(_options.MetricsRollupStepMin, nowMs, ct).ConfigureAwait(false);

        long rawCutoff = nowMs - (_options.MetricsRawRetentionHours * 3_600_000L);
        await _store.PruneRawAsync(rawCutoff, ct).ConfigureAwait(false);

        long rollupCutoff = nowMs - (_options.MetricsRollupRetentionDays * 86_400_000L);
        await _store.PruneRollupsAsync(rollupCutoff, ct).ConfigureAwait(false);

        await _store.VacuumAsync(ct).ConfigureAwait(false);
    }
}
