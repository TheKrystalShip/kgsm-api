namespace TheKrystalShip.Api.Data;

/// <summary>
/// One account's quiet window: when it does not want to be woken, and what is important enough to wake it
/// anyway.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per account, not per device.</b> A person with a phone and a laptop is asleep for both of them, and a
/// window they had to set twice would eventually disagree with itself.
/// </para>
/// <para>
/// <b>It shapes push and nothing else.</b> Slack and the webhook are addressed to a channel rather than to
/// a person, so there is nobody whose night this would be — silencing those would be silencing a team on
/// one member's schedule.
/// </para>
/// </remarks>
public sealed class PushQuietHoursEntity
{
    /// <summary>The account, as the subject the subscription rows carry.</summary>
    public string UserSubject { get; set; } = "";

    /// <summary>Whether the window applies at all. Kept rather than deleting the row, so switching quiet
    /// hours off for a week does not lose the times somebody chose.</summary>
    public bool Enabled { get; set; }

    /// <summary>Minutes past local midnight the window opens (0–1439).</summary>
    public int StartMinute { get; set; }

    /// <summary>Minutes past local midnight it closes. <b>Less than <see cref="StartMinute"/> means it
    /// wraps midnight</b>, which is the normal case — 23:00 to 08:00 is a night, not an empty set.</summary>
    public int EndMinute { get; set; }

    /// <summary>
    /// The IANA zone the two times are read in (<c>Europe/Bucharest</c>), as the browser that set them
    /// reported it.
    /// </summary>
    /// <remarks>
    /// Stored rather than assumed, because the host's clock is not the person's: a fleet is often
    /// administered from a different country than it runs in, and a window silently applied in UTC would
    /// silence the wrong nine hours.
    /// </remarks>
    public string TimeZoneId { get; set; } = "";

    /// <summary>The lowest <c>AuditSeverity</c> that still gets through while the window is open — a
    /// <c>PushQuietFloor</c> value.</summary>
    public string MinSeverity { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// What a quiet window still lets through. The severity band an event has to reach, plus the one value
/// that is not a band.
/// </summary>
public static class PushQuietFloor
{
    /// <summary>Everything arrives; the window changes nothing. Stored rather than treated as "off",
    /// because it is a different statement from having no quiet hours configured.</summary>
    public const string Everything = "everything";

    /// <summary>Warnings and worse.</summary>
    public const string Warn = "warn";

    /// <summary>Only <c>danger</c>: a crash, a give-up, a threshold, a service down.</summary>
    public const string Danger = "danger";

    /// <summary>Nothing gets through. Spelled as its own word rather than as an impossible severity, so it
    /// can never be misread as "no floor".</summary>
    public const string Nothing = "nothing";

    public static bool IsKnown(string? floor) => floor is Everything or Warn or Danger or Nothing;

    /// <summary>
    /// Whether an event at <paramref name="severity"/> clears <paramref name="floor"/>.
    /// </summary>
    /// <remarks>
    /// <c>success</c> ranks with <c>info</c>: it means something finished, which is good news and not
    /// urgent. An unrecognised severity ranks as low as possible on purpose — the value of the floor is
    /// that it holds things back, and a spelling this build does not know is not grounds to make an
    /// exception.
    /// </remarks>
    public static bool Passes(string? severity, string? floor) => floor switch
    {
        Nothing => false,
        Danger => severity == Contracts.AuditSeverity.Danger,
        Warn => severity is Contracts.AuditSeverity.Danger or Contracts.AuditSeverity.Warn,
        _ => true,
    };
}
