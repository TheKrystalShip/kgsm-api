namespace TheKrystalShip.Api.Data;

/// <summary>
/// One preference a person has set, on one device — the general per-account preference store the
/// panel's editable surface reads and writes.
/// <para>
/// <b>The key is opaque.</b> The API stores what it is handed under the name it is handed and knows
/// nothing about what any of it means: <c>dashboard.layout</c> is a widget arrangement, <c>ui.theme</c>
/// a palette id, and a key nothing here has heard of stores and reads back the same way. That is the
/// property worth keeping — a new preference is zero backend work.
/// </para>
/// <para>
/// <b>A row is per device, and <see cref="DeviceId"/> is client-minted.</b> A session is per-host and
/// expires, so a session id names a sign-in rather than a machine — the same laptop signing in again
/// would be a new device and would lose its layout. The client mints one id and keeps it; this store
/// takes it as given, and every device-scoped request has to carry it (there is no default: the empty
/// device is the synced record's own slot, below).
/// </para>
/// <para>
/// <b><see cref="DeviceId"/> is <c>""</c> for the synced record</b> — the one row per key that every
/// device reads while account sync is on (<see cref="UserSyncEntity"/>). It sits in the same table
/// because it is the same thing with a wider audience, and a separate table would mean two shapes to
/// merge in a cluster instead of one.
/// </para>
/// </summary>
public sealed class UserPreferenceEntity
{
    /// <summary>The account this belongs to — the provider-qualified handle the session bearer carries
    /// as its subject (<c>discord:198772043</c>). Qualified rather than bare, because a provider's
    /// subject is unique only within that provider.</summary>
    public string UserId { get; set; } = "";

    /// <summary>The device that owns this row, or <c>""</c> for the synced record every device reads
    /// while sync is on.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The preference's name. Opaque to this API.</summary>
    public string Key { get; set; } = "";

    /// <summary>The preference itself, as JSON text. Stored verbatim and handed back as JSON, so the
    /// shape belongs entirely to whoever wrote it.</summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// Monotonic per <c>(UserId, Key)</c> — the merge key. Every write takes the highest version that
    /// key has anywhere for this account and adds one, so nodes converge on last-write-wins over a
    /// counter rather than over a clock: wall-clock LWW hands permanent victory to whichever node's
    /// clock runs fastest, and the losing device watches its layout revert with no error anywhere.
    /// </summary>
    public long Version { get; set; }

    /// <summary>The device whose write produced this version — the tiebreak when two versions are
    /// equal, compared lexically so every node reaches the same answer.</summary>
    public string OriginDevice { get; set; } = "";

    /// <summary>When this row was written. <b>Display only, never a merge input</b> — a settings card
    /// can honestly say when a preference last changed; nothing decides anything on it.</summary>
    public DateTimeOffset Updated { get; set; }
}
