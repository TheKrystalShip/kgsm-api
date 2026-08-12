using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Integrations.WebPush;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Redeems a button on a push notification.
/// </summary>
/// <remarks>
/// <para>
/// <b>This route is deliberately anonymous, and it is the only write that is.</b> A service worker holds
/// no session — it can read neither the access token in <c>sessionStorage</c> nor the refresh token in
/// <c>localStorage</c> — so there is no bearer to present and the handle stands in for one. That is a
/// real widening, and three things narrow it back:
/// </para>
/// <list type="bullet">
/// <item>The handle names one operation on one target, staged by this API. Nothing in the request
/// describes what to do, so there is nothing in it to poison — the assistant's confirmation model,
/// unchanged.</item>
/// <item>It is bound to the device it was staged for. The worker presents its own push endpoint, and a
/// handle without that endpoint redeems nothing.</item>
/// <item><b>The tier is resolved here, from the account store, not carried from staging time.</b> Somebody
/// demoted or switched off between the notification and the tap is refused, exactly as they would be on
/// any other request.</item>
/// </list>
/// <para>
/// <b>It writes no audit row of its own.</b> An update is kgsm's event to emit, so this stamps
/// <c>actor</c> and <c>origin</c> onto the engine call and the row is written from the echo — a second
/// writer for an action the engine already emits is the one thing the audit model forbids. The row reads
/// <c>origin: notification</c>, a reserved value no request may declare: a caller naming it would be
/// claiming to be a redemption this API performed. A snooze writes nothing anywhere, being a personal
/// preference like every other push preference.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/notifications/actions")]
[AllowAnonymous]
public sealed class NotificationActionsController(
    PushActionStore staged,
    PushSnoozeStore snoozes,
    PushSubscriptionStore subscriptions,
    Services.Auth.UserDirectory users,
    ServerAggregator aggregator,
    PlayerHistoryService history,
    JobRegistry jobs,
    CommandRunner runner,
    IUnitController units,
    ApiJournal journal,
    ApiOptions options,
    ILogger<NotificationActionsController> logger) : ControllerBase
{
    private string hostId => options.HostId;

    /// <summary>
    /// Redeem <paramref name="handle"/> for the device that presents its own endpoint.
    /// </summary>
    /// <remarks>
    /// Every refusal that is about the handle answers <c>404</c> with one message — unknown, expired,
    /// already used, wrong device. A caller cannot tell them apart and does not need to: none of them is
    /// an operation to run, and separate answers would let somebody probe which handles exist.
    /// </remarks>
    [HttpPost("{handle}")]
    public async Task<IActionResult> Redeem(string handle, [FromBody] PushActionRedeemRequest? body, CancellationToken ct)
    {
        PushActionEntity? action = await staged.TakeAsync(handle, body?.Endpoint, ct);
        if (action is null)
            return NotFound(new PushActionResult(false, "That notification has already been dealt with, or it has expired."));

        if (!KgsmActor.TryParse(action.UserHandle, out string provider, out string subject))
            return Refuse("This action could not be attributed to an account.");

        var identity = new KgsmIdentity(provider, subject, action.Username ?? subject, action.Username ?? subject, null, []);

        AuthorityAnswer authority;
        try
        {
            if (!users.Available)
                return Unavailable("The account store on this host cannot be read, so nothing can be authorized right now.");
            authority = await users.Authority.ResolveAsync(identity, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // "We could not ask" is a third answer and stays one — never a default grant, never a
            // denial dressed up as one.
            logger.LogError(e, "notification action: could not resolve authority for {Handle}", action.UserHandle);
            return Unavailable("The account store on this host could not be read, so nothing can be authorized right now.");
        }

        if (authority.Outcome == AuthorityOutcome.Disabled)
            return Refuse("That account has been switched off.");

        if (action.Kind == PushActionKind.ServerUpdateAll)
            return await UpdateAllAsync(action, identity, authority.Tier, ct);

        if (PushActionKind.VerbFor(action.Kind) is { } verb)
            return await LifecycleAsync(action, verb, identity, authority.Tier, ct);

        if (PushActionKind.ModerationFor(action.Kind) is { } moderation)
            return ModerateAsync(action, moderation, identity, authority.Tier);

        return action.Kind switch
        {
            PushActionKind.ConditionSnooze => await SnoozeAsync(action, ct),
            PushActionKind.LeafRestart => await RestartLeafAsync(action, identity, authority.Tier, ct),
            PushActionKind.SchedulePostpone => await PostponeAsync(action, identity, authority.Tier, ct),
            PushActionKind.UserApprove => await ApproveAsync(action, identity, authority.Tier, ct),
            _ => Refuse("This build does not know how to do that."),
        };
    }

    /// <summary>
    /// Restart one of this host's own services.
    /// </summary>
    /// <remarks>
    /// <b>Admin, like every other way of restarting a leaf from the panel.</b> It interrupts something the
    /// rest of the host depends on, and the fact that the request arrived from a lock screen changes
    /// nothing about that. The privilege underneath is the polkit rule scoped to exactly these units, so a
    /// leaf outside <see cref="LeafCatalog.IsRestartable"/> is refused here rather than shelling a command
    /// that would be denied.
    /// </remarks>
    private async Task<IActionResult> RestartLeafAsync(
        PushActionEntity action, KgsmIdentity identity, KgsmTier tier, CancellationToken ct)
    {
        if (tier < KgsmTier.Admin)
            return Refuse("That account is not allowed to restart a service.");

        if (!LeafCatalog.IsRestartable(action.Target) || LeafCatalog.Find(action.Target) is not { } leaf)
            return Refuse($"{action.Target} is not a service this host can restart.");

        bool ok = await units.RestartAsync(leaf.Unit, ct).ConfigureAwait(false);

        await journal.ServiceRestartedAsync(
            leaf.Id, leaf.DisplayName, leaf.Unit, ok,
            KgsmActor.Format(identity.Provider, identity.Username), AuditOrigin.Notification, ct)
            .ConfigureAwait(false);

        // A row either way: a refused restart is a thing an operator needs to be able to find later, and it
        // is the case where nobody was watching a screen to see it fail.
        return ok
            ? Ok(new PushActionResult(true, $"Asked systemd to restart {leaf.DisplayName}."))
            : Unavailable($"systemd would not restart {leaf.DisplayName}.");
    }

    /// <summary>
    /// Push a server's next scheduled restart back an hour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Operator, like every other verb that changes what a server does.</b> Deferring a restart is not
    /// a settings change — the schedule is untouched and the fire after this one lands where it always
    /// would have — but it does decide whether a server goes down tonight, which is the operator's call.
    /// </para>
    /// <para>
    /// <b>The scheduler enforces nothing, so this does.</b> Its control socket carries no identity: the
    /// gate here is the only one there is, which is why it runs before the socket is dialled rather than
    /// being left to a daemon that has no way to apply it.
    /// </para>
    /// <para>
    /// <b>No audit row.</b> A postponement changes no configuration and leaves no trace on the host — the
    /// scheduler holds the moved target in memory, and it is gone if the daemon restarts. A row claiming
    /// a durable change would be recording something that is not there.
    /// </para>
    /// </remarks>
    private async Task<IActionResult> PostponeAsync(
        PushActionEntity action, KgsmIdentity identity, KgsmTier tier, CancellationToken ct)
    {
        if (tier < KgsmTier.Operator)
            return Refuse("That account is not allowed to change when a server restarts.");

        if (HttpContext.RequestServices.GetService(typeof(SchedulerClient)) is not SchedulerClient scheduler
            || !scheduler.CanControl)
            return Unavailable("This host is not wired to the scheduler.");

        SchedulerControlResponse result = await scheduler
            .PostponeAsync(action.Target, PushActionCatalog.PostponeBy, ct).ConfigureAwait(false);

        logger.LogInformation("notification action: postpone {Server} — {Message} (actor={Actor}, via push)",
            action.Target, result.Message, identity.ActorString);

        if (!result.Ok)
            return Refuse(Capitalize(result.Message) + ".");

        // The new time, in the words the person will read it in. The scheduler answers with it precisely
        // so a caller never has to ask again to find out what it just did.
        string when = result.NextFireUtc is { } next
            ? $" It will restart at {next.ToLocalTime():HH:mm} instead."
            : "";

        return Ok(new PushActionResult(true, $"{action.Target}'s restart is pushed back an hour.{when}"));
    }

    /// <summary>
    /// Let a waiting account in, at the floor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Viewer, and only viewer.</b> A button has no room to choose a tier, and the floor is the one
    /// grant that is safe to make from a notification's worth of context. Anything above it stays a
    /// decision somebody makes in the Users tab while looking at who is asking.
    /// </para>
    /// <para>
    /// <b>This one writes its own audit row</b>, unlike the lifecycle buttons: kgsm runs nothing for an
    /// account change and emits no event, so there is no echo to carry the provenance and a direct write
    /// is the only record there will be. Same posture as the Users tab's own writes.
    /// </para>
    /// <para>
    /// An account that is no longer pending — approved from a laptop in the meantime, or since disabled —
    /// is reported as it is rather than overwritten. Two admins answering the same notification is the
    /// expected case, not an edge one.
    /// </para>
    /// </remarks>
    private async Task<IActionResult> ApproveAsync(
        PushActionEntity action, KgsmIdentity identity, KgsmTier tier, CancellationToken ct)
    {
        if (tier < KgsmTier.Admin)
            return Refuse("That account is not allowed to approve accounts.");

        KgsmUser? account;
        try
        {
            account = await users.Store.FindByIdAsync(action.Target, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "notification action: could not read account {UserId}", action.Target);
            return Unavailable("The account store on this host could not be read.");
        }

        if (account is null)
            return Refuse("That account no longer exists on this host.");

        if (account.Status == UserStatus.Active)
            return Ok(new PushActionResult(true, $"{account.DisplayName} has already been approved."));

        if (account.Status == UserStatus.Disabled)
            return Refuse($"{account.DisplayName} has been switched off, so approving is not the answer.");

        KgsmUser approved = account with
        {
            Tier = KgsmTier.Viewer,
            TierSource = TierSource.Granted,
            Status = UserStatus.Active,
            Updated = DateTimeOffset.UtcNow,
        };

        if (!await users.Store.UpdateAsync(approved, ct).ConfigureAwait(false))
            return Refuse("That account no longer exists on this host.");

        // Authority is resolved per request from a short-lived cache; drop this account's entries so the
        // person who was just let in is not still refused for the length of a TTL.
        await users.ForgetAsync(approved.UserId, ct).ConfigureAwait(false);

        await journal.AccountAsync(
            ApiJournal.UserApprovedEvent,
            approved.UserId,
            approved.Username,
            toTier: KgsmTiers.ToWire(approved.Tier),
            fromStatus: UserStatuses.ToWire(account.Status),
            toStatus: UserStatuses.ToWire(approved.Status),
            actor: KgsmActor.Format(identity.Provider, identity.Username),
            origin: AuditOrigin.Notification,
            ct: ct).ConfigureAwait(false);

        logger.LogInformation("notification action: approved {User} as viewer (actor={Actor}, via push)",
            approved.Username, identity.ActorString);

        return Ok(new PushActionResult(true, $"{approved.DisplayName} can now sign in as a viewer."));
    }

    /// <summary>
    /// Run one lifecycle verb against the staged server.
    /// </summary>
    /// <remarks>
    /// The same gates the panel's own command path applies, in the same order — the tier, the observed
    /// run state, and the one-in-flight claim — because a shortcut from a lock screen must not be a
    /// shortcut past any of them. Notably the state gate is not softened for arriving late: a person
    /// tapping Start on a server somebody else already started is told it is already running, rather than
    /// having the tap quietly do nothing.
    /// </remarks>
    private async Task<IActionResult> LifecycleAsync(
        PushActionEntity action, string verb, KgsmIdentity identity, KgsmTier tier, CancellationToken ct)
    {
        // The ordinal IS the hierarchy (admin ⊇ operator ⊇ viewer) — the same comparison the policy
        // handler makes for the panel's own command route.
        if (tier < KgsmTier.Operator)
            return Refuse($"That account is not allowed to {verb} a server.");

        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct);
        Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, action.Target, StringComparison.Ordinal));
        if (server is null)
            return Refuse($"{action.Target} is not on this host any more.");

        if (CommandGate.Inadmissible(verb, server.Status) is { } noop)
            return Refuse(Capitalize(noop) + ".");

        string jobId = "job_" + Guid.NewGuid().ToString("N")[..8];
        Job? job = jobs.TryStart(jobId, action.Target, verb, DateTimeOffset.UtcNow);
        if (job is null)
            return Refuse($"Something is already running on {action.Target}.");

        // Stamped, not written: every verb here is kgsm's event to emit, so the provenance rides the
        // engine call and the audit row comes from the echo. A second writer for an action the engine
        // already emits is what the audit model forbids.
        runner.Start(job, identity.ActorString, AuditOrigin.Notification);
        logger.LogInformation(
            "notification action: {Verb} {ServerId} job={JobId} (actor={Actor}, via push)",
            verb, action.Target, job.Id, identity.ActorString);

        // What has happened is that kgsm was asked. Whether the server ends up in that state is the job's
        // answer, and claiming it here would be reporting an outcome nobody has yet.
        return Ok(new PushActionResult(true, $"Asked kgsm to {verb} {action.Target}."));
    }

    /// <summary>
    /// Update every server a summary named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each one runs the same gates, individually.</b> A batch is not a way past the state check or the
    /// one-in-flight claim — it is the same verb, several times, and a server that cannot take it now says
    /// so while the others still go.
    /// </para>
    /// <para>
    /// <b>Partial is the normal outcome, so it is what gets reported.</b> Somewhere in a list of five
    /// there is usually one mid-backup, and a message saying "updating 5" when it started 4 would be the
    /// only record that person ever sees of the one that did not.
    /// </para>
    /// </remarks>
    private async Task<IActionResult> UpdateAllAsync(
        PushActionEntity action, KgsmIdentity identity, KgsmTier tier, CancellationToken ct)
    {
        if (tier < KgsmTier.Operator)
            return Refuse("That account is not allowed to update a server.");

        IReadOnlyList<string> targets = PushActionTargets.Split(action.Subject);
        if (targets.Count == 0)
            return Refuse("That notification named no servers.");

        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct).ConfigureAwait(false);
        var started = new List<string>();
        var skipped = new List<string>();

        foreach (string id in targets)
        {
            Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (server is null) { skipped.Add(id); continue; }
            if (CommandGate.Inadmissible(Contracts.CommandVerb.Update, server.Status) is not null)
            {
                skipped.Add(id);
                continue;
            }

            string jobId = "job_" + Guid.NewGuid().ToString("N")[..8];
            Job? job = jobs.TryStart(jobId, id, Contracts.CommandVerb.Update, DateTimeOffset.UtcNow);
            if (job is null) { skipped.Add(id); continue; }

            runner.Start(job, identity.ActorString, AuditOrigin.Notification);
            started.Add(id);
        }

        logger.LogInformation(
            "notification action: update {Started} of {Total} (actor={Actor}, via push); skipped: {Skipped}",
            started.Count, targets.Count, identity.ActorString,
            skipped.Count == 0 ? "(none)" : string.Join(", ", skipped));

        if (started.Count == 0)
            return Refuse($"None of the {targets.Count} could be updated right now.");

        string tail = skipped.Count == 0
            ? ""
            : $" {string.Join(", ", skipped)} couldn't be started.";

        return Ok(new PushActionResult(true,
            $"Asked kgsm to update {started.Count} of {targets.Count}.{tail}"));
    }

    // The gate's reasons are written as sentence fragments for an error envelope ("server is already
    // stopped"); here they are the whole message a person reads on a notification.
    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Remove one player from one server — the same resolution the panel's moderation route performs, and
    /// deliberately re-performed rather than trusted from staging time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The staged row names a roster key, never an identity to send.</b> Which field reaches the game is
    /// the blueprint's decision, read here through <see cref="ModerationTargetResolver"/> against the roster
    /// record that key resolves to — so a handle cannot carry an address the roster never saw, and a game
    /// that moderates by account id is not sent a name because a name is what was to hand.
    /// </para>
    /// <para>
    /// <b>Everything is re-checked at the tap, because the interval is the point.</b> A notification is
    /// answered minutes later from a lock screen: by then the person may have left, the game may not declare
    /// this action, and the account may have been demoted. Each of those is reported in the words the person
    /// will read on the follow-up notification.
    /// </para>
    /// </remarks>
    private IActionResult ModerateAsync(
        PushActionEntity action, string moderation, KgsmIdentity identity, KgsmTier tier)
    {
        if (tier < KgsmTier.Operator)
            return Refuse($"That account is not allowed to {moderation} a player.");

        if (string.IsNullOrEmpty(action.Subject))
            return Refuse("That notification did not name a player.");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService engine)
            return Unavailable("The kgsm engine is not readable on this host right now.");

        Instance? instance = engine.GetInstanceInfo(action.Target);
        if (instance is null)
            return Refuse($"{action.Target} is not on this host any more.");

        if (!history.TryGetPlayer(action.Target, action.Subject!, out RosterPlayer player))
            return Refuse($"{action.Subject} is not on {action.Target}'s roster.");

        // A kick on somebody who already left is answered here rather than by the engine: the game would
        // refuse it as a 502 that reads like a fault, when what happened is simply that the moment passed.
        if (moderation == Contracts.ModerationAction.Kick && player.Status != PlayerStatus.online)
            return Refuse($"{Name(player)} is no longer connected.");

        string? template = moderation == Contracts.ModerationAction.Kick ? instance.KickCommand : instance.BanCommand;
        ModerationTargetResolver.Failure failure =
            ModerationTargetResolver.TryResolve(template, player, out string target, out ModerationTargetKind kind);

        if (failure == ModerationTargetResolver.Failure.Unsupported)
            return Refuse($"This game declares no {moderation} command.");
        if (failure == ModerationTargetResolver.Failure.NoSuchIdentity)
            return Refuse($"This game moderates by {kind.ToString().ToLowerInvariant()}, which {Name(player)} has none of.");

        // Stamped, not written — kgsm emits instance_player_kicked/_banned and the audit row comes off that
        // echo, exactly as the panel's own moderation route leaves it.
        KgsmResult result = moderation == Contracts.ModerationAction.Kick
            ? engine.Kick(action.Target, target, identity.ActorString, AuditOrigin.Notification)
            : engine.Ban(action.Target, target, identity.ActorString, AuditOrigin.Notification);

        if (!result.IsSuccess)
        {
            logger.LogWarning("notification action: {Action} {Player} on {Server} refused by the engine — {Error}",
                moderation, action.Subject, action.Target, result.Stderr);
            return Unavailable($"The engine refused the {moderation}.");
        }

        logger.LogInformation("notification action: {Action} {Player} on {Server} (actor={Actor}, via push)",
            moderation, action.Subject, action.Target, identity.ActorString);

        return Ok(new PushActionResult(true, $"{Name(player)} was {(moderation == Contracts.ModerationAction.Kick ? "kicked" : "banned")}."));
    }

    // The name the person read on the notification, when the roster has one — an account id or an address
    // is what the game answers to, not what somebody recognises on a lock screen.
    private static string Name(RosterPlayer player) =>
        !string.IsNullOrWhiteSpace(player.PlayerName) ? player.PlayerName! : player.PlayerIdentity;

    /// <summary>
    /// Silence one condition on the tapping person's own devices. Needs nothing above the account
    /// existing: it changes what their phone does and nothing about the host.
    /// </summary>
    private async Task<IActionResult> SnoozeAsync(PushActionEntity action, CancellationToken ct)
    {
        // Keyed on the subject the subscription rows carry, which is what the fan-out gate reads.
        PushSubscriptionEntity? device = (await subscriptions.AllAsync(ct))
            .FirstOrDefault(d => string.Equals(d.Endpoint, action.Endpoint, StringComparison.Ordinal));
        if (device is null)
            return Refuse("That device is no longer registered on this host.");

        await snoozes.SetAsync(device.UserSubject, action.Target, DateTimeOffset.UtcNow.Add(PushActionCatalog.SnoozeFor), ct);
        logger.LogInformation("notification action: snoozed {Condition} for {User} for {Hours}h",
            action.Target, device.UserSubject, PushActionCatalog.SnoozeFor.TotalHours);

        return Ok(new PushActionResult(true, $"Muted for {PushActionCatalog.SnoozeFor.TotalHours:n0} hours on your devices."));
    }

    // A refusal the person should read, not an error envelope: the caller is a service worker whose only
    // move is to show the sentence on a follow-up notification.
    private IActionResult Refuse(string message) =>
        StatusCode(StatusCodes.Status403Forbidden, new PushActionResult(false, message));

    private IActionResult Unavailable(string message) =>
        StatusCode(StatusCodes.Status502BadGateway, new PushActionResult(false, message));
}
