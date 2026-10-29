namespace TheKrystalShip.Api.Services.Transfers;

/// <summary>
/// Settles the transfers a stopped process left behind, on both sides.
/// </summary>
/// <remarks>
/// <para>
/// At startup the target side runs once (<see cref="TransferTarget.RecoverAsync"/>): a transfer this node
/// was running when it stopped cannot be resumed, because a part restarts from nothing and the job's
/// ticket lived only in memory.
/// </para>
/// <para>
/// The source side runs at startup and every <see cref="Interval"/>
/// (<see cref="TransferSource.RecoverAsync"/>): every lock this node holds is settled by asking its target,
/// which also covers a commit message the bus dead-lettered.
/// </para>
/// </remarks>
public sealed class TransferRecovery(
    TransferStore store,
    TransferSource source,
    TransferTarget target,
    ILogger<TransferRecovery> logger) : BackgroundService
{
    /// <summary>How often the source asks its targets about the locks it holds.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await store.LoadAsync(stoppingToken).ConfigureAwait(false);
            await target.RecoverAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "transfer recovery on the target side failed");
        }

        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await source.RecoverAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "transfer recovery on the source side failed; it runs again in {Interval}", Interval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }
}
