using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The wire contracts for outbound-notification integrations (architecture.html §3·e, M8·c). camelCase
/// like the rest of the surface. The webhook secret is never on the wire — read returns a masked
/// <see cref="WebhookView.Hint"/>; PATCH accepts a full URL (write-only).
/// </summary>
// GET /integrations — one row per registered provider + whether it is configured/on (no secrets).
public sealed record IntegrationSummary(string Provider, bool Configured, bool Enabled);

/// <summary>The webhook block: whether a secret is set, and a masked hint (never the URL).</summary>
public sealed record WebhookView(bool Configured,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint);

/// <summary>One catalog event ⋈ the user's rule (architecture.html §3·e <c>events[]</c> + the
/// server-defined catalog title/description).</summary>
public sealed record IntegrationEventView(
    string Id, string Title, string Description, bool Enabled, string Cadence, bool Ping);

/// <summary>The Discord two-way control-bot block. <b>Always null in M8·c</b> — this integration is
/// one-way webhook delivery only; the control bot + slash-commands are kgsm-bot's interactive surface
/// (a deliberate, frozen honesty boundary). The shape exists so the field reads as a real "webhook-only"
/// signal rather than being absent.</summary>
public sealed record BotConnection(bool Connected, string? OpsRole);

/// <summary>GET/PATCH /integrations/discord — the §3·e record. <see cref="Bot"/> is null (webhook-only).</summary>
public sealed record DiscordIntegrationView(
    string Provider,
    WebhookView Webhook,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChannelLabel,
    BotConnection? Bot,
    bool Enabled,
    IReadOnlyList<IntegrationEventView> Events);

/// <summary>GET/PATCH /integrations/slack — Slack's webhook-only record (M8·c Increment C). <b>No <c>bot</c>
/// block:</b> Slack incoming webhooks have no Discord-style two-way control bot, so inventing one would be
/// dishonest — the frontend renders per <c>provider</c>. Same masked-webhook + catalog-events shape otherwise.</summary>
public sealed record SlackIntegrationView(
    string Provider,
    WebhookView Webhook,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChannelLabel,
    bool Enabled,
    IReadOnlyList<IntegrationEventView> Events);

/// <summary>POST /integrations/{provider}/test — 202 on a real send (architecture.html §3·e).</summary>
public sealed record IntegrationTestResponse(bool Ok, string Posted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChannelLabel);

/// <summary>PATCH /integrations/{provider} — a sparse update. Only the present fields change.
/// <see cref="Webhook"/> sets/rotates the secret (a blank string clears it).</summary>
public sealed record IntegrationPatch(
    bool? Enabled,
    string? ChannelLabel,
    string? Webhook,
    IReadOnlyList<EventRulePatch>? Events,
    IReadOnlyDictionary<string, string>? Settings);

/// <summary>One sparse event-rule change in a PATCH — <see cref="Id"/> required, the rest optional.</summary>
public sealed record EventRulePatch(string Id, bool? Enabled, string? Cadence, bool? Ping);
