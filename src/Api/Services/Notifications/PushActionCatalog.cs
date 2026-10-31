using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Actions;
using TheKrystalShip.Api.Services.Integrations;

namespace TheKrystalShip.Api.Services.Notifications;

/// <summary>What a notification for one event offers to do, and what it says on the button.</summary>
/// <param name="Kind">A <see cref="PushActionKind"/> value — the operation staged behind it.</param>
/// <param name="Target">What it acts on: the server, or the leaf.</param>
/// <param name="Label">The button text. Short — a lock screen truncates.</param>
/// <param name="Subject">Who or what inside <paramref name="Target"/> it acts on: the player for a
/// moderation kind, the maintenance window for a postponement.</param>
public sealed record PushActionOffer(string Kind, string Target, string Label, string? Subject = null);

/// <summary>
/// The closed map from a notifiable event to the buttons its notification carries. A breach has none: it
/// cannot be fixed from a lock screen, and silencing it is not one of the verbs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Most events offer nothing, and that is the design.</b> A button is an instruction given with no
/// context beyond one line of text, so it belongs only where a single tap is unambiguous. "A server
/// came up" needs no reply; "the box is hot" has no one-tap remedy, and a button that restarted the
/// largest server would be this API guessing at a cause it has not established.
/// </para>
/// <para>
/// Two, at most, whatever the event: Android renders two and drops the rest, so a third would exist
/// only on a desktop and be silently absent where these are actually read.
/// </para>
/// </remarks>
public static class PushActionCatalog
{
    /// <summary>How long a button stays redeemable. A button is answered in the minutes after it arrives,
    /// and a capability nobody used should not keep standing. Never longer than
    /// <see cref="NotificationActors.ActionFreshness"/>, the ceiling on acting from a notification.</summary>
    public static readonly TimeSpan ButtonLifetime = TimeSpan.FromHours(2);

    /// <param name="moderation">What the event's game declares it can do to a player, or
    /// <see langword="null"/> when that could not be established. <b>The blueprint's placeholder is the
    /// contract</b> — a game that declares no ban template cannot ban, so offering the button would be
    /// promising something the engine will refuse. Not knowing is treated exactly like not supporting it:
    /// this is the one place where being wrong means removing the wrong person from a game.</param>
    public static IReadOnlyList<PushActionOffer> For(NotificationEvent ev, ModerationCapability? moderation = null)
    {
        // Somebody arrived, and the phone is the point: the person who can do something about a griefer is
        // usually not the person sitting in front of the panel. Kick before ban — the reversible one first,
        // and on a two-button ceiling the order is what survives.
        if (ev.CatalogId == "player_join"
            && ev.ActionSubject is { Length: > 0 } who
            && ev.ServerId is { Length: > 0 } server
            && moderation is not null)
        {
            var offers = new List<PushActionOffer>(2);
            if (moderation.Kick) offers.Add(new PushActionOffer(PushActionKind.PlayerKick, server, "Kick", who));
            if (moderation.Ban) offers.Add(new PushActionOffer(PushActionKind.PlayerBan, server, "Ban", who));
            return offers;
        }

        // The two events that name something other than a server. Both act on the id the event carries,
        // and both are refused at the tap if the account has since lost the action for them.
        if (ev.ActionSubject is { Length: > 0 } subject)
        {
            if (ev.CatalogId == "leaf_down" && Leaves.LeafCatalog.IsRestartable(subject))
                return [new PushActionOffer(PushActionKind.LeafRestart, subject, "Restart")];
        }

        // A reactor offer deliberately gets no button, and the absence is the design rather than an
        // omission. Confirming one re-derives the condition on the leaf and shows the person what it
        // found — sometimes that the thing is no longer applicable — and a lock-screen tap would skip
        // exactly that reading. The push exists to get somebody to OPEN the offer before it expires,
        // which is what the notification's own tap already does.
        if (ev.CatalogId == "reactor_offer") return [];

        if (string.IsNullOrEmpty(ev.ServerId)) return [];

        // The three conditions the alert feed also describes take their verb from ConditionActions, which
        // carries the reasoning for each choice — the same condition read on a phone and on a card must
        // never be answered differently. The wording stays here: a lock screen has one line of context, so
        // it says "Update now" where a card beside a server name says "Update".
        return ev.CatalogId switch
        {
            "update_available" => [new PushActionOffer(ConditionActions.UpdateAvailable, ev.ServerId!, "Update now")],

            "crash" => [new PushActionOffer(ConditionActions.Crashed, ev.ServerId!, "Stop")],

            "crash_loop" => [new PushActionOffer(ConditionActions.CrashLoop, ev.ServerId!, "Start")],

            // Being told a server went down and being able to answer "put it back" is the whole point of
            // hearing about it away from a desk. No alert describes this one — the feed mirrors conditions
            // the host measures, and a server somebody stopped on purpose is not one.
            "offline" => [new PushActionOffer(PushActionKind.ServerStart, ev.ServerId!, "Start")],

            // An empty server costs the same as a full one. Stopping it is the whole reason to be told.
            "server_empty" => [new PushActionOffer(PushActionKind.ServerStop, ev.ServerId!, "Stop")],

            _ => [],
        };
    }
}

/// <summary>What a button carries to its redemption: the target and, for the kinds that need one, who or
/// what inside it.</summary>
public sealed record ButtonArgument(string Target, string? Subject)
{
    /// <summary>JSON, because a subject can be a character name the game chose and any separator could
    /// appear in it.</summary>
    public static string Pack(string target, string? subject) =>
        System.Text.Json.JsonSerializer.Serialize(new ButtonArgument(target, subject));

    public static ButtonArgument? Unpack(string argument)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ButtonArgument>(argument);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
