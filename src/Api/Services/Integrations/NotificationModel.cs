using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// The typed, in-memory view of one provider's stored integration config (the de-serialized
/// <see cref="Data.IntegrationEntity"/>). Provider-agnostic: <see cref="Settings"/> holds any
/// provider-specific keys so the persisted shape doesn't grow a per-provider schema.
/// </summary>
public sealed record IntegrationRecord(
    string Provider,
    bool Enabled,
    string? Secret,
    string? ChannelLabel,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<NotificationRule> Events,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>The honest "nothing configured yet" record for a provider (no secret, off, no rules).</summary>
    public static IntegrationRecord Empty(string provider) =>
        new(provider, false, null, null,
            new Dictionary<string, string>(), [], null);
}

/// <summary>One per-event routing rule (architecture.html §3·e <c>events[]</c>): for a catalog event id,
/// whether to post it, how loudly (<see cref="Cadence"/>), and whether to @-mention the ops role.</summary>
public sealed record NotificationRule(string Id, bool Enabled, string Cadence, bool Ping);

/// <summary>The cadence vocabulary (architecture.html §3·e). <see cref="Every"/> is enforced from M8·c
/// Increment B; <see cref="Once"/>/<see cref="Digest"/> are accepted in the contract but their delivery
/// enforcement is deferred (Increment C) — accepted-but-inert, the M8·b reserved-field pattern.</summary>
public static class NotificationCadence
{
    public const string Every = "every";
    public const string Once = "once";
    public const string Digest = "digest";

    public static bool IsKnown(string? cadence) => cadence is Every or Once or Digest;
}

/// <summary>A server-defined catalog event (architecture.html §3·e): the events Discord can announce.
/// The user only configures <see cref="NotificationRule"/> over this fixed catalog.</summary>
public sealed record CatalogEvent(string Id, string Title, string Description);

/// <summary>
/// The server-defined notification catalog (architecture.html §3·e). <b>Honest:</b> only events the API
/// can actually source/deliver are listed — <c>resource</c> (no CPU/RAM/disk threshold-alert source is
/// built) and <c>join</c> (no player tracking) are deliberately omitted, never faked. They join the
/// catalog when an honest source lands.
/// </summary>
public static class NotificationCatalog
{
    public static readonly IReadOnlyList<CatalogEvent> Events =
    [
        new("online", "Server online", "A server came up and is running (server.start / server.restart)."),
        new("offline", "Server offline", "A server stopped (server.stop)."),
        new("crash", "Server crash", "The watchdog detected a server exited unexpectedly (server.crash)."),
        new("update", "Game updated", "A new game build was applied (server.update)."),
        new("installed", "Game installed", "A new server was installed (server.install)."),
        new("backup", "Backup created", "A server backup completed (backup.create)."),
    ];

    public static bool IsKnown(string id) =>
        Events.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    /// <summary>The default rule for a catalog event the user hasn't configured: enabled, every, no ping.</summary>
    public static NotificationRule DefaultRule(string id) => new(id, Enabled: true, NotificationCadence.Every, Ping: false);

    /// <summary>
    /// Map an <see cref="AuditAction"/> (the always-on audit row, M8·c Increment B) to the catalog event
    /// the providers route on, or <see langword="null"/> when the action is not notifiable (the common
    /// case — <c>auth.*</c>, <c>network.*</c>, <c>server.uninstall</c>, <c>backup.restore</c> have no
    /// catalog event, so they are dropped before they ever reach the bus). This is the one place the audit
    /// vocabulary and the notification catalog meet. <b>Note:</b> both <c>server.start</c> AND
    /// <c>server.restart</c> map to <c>online</c> — a completed restart means the server is up, so the
    /// watchdog's autonomous crash-restart (<c>instance_restarted</c> → <c>server.restart</c>) delivers the
    /// "back online" signal that pairs with its crash, not a silent gap.
    /// </summary>
    public static string? CatalogIdForAction(string action) => action switch
    {
        AuditAction.ServerStart => "online",
        AuditAction.ServerRestart => "online",
        AuditAction.ServerStop => "offline",
        AuditAction.ServerCrash => "crash",
        AuditAction.ServerUpdate => "update",
        AuditAction.ServerInstall => "installed",
        AuditAction.BackupCreate => "backup",
        _ => null,
    };
}

/// <summary>The outcome of a provider's <c>/test</c> send. <see cref="Ok"/> is honest — a real send that
/// failed reports <see cref="Error"/>, never a fabricated success.</summary>
public sealed record NotificationTestResult(bool Ok, string? Posted, string? ChannelLabel, string? Error);

/// <summary>
/// One notifiable fact, derived from an audit row, en route to the providers (M8·c Increment B). Lean and
/// provider-agnostic: it carries what a provider needs to <em>route</em> (the <see cref="CatalogId"/> →
/// the user's rule) and to <em>render</em> a message — never the audit row itself (the bus is decoupled
/// from the audit contract). <see cref="Action"/> is the source <see cref="AuditAction"/> so a provider
/// can phrase a nuance (a restart vs a fresh start) while the rule lookup still keys on the catalog id.
/// </summary>
public sealed record NotificationEvent(
    string CatalogId,
    string Action,
    string? ServerId,
    string Severity,
    string Summary,
    DateTimeOffset Ts,
    string AuditId);

/// <summary>The outcome of one provider <c>SendAsync</c> (M8·c Increment B). Honest like
/// <see cref="NotificationTestResult"/> — a real failure reports <see cref="Error"/>, never a faked ok.</summary>
public sealed record NotificationDeliveryResult(bool Ok, string? Error);

/// <summary>
/// The thin provider seam (M8·c). One implementation per channel (Discord first; Slack/Telegram later —
/// the abstraction gets validated/adjusted when provider #2 actually lands). Resolved by
/// <see cref="ProviderId"/> from the registered <c>IEnumerable&lt;INotificationProvider&gt;</c>.
/// </summary>
public interface INotificationProvider
{
    /// <summary>The provider id used in the route (<c>/integrations/{provider}</c>) and config key.</summary>
    string ProviderId { get; }

    /// <summary>Render the provider-shaped GET view from the stored config — secret masked to a hint,
    /// never echoed (architecture.html §3·e). Returns a wire DTO the controller serializes.</summary>
    object Describe(IntegrationRecord record);

    /// <summary>Validate/normalize a candidate secret (a webhook URL) on PATCH. False + <paramref name="error"/>
    /// on a malformed value → the controller returns a 400 with that detail; never store a bogus secret.</summary>
    bool TryNormalizeSecret(string raw, out string? normalized, out string? error);

    /// <summary>POST /test — actually send a test message through the configured secret. Honest: a real
    /// failure (or no secret) returns <see cref="NotificationTestResult.Ok"/> false, never a faked ok.</summary>
    Task<NotificationTestResult> TestAsync(IntegrationRecord record, CancellationToken ct);

    /// <summary>Deliver a real notification for <paramref name="ev"/> through the configured secret,
    /// honoring the per-event <paramref name="rule"/> (e.g. an ops-role ping). Called by the
    /// <c>NotificationDeliveryWorker</c> (Increment B). Honest: a real failure returns
    /// <see cref="NotificationDeliveryResult.Ok"/> false + an error, never a faked ok.</summary>
    Task<NotificationDeliveryResult> SendAsync(
        NotificationEvent ev, NotificationRule rule, IntegrationRecord record, CancellationToken ct);
}
