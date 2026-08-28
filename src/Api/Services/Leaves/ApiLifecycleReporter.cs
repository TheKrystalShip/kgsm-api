using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;
using TheKrystalShip.KGSM.Lifecycle;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// What this API says about its own state, to its own journal.
/// </summary>
/// <remarks>
/// <para>
/// The aggregator is a producer like any other, and the one component whose absence is most obvious
/// — a Control Panel that will not load says so by not loading. What is not obvious is the API
/// running and unable to do half its job: a database it cannot reach refuses every audit read and
/// every session check, and an engine it cannot run answers no question about a server, both while
/// the SPA is served perfectly from the same process.
/// </para>
/// <para>
/// <b>It reports on itself only.</b> Whether another leaf is well is
/// <see cref="LeafDegradationTracker"/>'s answer, read from that leaf's own journal — this API stating
/// something about a leaf in its own would be exactly the second answer able to disagree that the
/// producer-from-location rule exists to prevent.
/// </para>
/// </remarks>
public sealed class ApiLifecycleReporter(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    LeafLifecycle lifecycle,
    ILogger<ApiLifecycleReporter> logger) : BackgroundService
{
    /// <summary>How often this API re-reads its own dependencies.</summary>
    /// <remarks>
    /// Slow: both are conditions that persist, and the emitter reports only transitions, so a steady
    /// state costs nothing after the first line.
    /// </remarks>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Readiness is the host having started, which for this leaf really is the moment it can do its
        // job: the pipeline is listening and every hosted service is running. That is not true of the
        // leaves whose real work begins later — a supervisor joining its slice, a gateway connecting —
        // and it is why each of them hangs readiness off its own signal instead.
        lifetime.ApplicationStarted.Register(() => lifecycle.MarkReady("serving"));

        lifetime.ApplicationStopping.Register(() => lifecycle.MarkStopping(LeafStopReason.Signal));

        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await ReportAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ReportAsync(CancellationToken ct)
    {
        try
        {
            await ReportDatabaseAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Reporting must never take the surface down with it. A reading that threw is a reading
            // not taken, which the next tick retries.
            logger.LogDebug(ex, "could not read this API's own state");
        }
    }

    /// <summary>
    /// Whether the store this API keeps its own records in can be reached.
    /// </summary>
    /// <remarks>
    /// Its loss is not visible from outside. The SPA is still served, every leaf is still probed and
    /// every capability still reported — while no audit row can be read or written and no session can
    /// be checked, which is a Control Panel that looks entirely healthy and refuses everybody.
    /// </remarks>
    private async Task ReportDatabaseAsync(CancellationToken ct)
    {
        using IServiceScope scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
            lifecycle.MarkRecovered(ApiComponents.Database);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lifecycle.MarkDegraded(
                ApiComponents.Database,
                $"the API's own store cannot be reached ({ex.Message}); no audit row can be read or "
                + "written and no session can be checked, while every other surface reads healthy");
        }
    }
}

/// <summary>
/// The parts of this API's job that can stop working while it keeps serving.
/// </summary>
public static class ApiComponents
{
    /// <summary>The store this API keeps its audit log and session registry in.</summary>
    public const string Database = "database";
}
