using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>What a notification for one event offers to do, and what it says on the button.</summary>
/// <param name="Kind">A <see cref="PushActionKind"/> value — the operation staged behind it.</param>
/// <param name="Target">What it acts on: the server, or the watched condition.</param>
/// <param name="Label">The button text. Short — a lock screen truncates.</param>
public sealed record PushActionOffer(string Kind, string Target, string Label);

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

    public static IReadOnlyList<PushActionOffer> For(NotificationEvent ev)
    {
        // An update is the one lifecycle verb a tap can mean exactly one thing by: it applies the
        // build the engine has already established is available, to the server the row names.
        if (ev.CatalogId == "update_available" && !string.IsNullOrEmpty(ev.ServerId))
            return [new PushActionOffer(PushActionKind.ServerUpdate, ev.ServerId!, "Update now")];

        // A breach cannot be fixed from a lock screen, but it can be acknowledged. The target is the
        // condition rather than the host: silencing "this NVMe is hot" is not asking to stop hearing
        // about temperature.
        if (ev.CatalogId == "threshold_breach" && ev.SubjectKey is { Length: > 0 } condition)
            return [new PushActionOffer(PushActionKind.ConditionSnooze, condition, "Snooze 4h")];

        return [];
    }
}
