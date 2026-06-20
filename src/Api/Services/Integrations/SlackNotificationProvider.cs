using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// The Slack notification provider (M8·c Increment C) — the second <see cref="WebhookNotificationProvider"/>,
/// added to validate the webhook-family abstraction. One-way outbound delivery via a Slack
/// <b>incoming webhook</b> (<c>https://hooks.slack.com/services/…</c>): the secret IS the URL, exactly like
/// Discord, so it shares the base's POST/test/send/masking and supplies only the Slack specifics — the host
/// validation, the GET view (no <c>bot</c> block — Slack incoming webhooks have no Discord-style two-way
/// control bot, so inventing one would be dishonest), and the mrkdwn message payload.
/// </summary>
public sealed class SlackNotificationProvider(HttpClient http, ILogger<SlackNotificationProvider> logger)
    : WebhookNotificationProvider(http, logger)
{
    /// <summary>The Settings key holding the Slack user-group (subteam) id to mention when a rule's
    /// <c>Ping</c> is on. Absent → no ping even if the rule asks (we can't ping a group we don't have —
    /// honest, never invented). Mention syntax is <c>&lt;!subteam^ID&gt;</c>.</summary>
    public const string PingSubteamSetting = "pingSubteamId";

    public override string ProviderId => "slack";

    public override object Describe(IntegrationRecord record) =>
        new SlackIntegrationView(
            Provider: ProviderId,
            Webhook: new WebhookView(record.Secret is not null, MaskWebhook(record.Secret, "services")),
            ChannelLabel: record.ChannelLabel,
            Enabled: record.Enabled,
            Events: EventViews(record));

    public override bool TryNormalizeSecret(string raw, out string? normalized, out string? error)
    {
        normalized = null;
        string trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "the webhook must be an absolute https URL";
            return false;
        }
        // Incoming webhooks are always hooks.slack.com/services/T.../B.../X... — exact host (not EndsWith,
        // which would also match e.g. notslack.com).
        bool slackHost = string.Equals(uri.Host, "hooks.slack.com", StringComparison.OrdinalIgnoreCase);
        bool servicesPath = uri.AbsolutePath.Contains("/services/", StringComparison.OrdinalIgnoreCase);
        if (!slackHost || !servicesPath)
        {
            error = "not a Slack incoming-webhook URL (expected https://hooks.slack.com/services/<T>/<B>/<token>)";
            return false;
        }
        normalized = trimmed;
        error = null;
        return true;
    }

    protected override object TestPayload() =>
        new { text = "✅ KGSM Control Panel — test notification (your Slack webhook is wired up correctly)." };

    protected override object MessagePayload(NotificationEvent ev, NotificationRule rule, IntegrationRecord record)
    {
        string message = FormatMessage(ev);
        // Optionally mention the configured ops user-group when the rule asks AND a subteam id is set; the
        // subteam id is admin-supplied config (a structural mention), so it is not escaped — the message
        // text is (below).
        if (rule.Ping
            && record.Settings.TryGetValue(PingSubteamSetting, out string? subteam)
            && !string.IsNullOrWhiteSpace(subteam))
            return new { text = $"<!subteam^{subteam}> {message}" };
        return new { text = message };
    }

    // Slack mrkdwn: *bold* (single asterisk). The server name / summary are escaped (Slack parses <…> as
    // links/mentions and treats & specially) — the Slack analog of Discord's allowed_mentions care.
    private static string FormatMessage(NotificationEvent ev)
    {
        string server = SlackEscape(string.IsNullOrEmpty(ev.ServerId) ? "a server" : ev.ServerId);
        return ev.Action switch
        {
            AuditAction.ServerStart => $"🟢 *{server}* is online",
            AuditAction.ServerRestart => $"🔄 *{server}* restarted",
            AuditAction.ServerStop => $"⚪ *{server}* went offline",
            AuditAction.ServerCrash => $"🔴 {SlackEscape(ev.Summary)}",
            AuditAction.ServerUpdate => $"⬆️ *{server}* was updated",
            AuditAction.ServerInstall => $"📦 *{server}* was installed",
            AuditAction.BackupCreate => $"💾 *{server}* backup created",
            _ => $"ℹ️ {SlackEscape(ev.Summary)}",
        };
    }

    // Slack requires &, <, > escaped in message text (https://api.slack.com/reference/surfaces/formatting#escaping).
    // & first so the &amp; it produces isn't re-escaped by the < / > passes.
    private static string SlackEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
