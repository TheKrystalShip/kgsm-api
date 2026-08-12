namespace TheKrystalShip.Api.Data;

/// <summary>
/// One person's "not this one, not for a few hours" about one watched condition.
/// <para>
/// It is deliberately narrower than a <see cref="PushPreferenceEntity"/>. A preference is about a
/// whole catalog event and lasts until it is changed; this is about a single condition — one rule on
/// one sensor — and expires on its own. Somebody silencing a hot NVMe for the afternoon has not asked
/// to stop hearing about temperature.
/// </para>
/// <para>
/// <b>Personal, and only on this channel.</b> It gates the push provider's per-device fan-out and
/// nothing else: the condition still fires, still writes its audit rows, still shows in the alert feed,
/// and still reaches Slack and everybody else's phone. Silencing a host for everyone is the admin's
/// host-wide rule, which is a different control in a different place.
/// </para>
/// </summary>
public sealed class PushSnoozeEntity
{
    /// <summary>The account's stable subject — half the composite key.</summary>
    public string UserSubject { get; set; } = "";

    /// <summary>The condition's subject key (rule + sensor + server) — the other half. The same value
    /// the delivery worker coalesces on, so what gets silenced is exactly what was noisy.</summary>
    public string SubjectKey { get; set; } = "";

    /// <summary>When it lapses. A row past this is ignored on read and swept on the next write, so a
    /// snooze can never quietly become permanent.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
