namespace TheKrystalShip.Api.Data;

/// <summary>
/// One action staged for one button on one device's notification — the thing a tap redeems.
/// <para>
/// <b>The operation stays here; the device holds a handle to it.</b> This mirrors the assistant's
/// <c>pending_confirmations</c>, and for the same reason: a signed envelope round-tripping through the
/// client is a thing that has to be verified, where a handle is a thing that gets looked up. Nothing a
/// request carries beyond the handle is read, so there is nothing in it to poison.
/// </para>
/// <para>
/// <b>The handle is the capability, and that is a real difference from the assistant's.</b> A service
/// worker holds no session — it can read neither the access token nor the refresh token — so there is
/// no bearer on the redemption call and the handle is what stands in for one. Three things put the
/// floor back under it: the row names the <em>device</em> it was staged for and a redemption has to
/// present that endpoint, the tier is re-resolved from the account store at redemption rather than
/// trusted from staging time, and it is single-use with a short life.
/// </para>
/// </summary>
public sealed class PushActionEntity
{
    /// <summary>The opaque handle, 32 hex characters from the cryptographic RNG. Never derived from
    /// what it redeems — it carries no meaning a holder could read or forge a sibling of.</summary>
    public string Id { get; set; } = "";

    /// <summary>Which operation this redeems — a <c>PushActionKind</c> value. Stored as the word, not
    /// an ordinal, so reordering the set can never silently repoint a staged row.</summary>
    public string Kind { get; set; } = "";

    /// <summary>What it acts on: a server id, or the watched condition's subject for a snooze.</summary>
    public string Target { get; set; } = "";

    /// <summary>
    /// Who inside <see cref="Target"/> it acts on, for the kinds that need one — the roster key of the
    /// player a kick or a ban names. Null for everything else.
    /// </summary>
    /// <remarks>
    /// Its own column rather than a separator inside <see cref="Target"/>: a player identity can be a
    /// character name the game chose, and a name containing whatever byte was picked as the separator would
    /// split into the wrong two halves — which on this path means moderating a different person.
    /// </remarks>
    public string? Subject { get; set; }

    /// <summary>The provider-qualified handle of the account it was staged for (<c>provider:subject</c>) —
    /// what the tier is re-resolved from. A subject alone is unique only within its provider.</summary>
    public string UserHandle { get; set; } = "";

    /// <summary>That account's username at staging time, for the audit actor. A label, never authority.</summary>
    public string? Username { get; set; }

    /// <summary>The push endpoint this was staged for. A redemption presenting a different one is
    /// refused: a handle lifted out of this row is inert without the device it was minted for.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>What the button said, so the outcome can be reported in the same words the person read.</summary>
    public string Label { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it stops being redeemable. Short: a button on a notification is answered in the
    /// minutes after it arrives, and a capability nobody used should not keep standing.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// How the one multi-target action carries its targets.
/// </summary>
/// <remarks>
/// <b>A separator is safe here and nowhere else.</b> The moderation kinds keep the player out of
/// <see cref="PushActionEntity.Target"/> precisely because a character name is whatever a game let somebody
/// type; a kgsm instance name is a constrained identifier that cannot contain a comma, so joining a list of
/// them is unambiguous. Anything that does not round-trip is dropped rather than guessed at.
/// </remarks>
public static class PushActionTargets
{
    /// <summary>What <see cref="PushActionEntity.Target"/> reads for a batch — the servers are in
    /// <see cref="PushActionEntity.Subject"/>, and this says so rather than naming one of them.</summary>
    public const string AllServers = "*";

    private const char Separator = ',';

    public static string Join(IEnumerable<string> ids) => string.Join(Separator, ids);

    public static IReadOnlyList<string> Split(string? joined) =>
        string.IsNullOrWhiteSpace(joined)
            ? []
            : joined.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

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

    /// <summary>Stop pushing <c>Target</c> — one watched condition — to this person for a few hours.
    /// Their own phone, so it needs nothing above viewer.</summary>
    public const string ConditionSnooze = "condition.snooze";

    /// <summary>
    /// Disconnect <c>Subject</c> from <c>Target</c>. The reason this whole feature is worth having on a
    /// phone: somebody is ruining a game right now and the person who can stop it is not at a desk.
    /// </summary>
    public const string PlayerKick = "player.kick";

    /// <summary>Disconnect <c>Subject</c> from <c>Target</c> and keep them out.</summary>
    public const string PlayerBan = "player.ban";

    /// <summary>
    /// Restart the leaf named by <c>Target</c>. Admin, like every other way of restarting a service from
    /// the panel — it interrupts something every other surface on this host depends on.
    /// </summary>
    public const string LeafRestart = "leaf.restart";

    /// <summary>
    /// Let the account named by <c>Target</c> in, as a viewer. Viewer rather than a choice of tier: a
    /// button carries no room to pick one, and the floor is the only grant that is safe to make without
    /// looking at who is asking. Anything above it is a decision for the Users tab.
    /// </summary>
    public const string UserApprove = "user.approve";

    /// <summary>
    /// Apply the available update to every server a summary named. The one action that acts on more than
    /// one thing, and it exists only for a digest — where the batch is uniform, so the instruction reads
    /// the same as the single-server one it repeats.
    /// </summary>
    public const string ServerUpdateAll = "server.update_all";

    /// <summary>
    /// Push <c>Target</c>'s next scheduled restart back an hour. It changes nothing about the schedule —
    /// the fire after this one lands where it always would have — which is what makes it the one
    /// scheduling verb a single tap can mean unambiguously.
    /// </summary>
    public const string SchedulePostpone = "schedule.postpone";

    public static bool IsKnown(string? kind) =>
        kind is ServerUpdate or ServerStart or ServerStop or ConditionSnooze
             or PlayerKick or PlayerBan or LeafRestart or UserApprove or ServerUpdateAll
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
