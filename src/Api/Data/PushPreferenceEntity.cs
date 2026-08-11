namespace TheKrystalShip.Api.Data;

/// <summary>
/// One person's choice about one catalog event: do they want it pushed to their devices.
/// <para>
/// Keyed by account, not by device. Somebody's answer to "do I want to hear about crashes" is about
/// them, not about which phone is in their hand, and making it per-device would mean configuring the
/// same list once per browser they ever sign in from.
/// </para>
/// <para>
/// <b>Only explicit choices are stored.</b> No row means the default, which is ON — a person who has
/// subscribed a device has already opted in, and a newly added catalog event should reach them rather
/// than sit silently off until they discover it. So this table holds deviations, and an untouched
/// account has no rows at all.
/// </para>
/// <para>
/// It never overrides the admin. The host-wide rule on the integration decides what the channel
/// carries; this decides what a person wants out of that. Both must say yes.
/// </para>
/// </summary>
public sealed class PushPreferenceEntity
{
    /// <summary>The account's stable subject — half the composite key.</summary>
    public string UserSubject { get; set; } = "";

    /// <summary>The <c>NotificationCatalog</c> event id — the other half.</summary>
    public string CatalogId { get; set; } = "";

    /// <summary>Whether this person wants this event pushed.</summary>
    public bool Enabled { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
