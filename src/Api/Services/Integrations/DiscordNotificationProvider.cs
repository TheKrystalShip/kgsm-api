using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// The Discord notification provider (M8·c) — a <see cref="WebhookNotificationProvider"/> (the webhook-URL
/// family). One-way outbound delivery via an <b>incoming webhook</b> (architecture.html §3·e): no bot
/// gateway, no slash-commands (that is kgsm-bot's interactive surface). Supplies only the Discord
/// specifics — the secret validation, the GET view (with <c>bot:null</c>), and the message payloads; the
/// POST + test/send orchestration + masking live in the base.
/// </summary>
public sealed class DiscordNotificationProvider(HttpClient http, ILogger<DiscordNotificationProvider> logger)
    : WebhookNotificationProvider(http, logger)
{
    /// <summary>The Settings key holding the Discord role id to @-mention when a rule's <c>Ping</c> is on
    /// (provider-specific config — IntegrationRecord.Settings is exactly for this). Absent → no ping, even
    /// if the rule asks for one (we can't ping a role we don't have — honest, never invented).</summary>
    public const string PingRoleSetting = "pingRoleId";

    public override string ProviderId => "discord";

    public override object Describe(IntegrationRecord record) =>
        new DiscordIntegrationView(
            Provider: ProviderId,
            Webhook: new WebhookView(record.Secret is not null, MaskWebhook(record.Secret, "webhooks")),
            ChannelLabel: record.ChannelLabel,
            Bot: null, // webhook-only — the two-way control bot is out of scope (kgsm-bot's), honestly null
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

    protected override object TestPayload() => new
    {
        content = "✅ KGSM Control Panel — test notification (your webhook is wired up correctly).",
        allowed_mentions = SuppressMentions, // a test never pings anyone
    };

    protected override object MessagePayload(NotificationEvent ev, NotificationRule rule, IntegrationRecord record)
    {
        string message = FormatMessage(ev);
        // Optionally @-mention the configured ops role when the rule asks for it AND a role id is set;
        // otherwise no ping. allowed_mentions is always set so a stray @everyone in a message can't fire.
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

    // Suppress every mention by default — a server name that happens to contain @everyone/@here must never
    // accidentally ping. parse:[] disables all auto-parsed mentions; a deliberate role ping re-allows ONLY
    // that one id (see MessagePayload). (Discord allowed_mentions semantics.)
    private static readonly object SuppressMentions = new { parse = Array.Empty<string>() };

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
}
