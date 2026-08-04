namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// M4·c Increment 8 — the session GC worker. A permanent storage bound on the <c>sessions</c> table:
/// on a timer, deletes every row whose <see cref="Data.SessionEntry.Expires"/> has passed — BOTH
/// revoked and non-revoked (expired is dead regardless of revocation; see
/// <see cref="SessionStore.DeleteExpiredAsync"/>) — so the table doesn't grow forever across a long
/// run. A startup catch-up pass (a host that was down for a while shouldn't wait a full interval to
/// start shedding rows), then a
/// <see cref="PeriodicTimer"/> loop with a per-tick <c>try/catch</c> that swallows non-cancellation
/// exceptions (one bad tick must never kill the worker — the next tick tries again).
/// </summary>
public sealed class SessionCleanupWorker : BackgroundService
{
    private readonly SessionStore _sessions;
    private readonly ApiOptions _options;
    private readonly ILogger<SessionCleanupWorker> _logger;

    public SessionCleanupWorker(
        SessionStore sessions,
        ApiOptions options,
        ILogger<SessionCleanupWorker> logger)
    {
        _sessions = sessions;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The master switch (Api__SessionsDisabled) makes the whole registry inert (M4·a
        // stateless-JWT posture, a debugging escape hatch) — no per-request check, no revoke
        // endpoints, and (here) no GC either: there's nothing writing rows for this to clean up, and
        // starting the timer anyway would just be a silent no-op loop. Return cleanly (no timer at
        // all) rather than looping and skipping each tick — a genuinely inert worker, not a spinning one.
        if (!_options.SessionsEnabled)
        {
            _logger.LogInformation("session GC inert — sessions disabled");
            return;
        }

        _logger.LogInformation("session GC: started (interval={IntervalMs}ms)", _options.SessionsGcMs);

        // Catch-up pass on startup (downtime = a backlog of expired rows, not backfilled — just swept
        // once immediately instead of waiting for the first timer tick).
        await RunCleanupAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.SessionsGcMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunCleanupAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "session GC: tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private Task RunCleanupAsync(CancellationToken ct) =>
        _sessions.DeleteExpiredAsync(DateTimeOffset.UtcNow, ct);
}
