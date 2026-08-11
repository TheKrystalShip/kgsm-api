namespace TheKrystalShip.Api.Data;

/// <summary>
/// One browser's Web Push subscription — the row a device is reachable through.
/// <para>
/// This is the shape that does <b>not</b> fit <see cref="IntegrationEntity"/>, and the reason push
/// needed a table of its own: an integration holds ONE secret for the whole host (a Slack webhook),
/// while push has one credential per <em>user per device</em>, minted by the browser rather than
/// pasted by an admin.
/// </para>
/// <para>
/// <b>Not a secret we chose.</b> The endpoint is a capability URL issued by the push service, and the
/// keys seal payloads to that browser. They are stored plaintext in the host-local SQLite, the same
/// posture as the integration webhook — but note that possession of a row lets the holder push to that
/// device, so it is never echoed to anyone but its own owner.
/// </para>
/// </summary>
public sealed class PushSubscriptionEntity
{
    /// <summary>The push service's endpoint URL — the primary key. Unique per browser per origin, and
    /// the identity the user agent itself uses, so re-subscribing the same browser upserts one row.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>The owning account's stable subject (<c>local:usr_…</c>). Every read and delete is
    /// scoped by this: one user must never see or revoke another's devices.</summary>
    public string UserSubject { get; set; } = "";

    /// <summary>The owner's username at the time of subscribing — for display in their device list.
    /// The subject is the authority; this is a label (see the display-name-vs-audit rule).</summary>
    public string? Username { get; set; }

    /// <summary>The subscription's P-256 public key, base64url (the browser's <c>p256dh</c>).</summary>
    public string P256dh { get; set; } = "";

    /// <summary>The subscription's 16-byte auth secret, base64url.</summary>
    public string Auth { get; set; } = "";

    /// <summary>The browser's user-agent at subscribe time, so a person can tell their own devices
    /// apart in the list. Absent when the browser sent none — never invented.</summary>
    public string? UserAgent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last time the push service ACCEPTED a send for this row. The honest "this device is
    /// still reachable" signal; null until the first successful push.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Consecutive send failures that were NOT a definitive 404/410 (those delete the row
    /// outright). Lets a persistently broken endpoint be retired without punishing one bad night.</summary>
    public int FailureCount { get; set; }
}
