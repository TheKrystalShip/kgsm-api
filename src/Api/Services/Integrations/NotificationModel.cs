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
        new("online", "Server online", "A server finished starting and is running (server.start)."),
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
}

/// <summary>The outcome of a provider's <c>/test</c> send. <see cref="Ok"/> is honest — a real send that
/// failed reports <see cref="Error"/>, never a fabricated success.</summary>
public sealed record NotificationTestResult(bool Ok, string? Posted, string? ChannelLabel, string? Error);

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
}
