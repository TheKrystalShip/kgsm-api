namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// Drains the <see cref="INotificationBus"/> and delivers each notifiable event to every enabled provider
/// at <c>every</c> cadence (M8·c Increment B). The bus is fed by the always-on audit append, so this fires
/// on real kgsm lifecycle events with <b>no new event-socket consumer</b> — it rides the existing audit
/// flow. Single drain loop; mirrors the always-on-hosted-service shape of the audit consumer / alert engine.
/// </summary>
/// <remarks>
/// <para><b>Routing gates (all must pass):</b> the provider is <c>Enabled</c> and has a secret (somewhere
/// to send), the per-event rule is <c>Enabled</c>, and its cadence is <c>every</c>. <c>once</c>/<c>digest</c>
/// are accepted in the A contract but their delivery is deferred to Increment C — they deliver <b>zero</b>
/// here (not fewer); the skip is logged at Information so a mis-set cadence is never a silent black hole.</para>
/// <para><b>Anti-spam suppression.</b> A crash-looping server re-emits <c>server.crash</c> every watchdog
/// backoff cycle; without a guard, the worker would fire a post each cycle and self-DoS the webhook
/// (an incoming webhook is rate-limited in the tens per minute) exactly when a server is dying. So a per-<c>(provider,subject,catalog)</c>
/// window (<see cref="SuppressWindow"/>) coalesces repeats: the first fires immediately, repeats inside the
/// window are skipped (logged). The window counts the <b>attempt</b>, not just a success, so a failing
/// webhook is not hammered either. <b>Honest boundary:</b> a mass reboot autostarts N servers → N
/// <em>distinct</em> keys → N posts (not suppressed) — bounded by the host's server count, accepted; only
/// the unbounded single-server loop is coalesced. Heavier shaping (digest/global rate-limit) is Increment C.
/// The subject is <see cref="NotificationEvent.SubjectKey"/> where the event names one — a threshold event
/// is about a watched condition rather than a server, and several of those share one host.</para>
/// <para><b>Threading.</b> <see cref="_lastSent"/> is touched ONLY by the single drain loop (the channel
/// has one reader) — no lock. Each event is dispatched in its own DI scope so the transient typed
/// <see cref="INotificationProvider"/> clients (disposable HttpClients) are disposed and the
/// IHttpClientFactory handler rotation stays honest.</para>
/// </remarks>
public sealed class NotificationDeliveryWorker(
    INotificationBus bus,
    IServiceScopeFactory scopeFactory,
    IntegrationStore store,
    NotificationDigestStore digests,
    ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    /// <summary>Minimum gap between two deliveries of the same <c>(provider,server,catalog)</c> — the
    /// anti-spam coalesce window. Comfortably longer than the watchdog's crash backoff so a crash loop
    /// pings once per window, not once per cycle.</summary>
    internal static readonly TimeSpan SuppressWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The same window, for a rule set to <c>once</c>: at most one per subject per day.
    /// </summary>
    /// <remarks>
    /// <b>Not once-ever.</b> A literal reading would mean the first crash a server ever had is the only
    /// one anybody is ever told about, and nothing would bring it back — the rule would quietly become a
    /// mute. A day is the span over which "this again" stops being news and starts being the same news.
    /// </remarks>
    internal static readonly TimeSpan OnceWindow = TimeSpan.FromHours(24);

    // key "provider:server:catalog" -> last delivery attempt. Single-thread access (the drain loop). Pruned
    // when it grows, so an uninstalled server's stale keys don't accumulate.
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (NotificationEvent ev in bus.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await DispatchAsync(ev, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad dispatch must never kill the drain loop.
                    logger.LogDebug(ex, "notification dispatch failed for {Action} {Server}", ev.Action, ev.ServerId);
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task DispatchAsync(NotificationEvent ev, DateTimeOffset now, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IEnumerable<INotificationProvider> providers = scope.ServiceProvider.GetServices<INotificationProvider>();

        foreach (INotificationProvider provider in providers)
        {
            IntegrationRecord record = await store.GetAsync(provider.ProviderId, ct).ConfigureAwait(false);
            if (!record.Enabled || record.Secret is null) continue; // off, or nowhere to send

            NotificationRule rule = record.Events
                .FirstOrDefault(r => string.Equals(r.Id, ev.CatalogId, StringComparison.Ordinal))
                ?? NotificationCatalog.DefaultRule(ev.CatalogId);
            if (!rule.Enabled) continue;

            // Held rather than sent. The batch goes out from FlushAsync once the oldest thing in it has
            // waited long enough, so nothing further happens for this event here.
            if (string.Equals(rule.Cadence, NotificationCadence.Digest, StringComparison.Ordinal))
            {
                await digests.AddAsync(provider.ProviderId, ev, ct).ConfigureAwait(false);
                logger.LogDebug("held '{Catalog}' for {Server} for the {Provider} digest",
                    ev.CatalogId, ev.ServerId, provider.ProviderId);
                continue;
            }

            // `once` is the same coalescing over a much longer window — the vocabulary's third value is a
            // question of how often, not of a different delivery path.
            TimeSpan window = string.Equals(rule.Cadence, NotificationCadence.Once, StringComparison.Ordinal)
                ? OnceWindow
                : SuppressWindow;

            string key = $"{provider.ProviderId}:{ev.SubjectKey ?? ev.ServerId}:{ev.CatalogId}";
            if (_lastSent.TryGetValue(key, out DateTimeOffset last) && now - last < window)
            {
                logger.LogDebug(
                    "suppressed '{Catalog}' for {Server} via {Provider} — last sent {Ago:n0}s ago (cadence '{Cadence}', window {Window:n0}s)",
                    ev.CatalogId, ev.ServerId, provider.ProviderId, (now - last).TotalSeconds,
                    rule.Cadence, window.TotalSeconds);
                continue;
            }

            // Count the attempt (not just a success) so a failing webhook is not hammered every event.
            _lastSent[key] = now;

            NotificationDeliveryResult result = await provider.SendAsync(ev, rule, record, ct).ConfigureAwait(false);
            if (result.Ok)
                logger.LogDebug("notified '{Catalog}' for {Server} via {Provider}",
                    ev.CatalogId, ev.ServerId, provider.ProviderId);
            else
                logger.LogWarning("notification send failed: '{Catalog}' for {Server} via {Provider} — {Error}",
                    ev.CatalogId, ev.ServerId, provider.ProviderId, result.Error);
        }

        PruneSuppression(now);
    }

    // Keep _lastSent bounded: once it grows past a threshold, drop entries older than the LONGEST window
    // that could still suppress something. Pruning at the short one would quietly turn every `once` rule
    // back into `every` on a busy host, which is the sort of bug that only shows up as "why am I being
    // spammed again".
    private void PruneSuppression(DateTimeOffset now)
    {
        if (_lastSent.Count < 512) return;
        foreach (string stale in _lastSent
                     .Where(kv => now - kv.Value > OnceWindow)
                     .Select(kv => kv.Key)
                     .ToList())
            _lastSent.Remove(stale);
    }
}
