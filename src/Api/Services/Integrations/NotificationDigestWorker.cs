namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// Sends the summaries. Every so often it asks each provider's held batch whether the oldest thing in it
/// has waited long enough, and delivers it as one message if so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own loop, separate from <see cref="NotificationDeliveryWorker"/>.</b> That one is blocked on the
/// bus for the life of the process, which is right for it and useless here: a digest becomes due because
/// time passed, not because something happened, and on a quiet host nothing will happen to wake it.
/// </para>
/// <para>
/// <b>A batch is taken before it is sent.</b> A failed POST loses that summary rather than repeating it
/// every tick until the webhook comes back — a digest is a convenience, and the same message arriving
/// eight times is a worse failure than one that did not arrive. The failure is logged either way.
/// </para>
/// </remarks>
public sealed class NotificationDigestWorker(
    IServiceScopeFactory scopeFactory,
    IntegrationStore store,
    NotificationDigestStore digests,
    ILogger<NotificationDigestWorker> logger) : BackgroundService
{
    /// <summary>
    /// How often the batches are checked. Far shorter than the window itself, which only sets the
    /// resolution of "long enough" — a summary goes out within a few minutes of becoming due.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await FlushAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "digest flush failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    internal async Task FlushAsync(DateTimeOffset now, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IEnumerable<INotificationProvider> providers = scope.ServiceProvider.GetServices<INotificationProvider>();

        foreach (INotificationProvider provider in providers)
        {
            IReadOnlyList<Data.NotificationDigestEntity> due =
                await digests.TakeDueAsync(provider.ProviderId, now, ct).ConfigureAwait(false);
            if (due.Count == 0) continue;

            IntegrationRecord record = await store.GetAsync(provider.ProviderId, ct).ConfigureAwait(false);

            // Switched off, or its secret removed, since the batch was collected. The rows are already
            // gone, which is the right outcome: they were held for a channel that no longer exists.
            if (!record.Enabled || record.Secret is null)
            {
                logger.LogInformation(
                    "dropped a {Count}-item digest for {Provider} — the integration is no longer sending",
                    due.Count, provider.ProviderId);
                continue;
            }

            IReadOnlyList<NotificationEvent> events = due.Select(d => new NotificationEvent(
                d.CatalogId, d.Action, d.ServerId, d.Severity, d.Summary, d.Ts, d.Id)).ToList();

            // The rule for the batch's own event where they agree, so a ping set on that event still
            // applies; the catalog default otherwise, since a mixed batch has no one rule to honour.
            NotificationRule rule = RuleFor(events, record);

            NotificationDeliveryResult result =
                await provider.SendDigestAsync(events, rule, record, ct).ConfigureAwait(false);

            if (result.Ok)
                logger.LogInformation("sent a {Count}-item digest via {Provider}", due.Count, provider.ProviderId);
            else
                logger.LogWarning("digest send failed via {Provider} ({Count} items lost) — {Error}",
                    provider.ProviderId, due.Count, result.Error);
        }
    }

    private static NotificationRule RuleFor(IReadOnlyList<NotificationEvent> events, IntegrationRecord record)
    {
        string catalogId = events[0].CatalogId;
        bool uniform = events.All(e => string.Equals(e.CatalogId, catalogId, StringComparison.Ordinal));
        if (!uniform) return NotificationCatalog.DefaultRule("digest");

        return record.Events.FirstOrDefault(r => string.Equals(r.Id, catalogId, StringComparison.Ordinal))
            ?? NotificationCatalog.DefaultRule(catalogId);
    }
}
