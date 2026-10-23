namespace TheKrystalShip.Api.Data;

/// <summary>The closed set of operations a notification button can redeem. Deliberately short — a verb
/// belongs here only when a single tap, with no further context, is an unambiguous instruction.</summary>
public static class PushActionKind
{
    /// <summary>Apply the available update to <c>Target</c>. Needs operator, like every other mutation.</summary>
    public const string ServerUpdate = "server.update";

    /// <summary>Bring <c>Target</c> back up. The reply to being told it went down.</summary>
    public const string ServerStart = "server.start";

    /// <summary>
    /// Take <c>Target</c> down and leave it down. Offered on a crash, where it is the one thing a person
    /// actually wants and the watchdog will not do for them: the watchdog's job is to bring a crashed
    /// server back, so a server crashing repeatedly is being restarted repeatedly, and the way out is to
    /// change what its desired state is. "Restart it" is not offered because that is already happening.
    /// </summary>
    public const string ServerStop = "server.stop";

    /// <summary>
    /// Disconnect <c>Subject</c> from <c>Target</c>. The reason this whole feature is worth having on a
    /// phone: somebody is ruining a game right now and the person who can stop it is not at a desk.
    /// </summary>
    public const string PlayerKick = "player.kick";

    /// <summary>Disconnect <c>Subject</c> from <c>Target</c> and keep them out.</summary>
    public const string PlayerBan = "player.ban";

    /// <summary>
    /// Restart the leaf named by <c>Target</c>. Needs <c>api:services.manage</c>, like every other way of
    /// restarting a service from the panel — it interrupts something every other surface on this host depends on.
    /// </summary>
    public const string LeafRestart = "leaf.restart";

    /// <summary>
    /// Push <c>Target</c>'s <c>Subject</c> window back an hour. It changes nothing about the schedule —
    /// the fire after this one lands where it always would have — which is what makes it the one
    /// scheduling verb a single tap can mean unambiguously.
    /// </summary>
    public const string SchedulePostpone = "schedule.postpone";

    public static bool IsKnown(string? kind) =>
        kind is ServerUpdate or ServerStart or ServerStop
             or PlayerKick or PlayerBan or LeafRestart
             or SchedulePostpone;

    /// <summary>The moderation action a kind runs, or <see langword="null"/> when it is not one. Maps onto
    /// the same <see cref="Contracts.ModerationAction"/> vocabulary the panel's own route takes, so the two
    /// paths cannot mean different things by "ban".</summary>
    public static string? ModerationFor(string kind) => kind switch
    {
        PlayerKick => Contracts.ModerationAction.Kick,
        PlayerBan => Contracts.ModerationAction.Ban,
        _ => null,
    };

    /// <summary>The engine verb a server-scoped kind runs, or <see langword="null"/> when the kind is not
    /// a lifecycle command at all. One place, so the redemption path cannot drift from the panel's.</summary>
    public static string? VerbFor(string kind) => kind switch
    {
        ServerUpdate => Contracts.CommandVerb.Update,
        ServerStart => Contracts.CommandVerb.Start,
        ServerStop => Contracts.CommandVerb.Stop,
        _ => null,
    };
}
