using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.Api.Services.Integrations.WebPush;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

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
/// writer for an action the engine already emits is the one thing the audit model forbids. The
/// consequence is worth stating: the row reads <c>origin: ui</c>, which is true and does not distinguish
/// a tap on a notification from a click in the panel. A snooze writes nothing anywhere, being a personal
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
    JobRegistry jobs,
    CommandRunner runner,
    ILogger<NotificationActionsController> logger) : ControllerBase
{
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

        return action.Kind switch
        {
            PushActionKind.ServerUpdate => await UpdateAsync(action, identity, authority.Tier, ct),
            PushActionKind.ConditionSnooze => await SnoozeAsync(action, ct),
            _ => Refuse("This build does not know how to do that."),
        };
    }

    /// <summary>
    /// Apply the available update. The same gates the panel's own command path applies, in the same
    /// order — the tier, the observed run state, and the one-in-flight claim — because a shortcut from a
    /// lock screen must not be a shortcut past any of them.
    /// </summary>
    private async Task<IActionResult> UpdateAsync(
        PushActionEntity action, KgsmIdentity identity, KgsmTier tier, CancellationToken ct)
    {
        // The ordinal IS the hierarchy (admin ⊇ operator ⊇ viewer) — the same comparison the policy
        // handler makes for the panel's own command route.
        if (tier < KgsmTier.Operator)
            return Refuse("That account is not allowed to update a server.");

        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct);
        Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, action.Target, StringComparison.Ordinal));
        if (server is null)
            return Refuse($"{action.Target} is not on this host any more.");

        if (CommandGate.Inadmissible(CommandVerb.Update, server.Status) is { } noop)
            return Refuse(noop);

        string jobId = "job_" + Guid.NewGuid().ToString("N")[..8];
        Job? job = jobs.TryStart(jobId, action.Target, CommandVerb.Update, DateTimeOffset.UtcNow);
        if (job is null)
            return Refuse($"Something is already running on {action.Target}.");

        // origin is the panel: the notification IS this panel's surface, reaching a phone. The closed
        // vocabulary has no value for "through a notification", and widening it for one transport is
        // the move the audit model has already declined once.
        runner.Start(job, identity.ActorString, AuditOrigin.Ui);
        logger.LogInformation(
            "notification action: update {ServerId} job={JobId} (actor={Actor}, via push)",
            action.Target, job.Id, identity.ActorString);

        // What has happened is that kgsm was asked. Whether the server ends up updated is the job's
        // answer, minutes from now, and claiming it here would be reporting an outcome nobody has.
        return Ok(new PushActionResult(true, $"Asked kgsm to update {action.Target}."));
    }

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
