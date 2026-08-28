using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Events;

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

/// <summary>How loudly a provider carries one catalog event (architecture.html §3·e).</summary>
public static class NotificationCadence
{
    /// <summary>Every occurrence, subject only to the delivery worker's anti-spam window.</summary>
    public const string Every = "every";

    /// <summary>
    /// At most one per subject per <see cref="NotificationDeliveryWorker.OnceWindow"/> — the same
    /// coalescing as <see cref="Every"/>, over a much longer window.
    /// </summary>
    /// <remarks>
    /// A window rather than a literal once-ever, because once-ever has no way back: the first crash a
    /// server had would be the only one anybody was ever told about. A day is the span over which "this
    /// again" stops being news and starts being the same news.
    /// </remarks>
    public const string Once = "once";

    /// <summary>
    /// Held and delivered as one summary, once the oldest thing waiting reaches
    /// <see cref="NotificationDigestStore.Window"/>.
    /// </summary>
    public const string Digest = "digest";

    public static bool IsKnown(string? cadence) => cadence is Every or Once or Digest;
}

/// <summary>A server-defined catalog event (architecture.html §3·e): the events a notification provider
/// can announce. The user only configures <see cref="NotificationRule"/> over this fixed catalog.</summary>
public sealed record CatalogEvent(string Id, string Title, string Description);

/// <summary>
/// The server-defined notification catalog (architecture.html §3·e). <b>Honest:</b> only events the API
/// can actually source/deliver are listed — <c>join</c> (no player tracking) is deliberately omitted,
/// never faked. It joins the catalog when an honest source lands.
/// <para>
/// <b>A breach and its recovery are two events, not one.</b> They are separate immutable facts in the
/// audit log and they are separately worth hearing about: plenty of people want the alarm and not the
/// all-clear. Splitting them is also what keeps the delivery worker's coalesce window from letting a
/// recovery suppress itself against the breach that preceded it inside the same window.
/// </para>
/// </summary>
public static class NotificationCatalog
{
    public static readonly IReadOnlyList<CatalogEvent> Events =
    [
        new("online", "Server online", "A server came up and is running (server.started / server.restarted)."),
        new("offline", "Server offline", "A server stopped (server.stopped)."),
        new("crash", "Server crash", "The watchdog detected a server exited unexpectedly and is restarting it (server.crashed)."),
        new("crash_loop", "Server gave up", "The watchdog exhausted its restart retries and left a server down (server.crash.exhausted)."),
        new("update", "Game updated", "A new game build was applied (server.updated)."),
        new("update_available", "Update available", "A new game version is available to install (server.update.available)."),
        new("installed", "Game installed", "A new server was installed (server.installed)."),
        new("backup", "Backup created", "A server backup completed (backup.created)."),
        new("threshold_breach", "Threshold crossed", "A metric the monitor watches went over its threshold and stayed there (host.threshold.breached)."),
        new("threshold_clear", "Threshold recovered", "A metric that was over its threshold came back down (host.threshold.cleared)."),
        new("player_join", "Player joined", "Somebody connected to a server this host can observe presence on (player.joined)."),
        new("server_empty", "Server sitting empty", "A running server has had nobody connected to it for a while."),
        new("leaf_down", "Service went down", "A KGSM service on this host stopped answering its health check and stayed that way."),
        new("leaf_up", "Service came back", "A KGSM service that was down is answering again."),
        new("restart_soon", "Scheduled restart due", "A running server is minutes away from its scheduled restart."),
        new("awaiting_approval", "Account awaiting approval", "Somebody signed in for the first time and cannot do anything until an admin approves them (user.provisioned)."),
        new("reactor_offer", "Reactor offer", "A reactor rule staged an action and is waiting for somebody to confirm or dismiss it (reactor.proposed)."),
    ];

    /// <summary>
    /// The two events whose rate is set by <em>other people</em> rather than by the fleet doing something,
    /// and which therefore arrive opt-in.
    /// </summary>
    /// <remarks>
    /// Every other event is bounded by what the host does: a server starts, crashes, gets backed up. These
    /// two are bounded by how popular a server is, and a busy evening is hundreds of joins. Defaulting them
    /// on would mean adding them silently changed what an already-configured host sends — so an admin turns
    /// them on deliberately, and each person can still switch them off again.
    /// </remarks>
    private static readonly HashSet<string> OptIn = new(StringComparer.Ordinal) { "player_join", "server_empty" };

    public static bool IsKnown(string id) =>
        Events.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    /// <summary>The default rule for a catalog event the user hasn't configured: <c>every</c>, no ping, and
    /// enabled unless the event is one of the <see cref="OptIn"/> pair.</summary>
    public static NotificationRule DefaultRule(string id) =>
        new(id, Enabled: !OptIn.Contains(id), NotificationCadence.Every, Ping: false);

    /// <summary>
    /// Which catalog event an audit row's action announces, or <see langword="null"/> when nothing
    /// announces it — the common case, and why <c>auth.*</c>, <c>network.*</c>, an uninstall and a
    /// backup restore are dropped before they ever reach the bus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is where an open vocabulary meets a closed one, and the asymmetry is the point.</b> A
    /// row renders from its own dimensions, so a leaf may name an event nothing here has heard of and
    /// the panel still draws it. A notification is different: somebody ticked a box against a catalog
    /// id and that consent is stored, so an event cannot enrol itself into being announced. An
    /// unmapped action is silent, which is the honest default for a thing nobody asked to hear about.
    /// </para>
    /// <para>
    /// Both a start and a completed restart mean the server is up, so both announce <c>online</c> —
    /// the watchdog's autonomous crash-restart therefore delivers the "back online" that pairs with
    /// its crash, rather than a silent gap.
    /// </para>
    /// </remarks>
    public static string? CatalogIdForAction(string action) => action switch
    {
        var a when a == KgsmEventCatalog.NameOf<InstanceStartedData>() => "online",
        var a when a == KgsmEventCatalog.NameOf<InstanceRestartedData>() => "online",
        var a when a == KgsmEventCatalog.NameOf<InstanceStoppedData>() => "offline",

        // The supervisor still trying, and the supervisor having given up, are two events and stay two
        // here. They are split because the coalesce window would otherwise hide the second: a give-up
        // arrives at the end of a run of crashes for the same server, inside the window they were
        // suppressed by, so the one crash notification a person most needs — "it is down and staying
        // down" — is the one they would not get.
        var a when a == KgsmEventCatalog.NameOf<InstanceCrashedData>() => "crash",
        var a when a == KgsmEventCatalog.NameOf<InstanceFailedData>() => "crash_loop",

        var a when a == KgsmEventCatalog.NameOf<InstancePlayerJoinedData>() => "player_join",
        var a when a == ApiJournal.UserProvisionedEvent => "awaiting_approval",
        var a when a == KgsmEventCatalog.NameOf<InstanceVersionUpdatedData>() => "update",
        var a when a == KgsmEventCatalog.NameOf<InstanceUpdateAvailableData>() => "update_available",
        var a when a == KgsmEventCatalog.NameOf<InstanceInstalledData>() => "installed",
        var a when a == KgsmEventCatalog.NameOf<InstanceBackupCreatedData>() => "backup",
        var a when a == KgsmEventCatalog.NameOf<HostThresholdBreachedData>() => "threshold_breach",
        var a when a == KgsmEventCatalog.NameOf<HostThresholdClearedData>() => "threshold_clear",

        // ⚠ The offer, and never its resolution. `reactor.proposed` is the only one of the reactor's
        // four events with anything for a person to do: a decision is a judgment nobody has to answer,
        // an action taken alone is already done, and a resolution is somebody having answered — which
        // would announce their own tap back to them. This is also the one event on this host whose
        // whole point is reaching somebody who is not looking, because an unanswered offer expires.
        var a when a == KgsmEventCatalog.NameOf<ReactorProposedEventData>() => "reactor_offer",

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
/// from the audit contract). <see cref="Action"/> is the event's own name so a provider can phrase a
/// nuance (a restart vs a fresh start) while the rule lookup still keys on the catalog id.
/// </summary>
/// <param name="SubjectKey">What this event is <em>about</em>, for coalescing repeats — the server for a
/// lifecycle event, the individual watched condition for a threshold one. It exists because
/// <see cref="ServerId"/> is not always the subject: every host-scope threshold carries a null server, so a
/// window keyed on the server would let a disk breach silently swallow a temperature breach that happened
/// a few seconds later. Null falls back to <see cref="ServerId"/>.</param>
/// <param name="ActionSubject">What a button on this event would act <em>on</em>, inside the event's own
/// scope — the roster's key for a player on a join, the account id on a provision, the leaf id on a health
/// flip. Distinct from <paramref name="SubjectKey"/>, which is only ever a coalescing key: this one is an
/// operand, and it is carried rather than re-derived because the rule that produces it (the roster's
/// identity precedence, say) lives in exactly one place, and a second opinion about who somebody is would
/// eventually act on the wrong one.</param>
/// <param name="ActionQualifier">Which part of <paramref name="ActionSubject"/> the button acts on, where
/// the operand is not the whole of it — the maintenance window a countdown is about, on a server that holds
/// several. Carried rather than re-derived at the tap for the same reason as the subject: the fact was true
/// when the notification was written, and re-reading the schedule an hour later could name a different
/// window than the one the person was warned about.</param>
public sealed record NotificationEvent(
    string CatalogId,
    string Action,
    string? ServerId,
    string Severity,
    string Summary,
    DateTimeOffset Ts,
    string AuditId,
    string? SubjectKey = null,
    string? ActionSubject = null,
    string? ActionQualifier = null);

/// <summary>
/// The <see cref="NotificationEvent.Action"/> values for facts <b>this API observes itself</b>, which no
/// producer's journal therefore names.
/// </summary>
/// <remarks>
/// <b>Nothing here is ever written to the audit log.</b> The audit trail records what the engine and this
/// API <em>did</em>; a server sitting empty is neither — it is a reading taken by watching two authorities
/// agree (the engine says running, the supervisor says nobody is connected). Writing it as an audit row
/// would put an observation in a record of actions. Providers switch on these like any other action.
/// </remarks>
public static class DerivedNotificationAction
{
    /// <summary>A running server with observable presence and nobody on it for the dwell
    /// (<c>IdleServerWatcher</c>).</summary>
    public const string ServerEmpty = "server.empty";

    /// <summary>A leaf's health probe has said <c>down</c> for longer than a restart takes
    /// (<c>LeafHealthWatcher</c>).</summary>
    public const string LeafDown = "leaf.down";

    /// <summary>A leaf that had been reported down is answering again.</summary>
    public const string LeafUp = "leaf.up";

    /// <summary>A running server is inside the warning window before a maintenance window that will
    /// interrupt the people on it (<c>ScheduledRestartWatcher</c>).</summary>
    public const string RestartSoon = "restart.soon";
}

/// <summary>The outcome of one provider <c>SendAsync</c> (M8·c Increment B). Honest like
/// <see cref="NotificationTestResult"/> — a real failure reports <see cref="Error"/>, never a faked ok.</summary>
public sealed record NotificationDeliveryResult(bool Ok, string? Error);

/// <summary>
/// The thin provider seam. One implementation per channel, resolved by <see cref="ProviderId"/> from the
/// registered <c>IEnumerable&lt;INotificationProvider&gt;</c>. Discord is not among them: it is kgsm-bot's
/// channel, and a second path to it from here would double-post every event.
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

    /// <summary>
    /// Deliver several held-back facts as one summary — the <c>digest</c> cadence. <paramref name="events"/>
    /// is never empty and is ordered oldest first.
    /// </summary>
    /// <remarks>
    /// Its own method rather than a loop over <see cref="SendAsync"/>, because the whole point is that it
    /// is <em>one</em> message: sending five would be the cadence the person did not choose. Honest like
    /// its sibling — a real failure returns an error, never a faked ok.
    /// </remarks>
    Task<NotificationDeliveryResult> SendDigestAsync(
        IReadOnlyList<NotificationEvent> events, NotificationRule rule, IntegrationRecord record,
        CancellationToken ct);
}
