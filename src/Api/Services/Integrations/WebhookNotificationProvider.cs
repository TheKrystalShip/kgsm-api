using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// Shared base for the <b>webhook-secret-URL</b> family of notification providers (M8·c) — providers whose
/// stored secret <em>is</em> the POST endpoint (Discord, Slack). It captures the genuinely-common logic so a
/// new webhook provider is reduced to its specifics: the honest webhook POST, the <c>/test</c>+send
/// orchestration, the catalog⋈rules event overlay, and the secret-masking hint. A concrete provider supplies
/// only <see cref="ProviderId"/>, secret validation, the GET view shape, and the test/message payloads.
/// </summary>
/// <remarks>
/// The real cross-provider abstraction is <see cref="INotificationProvider"/>; this base is a convenience for
/// the webhook-URL subset. A provider with a different transport — e.g. Telegram, where the secret is a bot
/// <em>token</em>, the endpoint is fixed (<c>api.telegram.org/bot&lt;token&gt;/sendMessage</c>) and a
/// <c>chat_id</c> comes from settings (the secret is NOT the URL) — implements <see cref="INotificationProvider"/>
/// directly, not this base. So "the abstraction is validated" means the <em>webhook family</em> is; Telegram
/// is the next real test of the interface.
/// </remarks>
public abstract class WebhookNotificationProvider(HttpClient http, ILogger logger) : INotificationProvider
{
    public abstract string ProviderId { get; }

    public abstract bool TryNormalizeSecret(string raw, out string? normalized, out string? error);

    public abstract object Describe(IntegrationRecord record);

    /// <summary>The provider-shaped <c>/test</c> payload (e.g. Discord's <c>{content,allowed_mentions}</c>
    /// or Slack's <c>{text}</c>).</summary>
    protected abstract object TestPayload();

    /// <summary>The provider-shaped payload for a real event, honoring the rule (e.g. a ping).</summary>
    protected abstract object MessagePayload(NotificationEvent ev, NotificationRule rule, IntegrationRecord record);

    public async Task<NotificationTestResult> TestAsync(IntegrationRecord record, CancellationToken ct)
    {
        if (record.Secret is null)
            return new NotificationTestResult(false, null, record.ChannelLabel, "no webhook configured");
        (bool ok, string? error) = await PostAsync(record.Secret, TestPayload(), ct).ConfigureAwait(false);
        return ok
            ? new NotificationTestResult(true, "test", record.ChannelLabel, null)
            : new NotificationTestResult(false, null, record.ChannelLabel, error);
    }

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationEvent ev, NotificationRule rule, IntegrationRecord record, CancellationToken ct)
    {
        if (record.Secret is null)
            return new NotificationDeliveryResult(false, "no webhook configured");
        (bool ok, string? error) = await PostAsync(record.Secret, MessagePayload(ev, rule, record), ct).ConfigureAwait(false);
        return new NotificationDeliveryResult(ok, error);
    }

    /// <summary>The single webhook POST, shared by Test and Send. Honest failure — the provider's non-2xx
    /// status or an unreachable webhook, never a fabricated ok. (Discord returns <c>204</c>, Slack <c>200</c>
    /// with body <c>ok</c>; both are <c>IsSuccessStatusCode</c>.) The secret (the webhook URL) is never logged
    /// here; the typed client also has its loggers stripped at the DI seam (Startup <c>.RemoveAllLoggers()</c>).</summary>
    protected async Task<(bool Ok, string? Error)> PostAsync(string webhook, object payload, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage resp = await http.PostAsJsonAsync(webhook, payload, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return (true, null);
            string detail = $"{ProviderId} returned {(int)resp.StatusCode} {resp.ReasonPhrase}";
            logger.LogDebug("{Provider} webhook post failed: {Detail}", ProviderId, detail);
            return (false, detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "{Provider} webhook post threw", ProviderId);
            return (false, $"could not reach the {ProviderId} webhook");
        }
    }

    /// <summary>Overlay the user's stored rules onto the server-defined catalog → the GET view's
    /// <c>events[]</c>. Provider-agnostic (the catalog + rule model is shared), so it lives here.</summary>
    protected static IReadOnlyList<IntegrationEventView> EventViews(IntegrationRecord record)
    {
        var rules = record.Events.ToDictionary(e => e.Id, StringComparer.Ordinal);
        return NotificationCatalog.Events.Select(c =>
        {
            NotificationRule rule = rules.TryGetValue(c.Id, out NotificationRule? r) ? r : NotificationCatalog.DefaultRule(c.Id);
            return new IntegrationEventView(c.Id, c.Title, c.Description, rule.Enabled, rule.Cadence, rule.Ping);
        }).ToList();
    }

    /// <summary>Mask a webhook URL to a non-secret hint: the path from <paramref name="marker"/> onward with
    /// the final (secret) token truncated to its first 3 chars. Discord uses marker <c>webhooks</c>
    /// (<c>…/webhooks/{id}/{tok}***</c>), Slack <c>services</c> (<c>…/services/{team}/{app}/{tok}***</c>).
    /// <see langword="null"/> for an empty secret; <c>"***"</c> for an unparseable URL or one with fewer than
    /// two segments after the marker (never the raw secret).</summary>
    protected static string? MaskWebhook(string? url, string marker)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string[] segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int mi = Array.FindIndex(segs, s => string.Equals(s, marker, StringComparison.OrdinalIgnoreCase));
            if (mi >= 0 && segs.Length - (mi + 1) >= 2)
            {
                string[] tail = segs[(mi + 1)..];
                string token = tail[^1];
                tail[^1] = (token.Length <= 3 ? token : token[..3]) + "***";
                return $"…/{marker}/{string.Join('/', tail)}";
            }
        }
        return "***";
    }
}
