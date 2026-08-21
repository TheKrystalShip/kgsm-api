namespace TheKrystalShip.Api.Data;

/// <summary>
/// Whether one account's preferences follow the person rather than the machine — one row per account.
/// <para>
/// Off, every device keeps its own rows. On, every device reads and writes the one synced record (the
/// <c>""</c> device slot in <see cref="UserPreferenceEntity"/>), and the device that switched it on is
/// the one whose arrangement the others were overwritten with.
/// </para>
/// </summary>
public sealed class UserSyncEntity
{
    /// <summary>The account — the session bearer's provider-qualified handle.</summary>
    public string UserId { get; set; } = "";

    /// <summary>Whether preferences are shared across this account's devices.</summary>
    public bool Enabled { get; set; }

    /// <summary>The device that switched it on, or <c>""</c> while it is off. Recorded so a settings
    /// card can say which machine's arrangement won, rather than leaving somebody to guess why their
    /// dashboard changed.</summary>
    public string SourceDevice { get; set; } = "";

    /// <summary>When the switch last moved. Display only.</summary>
    public DateTimeOffset Updated { get; set; }
}
