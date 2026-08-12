using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>What a notification for one event offers to do, and what it says on the button.</summary>
/// <param name="Kind">A <see cref="PushActionKind"/> value — the operation staged behind it.</param>
/// <param name="Target">What it acts on: the server, or the watched condition.</param>
/// <param name="Label">The button text. Short — a lock screen truncates.</param>
/// <param name="Subject">Who inside <paramref name="Target"/> it acts on, for the moderation kinds.</param>
public sealed record PushActionOffer(string Kind, string Target, string Label, string? Subject = null);

/// <summary>
/// The closed map from a notifiable event to the buttons its notification carries.
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
    /// <summary>How long a snooze lasts. Long enough to get through whatever is causing it, short
    /// enough that forgetting to lift it is not the same as switching the event off.</summary>
    public static readonly TimeSpan SnoozeFor = TimeSpan.FromHours(4);

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
            && ev.PlayerIdentity is { Length: > 0 } who
            && ev.ServerId is { Length: > 0 } server
            && moderation is not null)
        {
            var offers = new List<PushActionOffer>(2);
            if (moderation.Kick) offers.Add(new PushActionOffer(PushActionKind.PlayerKick, server, "Kick", who));
            if (moderation.Ban) offers.Add(new PushActionOffer(PushActionKind.PlayerBan, server, "Ban", who));
            return offers;
        }

        // A breach cannot be fixed from a lock screen, but it can be acknowledged. The target is the
        // condition rather than the host: silencing "this NVMe is hot" is not asking to stop hearing
        // about temperature.
        if (ev.CatalogId == "threshold_breach" && ev.SubjectKey is { Length: > 0 } condition)
            return [new PushActionOffer(PushActionKind.ConditionSnooze, condition, "Snooze 4h")];

        if (string.IsNullOrEmpty(ev.ServerId)) return [];

        return ev.CatalogId switch
        {
            // The one lifecycle verb a tap can mean exactly one thing by: apply the build the engine has
            // already established is available, to the server the row names.
            "update_available" => [new PushActionOffer(PushActionKind.ServerUpdate, ev.ServerId!, "Update now")],

            // Being told a server went down and being able to answer "put it back" is the whole point of
            // hearing about it away from a desk.
            "offline" => [new PushActionOffer(PushActionKind.ServerStart, ev.ServerId!, "Start")],

            // Stop, not restart. The watchdog is already restarting it — that is what makes a crash
            // notification arrive repeatedly — so the button that changes anything is the one that
            // changes the desired state and lets it stay down.
            "crash" => [new PushActionOffer(PushActionKind.ServerStop, ev.ServerId!, "Stop")],

            // The mirror of the one above. Here the watchdog has stopped trying, so the server is down and
            // staying down, and Stop would be asking for what already is. Start is the one thing left worth
            // a tap: a crash cause that has since gone away (a full disk, a port somebody else was holding)
            // makes the next attempt succeed, and if it does not, the supervisor gives up again and says so.
            "crash_loop" => [new PushActionOffer(PushActionKind.ServerStart, ev.ServerId!, "Start")],

            // An empty server costs the same as a full one. Stopping it is the whole reason to be told.
            "server_empty" => [new PushActionOffer(PushActionKind.ServerStop, ev.ServerId!, "Stop")],

            _ => [],
        };
    }
}
