using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// The Discord notification provider (M8·c) — the first <see cref="INotificationProvider"/>. One-way
/// outbound delivery via an <b>incoming webhook</b> (architecture.html §3·e): no bot gateway, no
/// slash-commands (that is kgsm-bot's interactive surface). The injected <see cref="HttpClient"/> is the
/// typed-client from <c>AddHttpClient</c> (the <c>DiscordIdentityResolver</c> pattern, 10s timeout).
/// </summary>
public sealed class DiscordNotificationProvider(HttpClient http, ILogger<DiscordNotificationProvider> logger)
    : INotificationProvider
{
    public string ProviderId => "discord";

    public object Describe(IntegrationRecord record)
    {
        var rules = record.Events.ToDictionary(e => e.Id, StringComparer.Ordinal);
        List<IntegrationEventView> events = NotificationCatalog.Events.Select(c =>
        {
            NotificationRule rule = rules.TryGetValue(c.Id, out NotificationRule? r) ? r : NotificationCatalog.DefaultRule(c.Id);
            return new IntegrationEventView(c.Id, c.Title, c.Description, rule.Enabled, rule.Cadence, rule.Ping);
        }).ToList();

        return new DiscordIntegrationView(
            Provider: ProviderId,
            Webhook: new WebhookView(record.Secret is not null, Hint(record.Secret)),
            ChannelLabel: record.ChannelLabel,
            Bot: null, // webhook-only — the two-way control bot is out of scope (kgsm-bot's), honestly null
            Enabled: record.Enabled,
            Events: events);
    }

    public bool TryNormalizeSecret(string raw, out string? normalized, out string? error)
    {
        normalized = null;
        string trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "the webhook must be an absolute https URL";
            return false;
        }
        bool discordHost = uri.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("discordapp.com", StringComparison.OrdinalIgnoreCase);
        // .../webhooks/{id}/{token}
        bool webhookPath = uri.AbsolutePath.Contains("/webhooks/", StringComparison.OrdinalIgnoreCase);
        if (!discordHost || !webhookPath)
        {
            error = "not a Discord webhook URL (expected https://discord.com/api/webhooks/<id>/<token>)";
            return false;
        }
        normalized = trimmed;
        error = null;
        return true;
    }

    public async Task<NotificationTestResult> TestAsync(IntegrationRecord record, CancellationToken ct)
    {
        if (record.Secret is null)
            return new NotificationTestResult(false, null, record.ChannelLabel, "no webhook configured");

        var payload = new { content = "✅ KGSM Control Panel — test notification (your webhook is wired up correctly)." };
        try
        {
            using HttpResponseMessage resp = await http.PostAsJsonAsync(record.Secret, payload, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return new NotificationTestResult(true, "test", record.ChannelLabel, null);

            // Honest failure — surface Discord's status, never fabricate an ok.
            string detail = $"Discord returned {(int)resp.StatusCode} {resp.ReasonPhrase}";
            logger.LogDebug("discord webhook test failed: {Detail}", detail);
            return new NotificationTestResult(false, null, record.ChannelLabel, detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "discord webhook test threw");
            return new NotificationTestResult(false, null, record.ChannelLabel, "could not reach the Discord webhook");
        }
    }

    /// <summary>Mask a webhook URL to a non-secret hint (architecture.html §3·e: <c>"…/webhooks/123…/abc***"</c>).
    /// Shows the (non-secret) webhook id and only the first 3 chars of the (secret) token.</summary>
    private static string? Hint(string? webhook)
    {
        if (string.IsNullOrEmpty(webhook)) return null;
        if (Uri.TryCreate(webhook, UriKind.Absolute, out Uri? uri))
        {
            string[] segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int wi = Array.FindIndex(segs, s => string.Equals(s, "webhooks", StringComparison.OrdinalIgnoreCase));
            if (wi >= 0 && segs.Length > wi + 2)
            {
                string id = segs[wi + 1];
                string token = segs[wi + 2];
                string head = token.Length <= 3 ? token : token[..3];
                return $"…/webhooks/{id}/{head}***";
            }
        }
        return "***";
    }
}
