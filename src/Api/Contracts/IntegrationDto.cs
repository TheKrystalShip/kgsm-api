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

/// <summary>GET/PATCH /integrations/slack — Slack's webhook-only record. Every provider here delivers
/// one way, over an incoming webhook: an interactive two-way surface is a bot, and the one bot this
/// ecosystem ships is kgsm-bot, which owns its own connection and its own announcement switches.</summary>
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
