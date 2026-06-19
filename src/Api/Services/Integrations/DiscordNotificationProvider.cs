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

    /// <summary>The Settings key holding the Discord role id to @-mention when a rule's <c>Ping</c> is on
    /// (provider-specific config — IntegrationRecord.Settings is exactly for this). Absent → no ping, even
    /// if the rule asks for one (we can't ping a role we don't have — honest, never invented).</summary>
    public const string PingRoleSetting = "pingRoleId";

    public async Task<NotificationTestResult> TestAsync(IntegrationRecord record, CancellationToken ct)
    {
        if (record.Secret is null)
            return new NotificationTestResult(false, null, record.ChannelLabel, "no webhook configured");

        var payload = new
        {
            content = "✅ KGSM Control Panel — test notification (your webhook is wired up correctly).",
            allowed_mentions = SuppressMentions, // a test never pings anyone
        };
        (bool ok, string? error) = await PostAsync(record.Secret, payload, ct).ConfigureAwait(false);
        return ok
            ? new NotificationTestResult(true, "test", record.ChannelLabel, null)
            : new NotificationTestResult(false, null, record.ChannelLabel, error);
    }

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationEvent ev, NotificationRule rule, IntegrationRecord record, CancellationToken ct)
    {
        if (record.Secret is null)
            return new NotificationDeliveryResult(false, "no webhook configured");

        (bool ok, string? error) = await PostAsync(record.Secret, BuildPayload(ev, rule, record), ct).ConfigureAwait(false);
        return new NotificationDeliveryResult(ok, error);
    }

    // The single Discord webhook POST — shared by Test and Send. Honest failure (Discord's status or an
    // unreachable webhook), never a fabricated ok. The secret (the webhook URL) is never logged here; the
    // typed client also has its loggers stripped at the DI seam (Startup .RemoveAllLoggers()).
    private async Task<(bool Ok, string? Error)> PostAsync(string webhook, object payload, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage resp = await http.PostAsJsonAsync(webhook, payload, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return (true, null);
            string detail = $"Discord returned {(int)resp.StatusCode} {resp.ReasonPhrase}";
            logger.LogDebug("discord webhook post failed: {Detail}", detail);
            return (false, detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "discord webhook post threw");
            return (false, "could not reach the Discord webhook");
        }
    }

    // Suppress every mention by default — a server name that happens to contain @everyone/@here must never
    // accidentally ping. parse:[] disables all auto-parsed mentions; a deliberate role ping re-allows ONLY
    // that one id (see BuildPayload). (Discord allowed_mentions semantics.)
    private static readonly object SuppressMentions = new { parse = Array.Empty<string>() };

    // Build the webhook payload for a real event. Optionally @-mentions the configured ops role when the
    // rule asks for it AND a role id is set; otherwise no ping. allowed_mentions is always set so a stray
    // @everyone in a message can't fire.
    private static object BuildPayload(NotificationEvent ev, NotificationRule rule, IntegrationRecord record)
    {
        string message = FormatMessage(ev);
        if (rule.Ping
            && record.Settings.TryGetValue(PingRoleSetting, out string? roleId)
            && !string.IsNullOrWhiteSpace(roleId))
        {
            return new
            {
                content = $"<@&{roleId}> {message}",
                allowed_mentions = new { parse = Array.Empty<string>(), roles = new[] { roleId } },
            };
        }
        return new { content = message, allowed_mentions = SuppressMentions };
    }

    // Render the Discord message for a catalog event. Keyed on the source action so a restart reads
    // distinctly from a fresh start; crash reuses the audit summary (it already carries the restart-count
    // detail: "… crashed — auto-restarting" / "… supervisor gave up after N restart(s)").
    private static string FormatMessage(NotificationEvent ev)
    {
        string server = string.IsNullOrEmpty(ev.ServerId) ? "a server" : ev.ServerId;
        return ev.Action switch
        {
            AuditAction.ServerStart => $"🟢 **{server}** is online",
            AuditAction.ServerRestart => $"🔄 **{server}** restarted",
            AuditAction.ServerStop => $"⚪ **{server}** went offline",
            AuditAction.ServerCrash => $"🔴 {ev.Summary}",
            AuditAction.ServerUpdate => $"⬆️ **{server}** was updated",
            AuditAction.ServerInstall => $"📦 **{server}** was installed",
            AuditAction.BackupCreate => $"💾 **{server}** backup created",
            _ => $"ℹ️ {ev.Summary}",
        };
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
