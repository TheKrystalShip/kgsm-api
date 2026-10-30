using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.Notifications;

using ActionIds = TheKrystalShip.Api.Services.Auth.ActionIds;

namespace TheKrystalShip.Api.Services.Notifications;

/// <summary>
/// Redeems a button on a notification: the operation behind a handle, performed as the person it was
/// minted for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The anonymous tap route is the only write without a session, and this is what keeps it sound.</b>
/// A service worker holds no session, so the library's handle stands in for a bearer: single-use, bound to
/// the device it was minted for, and short-lived. Nothing in the request describes what to do. Whether the
/// person may perform the operation's action is asked here, at the tap, of what their token last listed
/// (<see cref="NotificationActors"/>): a tap carries no token of its own, so somebody whose access was
/// taken away is refused once that memory is older than <see cref="NotificationActors.Freshness"/>.
/// </para>
/// <para>
/// <b>It writes no audit row of its own.</b> The operations are kgsm's events to emit, so this stamps the
/// actor and the reserved <c>notification</c> origin onto the engine call and the row is written from the
/// echo — a second writer for an action the engine already emits is what the audit model forbids.
/// </para>
/// </remarks>
public sealed class FleetNotificationActions(
    NotificationActors actors,
    ServerAggregator aggregator,
    PlayerHistoryService history,
    JobRegistry jobs,
    CommandRunner runner,
    IUnitController units,
    ApiJournal journal,
    IServiceProvider services,
    ILogger<FleetNotificationActions> logger) : INotificationActionHandler
{
    public async Task<(bool Ok, string Message)> RedeemAsync(
        string person, string name, string argument, CancellationToken ct)
    {
        if (!PushActionKind.IsKnown(name) || ButtonArgument.Unpack(argument) is not { } target)
            return Refuse("This build does not know how to do that.");

        if (actors.Known(person) is not { } known)
            return Refuse("This action could not be attributed to an account that was recently signed in.");

        string actor = known.Actor;

        bool Allows(string action) => actors.Allows(person, action);

        if (PushActionKind.VerbFor(name) is { } verb)
            return await LifecycleAsync(target.Target, verb, actor, Allows, ct).ConfigureAwait(false);

        if (PushActionKind.ModerationFor(name) is { } moderation)
            return await ModerateAsync(target, moderation, actor, Allows).ConfigureAwait(false);

        return name switch
        {
            PushActionKind.LeafRestart => await RestartLeafAsync(target.Target, actor, Allows, ct).ConfigureAwait(false),
            PushActionKind.SchedulePostpone => await PostponeAsync(target, actor, Allows, ct).ConfigureAwait(false),
            _ => Refuse("This build does not know how to do that."),
        };
    }

    /// <summary>
    /// Restart one of this host's own services. <c>api:services.manage</c>, like every other way of managing
    /// a leaf from the panel: it interrupts something the rest of the host depends on, and arriving from a
    /// lock screen changes nothing about that. A leaf outside <see cref="LeafCatalog.IsRestartable"/> is
    /// refused here rather than shelling a command the privilege underneath would deny.
    /// </summary>
    private async Task<(bool, string)> RestartLeafAsync(
        string leafId, string actor, Func<string, bool> allows, CancellationToken ct)
    {
        if (!allows(ActionIds.ServicesManage))
            return Refuse("That account is not allowed to restart a service.");

        if (!LeafCatalog.IsRestartable(leafId) || LeafCatalog.Find(leafId) is not { } leaf)
            return Refuse($"{leafId} is not a service this host can restart.");

        bool ok = await units.RestartAsync(leaf.Unit, ct).ConfigureAwait(false);

        // A row either way: a refused restart is a thing an operator needs to be able to find later, and
        // it is the case where nobody was watching a screen to see it fail.
        await journal.ServiceRestartedAsync(leaf.Id, leaf.DisplayName, leaf.Unit, ok, actor, AuditOrigin.Notification, ct)
            .ConfigureAwait(false);

        return ok
            ? (true, $"Asked systemd to restart {leaf.DisplayName}.")
            : Unavailable($"systemd would not restart {leaf.DisplayName}.");
    }

    /// <summary>
    /// Push one of a server's maintenance windows back an hour. <c>scheduler:windows.write</c>, as on the
    /// panel. The scheduler's control socket carries no identity, so the gate here is the only one there is.
    /// No audit row: a postponement changes no configuration and leaves no trace on the host. It names the
    /// window it moves, because the daemon refuses an instruction naming none.
    /// </summary>
    private async Task<(bool, string)> PostponeAsync(
        ButtonArgument button, string actor, Func<string, bool> allows, CancellationToken ct)
    {
        if (!allows(ActionIds.SchedulerWindowsWrite))
            return Refuse("That account is not allowed to change when a server restarts.");

        if (services.GetService(typeof(SchedulerClient)) is not SchedulerClient scheduler || !scheduler.CanControl)
            return Unavailable("This host is not wired to the scheduler.");

        if (button.Subject is not { Length: > 0 } window)
            return Refuse("That notification does not say which maintenance window to move.");

        SchedulerControlResponse result = await scheduler
            .PostponeAsync(button.Target, window, PushActionCatalog.PostponeBy, ct).ConfigureAwait(false);

        logger.LogInformation("notification action: postpone {Window} on {Server} — {Message} (actor={Actor})",
            window, button.Target, result.Message, actor);

        if (!result.Ok)
            return Refuse(Capitalize(result.Message) + ".");

        // The new time, in the words the person will read it in; the scheduler answers with it so a caller
        // never has to ask again.
        string when = result.NextFireUtc is { } next ? $" It runs at {next.ToLocalTime():HH:mm} instead." : "";
        return (true, $"{button.Target}'s {window} maintenance is pushed back an hour.{when}");
    }

    /// <summary>
    /// Run one lifecycle verb against the staged server, through the same gates the panel's own command
    /// path applies, in the same order: the verb's action at the server, the observed run state, and the
    /// one-in-flight claim. The state gate is not softened for arriving late — a person tapping Start on a
    /// server somebody else already started is told it is already running.
    /// </summary>
    private async Task<(bool, string)> LifecycleAsync(
        string serverId, string verb, string actor, Func<string, bool> allows, CancellationToken ct)
    {
        if (ActionIds.ForVerb(verb) is not { } verbAction
            || !allows(verbAction))
            return Refuse($"That account is not allowed to {verb} this server.");

        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct).ConfigureAwait(false);
        Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.Ordinal));
        if (server is null)
            return Refuse($"{serverId} is not on this host any more.");

        if (CommandGate.Inadmissible(verb, server.Status) is { } noop)
            return Refuse(Capitalize(noop) + ".");

        string jobId = "job_" + Guid.NewGuid().ToString("N")[..8];
        Job? job = jobs.TryStart(jobId, serverId, verb, DateTimeOffset.UtcNow);
        if (job is null)
            return Refuse($"Something is already running on {serverId}.");

        // Stamped, not written: every verb here is kgsm's event to emit, so the provenance rides the engine
        // call and the audit row comes from the echo.
        runner.Start(job, actor, AuditOrigin.Notification);
        logger.LogInformation("notification action: {Verb} {ServerId} job={JobId} (actor={Actor})",
            verb, serverId, job.Id, actor);

        // What has happened is that kgsm was asked. Whether the server ends up in that state is the job's
        // answer, and claiming it here would be reporting an outcome nobody has yet.
        return (true, $"Asked kgsm to {verb} {serverId}.");
    }

    // The gate's reasons are sentence fragments for an error envelope ("server is already stopped"); here
    // they are the whole message a person reads on a notification.
    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Remove one player from one server — the same resolution the panel's moderation route performs,
    /// re-performed at the tap rather than trusted from the moment the notification was written. The button
    /// names a roster key, never an identity to send: which field reaches the game is the blueprint's
    /// decision, read through <see cref="ModerationTargetResolver"/> against the roster record that key
    /// resolves to.
    /// </summary>
    private async Task<(bool, string)> ModerateAsync(
        ButtonArgument button, string moderation, string actor, Func<string, bool> allows)
    {
        string moderationAction = moderation == ModerationAction.Kick
            ? ActionIds.ServerPlayersKick
            : ActionIds.ServerPlayersBan;
        if (!allows(moderationAction))
            return Refuse($"That account is not allowed to {moderation} a player on this server.");

        if (string.IsNullOrEmpty(button.Subject))
            return Refuse("That notification did not name a player.");

        if (services.GetService(typeof(IInstanceService)) is not IInstanceService engine)
            return Unavailable("The kgsm engine is not readable on this host right now.");

        Instance? instance = engine.GetInstanceInfo(button.Target);
        if (instance is null)
            return Refuse($"{button.Target} is not on this host any more.");

        if (!history.TryGetPlayer(button.Target, button.Subject, out RosterPlayer player))
            return Refuse($"{button.Subject} is not on {button.Target}'s roster.");

        // A kick on somebody who already left is answered here rather than by the engine: the game would
        // refuse it as a 502 that reads like a fault, when the moment simply passed.
        if (moderation == ModerationAction.Kick && player.Status != PlayerStatus.online)
            return Refuse($"{Name(player)} is no longer connected.");

        string? template = moderation == ModerationAction.Kick ? instance.KickCommand : instance.BanCommand;
        ModerationTargetResolver.Failure failure =
            ModerationTargetResolver.TryResolve(template, player, out string target, out ModerationTargetKind kind);

        if (failure == ModerationTargetResolver.Failure.Unsupported)
            return Refuse($"This game declares no {moderation} command.");
        if (failure == ModerationTargetResolver.Failure.NoSuchIdentity)
            return Refuse($"This game moderates by {kind.ToString().ToLowerInvariant()}, which {Name(player)} has none of.");

        // Stamped, not written — kgsm emits player.kicked/.banned and the audit row comes off that echo.
        KgsmResult result = moderation == ModerationAction.Kick
            ? engine.Kick(button.Target, target, actor, AuditOrigin.Notification)
            : engine.Ban(button.Target, target, actor, AuditOrigin.Notification);

        if (!result.IsSuccess)
        {
            logger.LogWarning("notification action: {Action} {Player} on {Server} refused by the engine — {Error}",
                moderation, button.Subject, button.Target, result.Stderr);
            return Unavailable($"The engine refused the {moderation}.");
        }

        logger.LogInformation("notification action: {Action} {Player} on {Server} (actor={Actor})",
            moderation, button.Subject, button.Target, actor);

        return (true, $"{Name(player)} was {(moderation == ModerationAction.Kick ? "kicked" : "banned")}.");
    }

    // The name the person read on the notification, when the roster has one — an account id or an address
    // is what the game answers to, not what somebody recognises on a lock screen.
    private static string Name(RosterPlayer player) =>
        !string.IsNullOrWhiteSpace(player.PlayerName) ? player.PlayerName! : player.PlayerIdentity;

    private static (bool, string) Refuse(string message) => (false, message);

    private static (bool, string) Unavailable(string message) => (false, message);
}
