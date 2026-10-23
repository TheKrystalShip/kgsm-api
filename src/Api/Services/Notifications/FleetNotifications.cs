using System.Collections.Concurrent;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Cluster;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.Notifications;

using ActionIds = TheKrystalShip.Api.Services.Auth.ActionIds;

namespace TheKrystalShip.Api.Services.Notifications;

/// <summary>
/// Tells the people who may read a fleet event about it, through the shared notification library: each
/// gets a row in their history and a push to their subscribed browsers.
/// </summary>
/// <remarks>
/// <para>
/// <b>A person is told only about what they may read.</b> An event about a server reaches the accounts that
/// hold <c>kgsm:server.read</c> there, and any other event reaches the accounts that hold
/// <c>api:audit.read</c> on this node — the record the event is a row of. A notification is a read, and a
/// person's switches only ever narrow what they may see. A host run with auth switched off tells its one
/// synthetic caller.
/// </para>
/// <para>
/// <b>The key is the occurrence.</b> The audit row's id for an event that has one, otherwise the event's
/// action, subject and time, so the library keeps one row per thing that happened and never repeats one.
/// </para>
/// <para>
/// <b>Repeats about one subject are coalesced.</b> A crash-looping server re-emits its crash every backoff
/// cycle; without a window it would wake every phone on the fleet once per cycle. The first is told, and
/// repeats of the same event about the same subject inside <see cref="CoalesceWindow"/> are not.
/// </para>
/// </remarks>
public sealed class FleetNotifications(
    INotifier notifier,
    MemberAccess memberAccess,
    NodeAccess nodeAccess,
    Aggregation.InstanceCache instances,
    ApiOptions options,
    ILogger<FleetNotifications> logger)
{
    /// <summary>The minimum gap between two notifications of one event about one subject.</summary>
    internal static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(60);

    /// <summary>The title every push carries.</summary>
    public const string PushTitle = "KGSM";

    /// <summary>Where a button's redemption is routed, below the API's public origin.</summary>
    public const string ActionsPath = "/api/v1/notifications/actions";

    // "subject:catalog" -> when it was last told. Pruned as it grows so an uninstalled server's keys do
    // not accumulate.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _told = new(StringComparer.Ordinal);

    /// <summary>The kinds this node tells: one per notification catalog event.</summary>
    public static IReadOnlyList<NotificationKind> Kinds { get; } =
        [.. NotificationCatalog.Events.Select(e =>
        {
            (string icon, string tone) = Style(e.Id);
            return new NotificationKind(e.Id, e.Title, icon, tone)
            {
                Description = e.Description,
                EnabledByDefault = NotificationCatalog.DefaultRule(e.Id).Enabled,
            };
        })];

    /// <summary>Tells everyone who may read <paramref name="ev"/>.</summary>
    public async Task TellAsync(NotificationEvent ev, DateTimeOffset now, CancellationToken ct)
    {
        if (Coalesced(ev, now))
            return;

        IReadOnlyList<string> people = await AudienceAsync(ev, ct).ConfigureAwait(false);
        if (people.Count == 0)
            return;

        NotificationDraft draft = DraftFor(ev, now);
        foreach (string person in people)
        {
            try
            {
                await notifier.TellAsync(person, draft, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One person's store failing must not keep the rest from hearing.
                logger.LogWarning(ex, "could not tell {Person} about {Catalog} for {Server}",
                    person, ev.CatalogId, ev.ServerId);
            }
        }
    }

    /// <summary>What a person is told about <paramref name="ev"/>.</summary>
    internal NotificationDraft DraftFor(NotificationEvent ev, DateTimeOffset now) => new()
    {
        Kind = ev.CatalogId,
        Key = KeyOf(ev),
        Text = Title(ev),
        Detail = string.Equals(ev.Summary, Title(ev), StringComparison.Ordinal) ? null : ev.Summary,
        Url = UrlOf(ev),
        Action = ActionWordsOf(ev),
        Buttons = ButtonsFor(ev, now),
    };

    internal static string KeyOf(NotificationEvent ev) =>
        ev.AuditId.Length > 0
            ? "audit:" + ev.AuditId
            : $"{ev.Action}:{ev.SubjectKey ?? ev.ServerId}:{ev.Ts.UtcTicks}";

    /// <summary>The panel path a row opens: a threshold goes to the alerts board, where the live view of
    /// the condition is; an event about a server to that server; everything else to the audit log.</summary>
    internal static string UrlOf(NotificationEvent ev)
    {
        string? server = string.IsNullOrEmpty(ev.ServerId) ? null : Uri.EscapeDataString(ev.ServerId);
        if (ev.CatalogId is "threshold_breach" or "threshold_clear")
            return server is null ? "/#/alerts" : $"/#/alerts?serverId={server}";

        return server is null ? "/#/audit" : $"/#/servers/{server}";
    }

    private static string ActionWordsOf(NotificationEvent ev) =>
        ev.CatalogId is "threshold_breach" or "threshold_clear"
            ? "Open alerts"
            : string.IsNullOrEmpty(ev.ServerId) ? "Open audit log" : "Open server";

    private IReadOnlyList<NotificationButton>? ButtonsFor(NotificationEvent ev, DateTimeOffset now)
    {
        IReadOnlyList<PushActionOffer> offers = PushActionCatalog.For(ev, ModerationFor(ev));
        if (offers.Count == 0)
            return null;

        DateTimeOffset expires = now.Add(PushActionCatalog.ButtonLifetime);
        return [.. offers.Select(o => new NotificationButton(
            o.Label, o.Kind, ButtonArgument.Pack(o.Target, o.Subject), expires))];
    }

    /// <summary>
    /// What the event's game declares it can do to a player, read off the instance the cache already holds.
    /// </summary>
    /// <remarks>
    /// Null for every event that is not about a person, and null when the engine is not readable or does not
    /// know this instance — which the catalog treats as "no moderation", the same as a game that declares
    /// none. That is the deliberate direction to fail in: a button offered on a guess ends with somebody
    /// removed from a game on the strength of an assumption.
    /// </remarks>
    private ModerationCapability? ModerationFor(NotificationEvent ev)
    {
        if (ev.CatalogId != "player_join" || string.IsNullOrEmpty(ev.ServerId))
            return null;

        return instances.Roster.TryGetValue(ev.ServerId!, out KGSM.Core.Models.Instance? instance)
            ? Players.ModerationTargetResolver.Describe(instance)
            : null;
    }

    private bool Coalesced(NotificationEvent ev, DateTimeOffset now)
    {
        string key = $"{ev.SubjectKey ?? ev.ServerId}:{ev.CatalogId}";
        if (_told.TryGetValue(key, out DateTimeOffset last) && now - last < CoalesceWindow)
        {
            logger.LogDebug("coalesced '{Catalog}' for {Server} — told {Ago:n0}s ago",
                ev.CatalogId, ev.ServerId, (now - last).TotalSeconds);
            return true;
        }

        _told[key] = now;
        if (_told.Count >= 512)
        {
            foreach (string stale in _told.Where(kv => now - kv.Value > CoalesceWindow).Select(kv => kv.Key).ToList())
                _told.TryRemove(stale, out _);
        }

        return false;
    }

    /// <summary>
    /// The accounts that may read what <paramref name="ev"/> is about, evaluated from this node's replica as
    /// it stands. None while the replica cannot be read: what a person would be told cannot be checked.
    /// </summary>
    private async Task<IReadOnlyList<string>> AudienceAsync(NotificationEvent ev, CancellationToken ct)
    {
        if (options.AuthDisabled)
            return [options.DisabledAuthActor];

        if (await memberAccess.EvaluatorAsync(ct).ConfigureAwait(false) is not { } evaluator)
            return [];

        bool aboutServer = !string.IsNullOrEmpty(ev.ServerId);
        string action = aboutServer ? ActionIds.ServerRead : ActionIds.AuditRead;
        AccessScope target = aboutServer ? nodeAccess.ServerTarget(ev.ServerId!) : nodeAccess.NodeTarget;

        return [.. evaluator.Snapshot.Accounts.Values
            .Where(a => a.Kind == AccountKind.Person
                && a.Status == AccountStatus.Active
                && evaluator.Allows(a.AccountId, action, target).Allowed)
            .Select(a => a.AccountId)];
    }

    /// <summary>The line a person reads: what happened, to what.</summary>
    internal static string Title(NotificationEvent ev)
    {
        string server = string.IsNullOrEmpty(ev.ServerId) ? "A server" : ev.ServerId;

        // The events the catalog id separates but the action does not. A crash and a give-up are one audit
        // action told apart by severity, and the title is where that distinction has to land — "crashed"
        // under a notification that means "it is not coming back" would be the wrong headline.
        switch (ev.CatalogId)
        {
            case "crash_loop": return $"{server} gave up";
            case "player_join": return $"Someone joined {server}";
            case "server_empty": return $"{server} is empty";

            // These name no server, so the summary carries the whole subject and the title is the
            // headline — the same shape a host-scope threshold takes.
            case "restart_soon": return $"{server} restarts shortly";
            case "leaf_down": return "A service went down";
            case "leaf_up": return "A service came back";
            case "leaf_degraded": return "A service is degraded";
            case "leaf_recovered": return "A service recovered";
            case "awaiting_approval": return "Someone is waiting to be let in";
            case "reactor_offer": return "A rule is waiting for your answer";
        }

        return ev.Action switch
        {
            // A threshold event's own summary is the detail (which sensor, which number), so the title is
            // the headline. It says "this host" rather than naming one: a phone reads this off the lock
            // screen with no idea which node sent it, and the detail carries the subject.
            var a when a == KgsmEventCatalog.NameOf<HostThresholdBreachedData>() => string.IsNullOrEmpty(ev.ServerId)
                ? "Host threshold crossed"
                : $"{ev.ServerId} threshold crossed",
            var a when a == KgsmEventCatalog.NameOf<HostThresholdClearedData>() => string.IsNullOrEmpty(ev.ServerId)
                ? "Host back to normal"
                : $"{ev.ServerId} back to normal",
            var a when a == KgsmEventCatalog.NameOf<InstanceStartedData>() => $"{server} is online",
            var a when a == KgsmEventCatalog.NameOf<InstanceRestartedData>() => $"{server} restarted",
            var a when a == KgsmEventCatalog.NameOf<InstanceStoppedData>() => $"{server} went offline",
            var a when a == KgsmEventCatalog.NameOf<InstanceCrashedData>() => $"{server} crashed",
            var a when a == KgsmEventCatalog.NameOf<InstanceFailedData>() => $"{server} crashed",
            var a when a == KgsmEventCatalog.NameOf<InstanceVersionUpdatedData>() => $"{server} was updated",
            var a when a == KgsmEventCatalog.NameOf<InstanceUpdateAvailableData>() => $"{server} has an update",
            var a when a == KgsmEventCatalog.NameOf<InstanceInstalledData>() => $"{server} was installed",
            var a when a == KgsmEventCatalog.NameOf<InstanceBackupCreatedData>() => $"{server} was backed up",
            _ => server,
        };
    }

    // The design system's icon and tone for each kind's rows.
    private static (string Icon, string Tone) Style(string id) => id switch
    {
        "online" => ("power", "success"),
        "offline" => ("power-off", "neutral"),
        "crash" => ("triangle-alert", "danger"),
        "crash_loop" => ("circle-x", "danger"),
        "update" => ("circle-check", "success"),
        "update_available" => ("download", "info"),
        "installed" => ("package-plus", "success"),
        "backup" => ("archive", "teal"),
        "threshold_breach" => ("gauge", "warning"),
        "threshold_clear" => ("gauge", "success"),
        "player_join" => ("user-plus", "info"),
        "server_empty" => ("ghost", "neutral"),
        "leaf_down" => ("unplug", "danger"),
        "leaf_up" => ("plug", "success"),
        "leaf_degraded" => ("triangle-alert", "warning"),
        "leaf_recovered" => ("circle-check", "success"),
        "restart_soon" => ("timer", "warning"),
        "awaiting_approval" => ("user-check", "info"),
        "reactor_offer" => ("zap", "info"),
        _ => ("bell", "neutral"),
    };
}
