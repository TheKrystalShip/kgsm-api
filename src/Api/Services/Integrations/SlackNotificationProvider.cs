using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// The Slack notification provider — a <see cref="WebhookNotificationProvider"/>. One-way outbound
/// delivery via a Slack <b>incoming webhook</b> (<c>https://hooks.slack.com/services/…</c>): the secret IS
/// the URL, so it shares the base's POST/test/send/masking and supplies only the Slack specifics — the host
/// validation, the GET view, and the mrkdwn message payload.
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

    /// <summary>
    /// One message listing what was held back, headed by a line saying how much and over what span.
    /// </summary>
    /// <remarks>
    /// Each line is the event's own summary, verbatim through the same escaping a single message gets —
    /// rewriting them here would give one fact two wordings depending on the cadence somebody chose. Past
    /// <see cref="NotificationDigestStore.MaxListed"/> the rest are counted rather than named: a Slack
    /// message listing forty crashes is a wall nobody reads, and silently dropping them would be worse
    /// than saying how many there were.
    /// </remarks>
    protected override object DigestPayload(
        IReadOnlyList<NotificationEvent> events, NotificationRule rule, IntegrationRecord record)
    {
        string head = SlackEscape(NotificationDigest.Headline(events));
        IEnumerable<string> lines = events.Take(NotificationDigestStore.MaxListed)
            .Select(e => "• " + SlackEscape(e.Summary));
        string tail = events.Count > NotificationDigestStore.MaxListed
            ? $"\n…and {events.Count - NotificationDigestStore.MaxListed} more"
            : "";

        string message = $"🗒️ *{head}*\n{string.Join("\n", lines)}{tail}";
        return Ping(message, rule, record);
    }

    protected override object MessagePayload(NotificationEvent ev, NotificationRule rule, IntegrationRecord record) =>
        Ping(FormatMessage(ev), rule, record);

    // Optionally mention the configured ops user-group when the rule asks AND a subteam id is set; the
    // subteam id is admin-supplied config (a structural mention), so it is not escaped — the message text
    // already is, by whichever caller built it.
    private static object Ping(string message, NotificationRule rule, IntegrationRecord record)
    {
        if (rule.Ping
            && record.Settings.TryGetValue(PingSubteamSetting, out string? subteam)
            && !string.IsNullOrWhiteSpace(subteam))
            return new { text = $"<!subteam^{subteam}> {message}" };
        return new { text = message };
    }

    // Slack mrkdwn: *bold* (single asterisk). The server name / summary are escaped (Slack parses <…> as
    // links/mentions and treats & specially), so a server name can never smuggle in markup or a mention.
    private static string FormatMessage(NotificationEvent ev)
    {
        string server = SlackEscape(string.IsNullOrEmpty(ev.ServerId) ? "a server" : ev.ServerId);
        return ev.Action switch
        {
            var a when a == KgsmEventCatalog.NameOf<InstanceStartedData>() => $"🟢 *{server}* is online",
            var a when a == KgsmEventCatalog.NameOf<InstanceRestartedData>() => $"🔄 *{server}* restarted",
            var a when a == KgsmEventCatalog.NameOf<InstanceStoppedData>() => $"⚪ *{server}* went offline",
            var a when a == KgsmEventCatalog.NameOf<InstanceCrashedData>() => $"🔴 {SlackEscape(ev.Summary)}",
            var a when a == KgsmEventCatalog.NameOf<InstanceFailedData>() => $"🔴 {SlackEscape(ev.Summary)}",
            var a when a == KgsmEventCatalog.NameOf<InstanceVersionUpdatedData>() => $"⬆️ *{server}* was updated",
            var a when a == KgsmEventCatalog.NameOf<InstanceInstalledData>() => $"📦 *{server}* was installed",
            var a when a == KgsmEventCatalog.NameOf<InstanceBackupCreatedData>() => $"💾 *{server}* backup created",
            // The summary already names the sensor, the metric and the number, and it is the same sentence
            // the audit trail carries — rephrasing it here would give one fact two wordings.
            var a when a == KgsmEventCatalog.NameOf<HostThresholdBreachedData>() => $"🌡️ {SlackEscape(ev.Summary)}",
            var a when a == KgsmEventCatalog.NameOf<HostThresholdClearedData>() => $"✅ {SlackEscape(ev.Summary)}",
            _ => $"ℹ️ {SlackEscape(ev.Summary)}",
        };
    }

    // Slack requires &, <, > escaped in message text (https://api.slack.com/reference/surfaces/formatting#escaping).
    // & first so the &amp; it produces isn't re-escaped by the < / > passes.
    private static string SlackEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
