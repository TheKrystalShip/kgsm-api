namespace TheKrystalShip.Api.Data;

/// <summary>
/// One node's half of one transfer: a row per transfer per side, keyed by (<see cref="TransferId"/>,
/// <see cref="Role"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A table because a transfer outlives the process driving it.</b> The source holds the engine's lock
/// from the lock to the commit, and only this row says which peer to ask before releasing it; the target's
/// row is what its recovery aborts a half-staged import from, and what the source's recovery reads back
/// over the member route.
/// </para>
/// <para>
/// <b>EnsureCreated, NOT a migration</b> (see <see cref="AppDbContext"/>). On an already-deployed database
/// <see cref="Services.Transfers.TransferStore"/>'s idempotent <c>CREATE TABLE IF NOT EXISTS</c> adds it.
/// </para>
/// </remarks>
public sealed class TransferEntity
{
    public string TransferId { get; set; } = "";

    /// <summary><c>source</c> or <c>target</c>.</summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// The source instance's id. On the source it is the local instance being exported; on the target it
    /// is what the server was called where it came from, which the target's own instance id is minted
    /// beside in <see cref="TargetInstanceId"/>.
    /// </summary>
    public string InstanceId { get; set; } = "";

    /// <summary>The other node's member id.</summary>
    public string PeerMemberId { get; set; } = "";

    /// <summary>The game name the source held for the instance when the transfer began; null for none.</summary>
    public string? GameName { get; set; }

    /// <summary><see cref="Contracts.TransferState"/>.</summary>
    public string State { get; set; } = "";

    /// <summary>Target: the job's <see cref="Contracts.TransferPhase"/>, written before each step so a
    /// restart knows how far the job got.</summary>
    public string? Phase { get; set; }

    /// <summary>Why an aborted transfer ended, as a <see cref="Contracts.TransferRefusalCodes"/> value; on
    /// the source, also set when a commit arrives for a transfer already aborted.</summary>
    public string? Reason { get; set; }

    /// <summary>Target: the engine's staged import id, set once <c>import begin</c> answered.</summary>
    public string? ImportId { get; set; }

    /// <summary>Target: the instance id <c>import finish</c> minted.</summary>
    public string? TargetInstanceId { get; set; }

    /// <summary>Target: the library asked for; null takes the engine's default.</summary>
    public string? Library { get; set; }

    /// <summary>Target: the job that runs it.</summary>
    public string? JobId { get; set; }

    /// <summary>Target: whether the commit message is in the outbox.</summary>
    public bool CommitEnqueued { get; set; }

    /// <summary>Source: lower-case hex SHA-256 of the ticket. The ticket itself is never stored.</summary>
    public string? TicketHash { get; set; }

    /// <summary>Source: when the ticket stops being redeemable unless the lock was taken.</summary>
    public DateTimeOffset? TicketExpiresAt { get; set; }

    /// <summary>Source: the account that minted the ticket, whose access is evaluated again whenever the
    /// target redeems it. Null on an auth-disabled host.</summary>
    public string? AccountId { get; set; }

    /// <summary>The person who asked, stamped onto every engine call made for the transfer.</summary>
    public string? Actor { get; set; }

    /// <summary>The surface that asked.</summary>
    public string? Origin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>A detached copy, so a row handed out of the store's cache cannot change what it holds.</summary>
    public TransferEntity Copy() => (TransferEntity)MemberwiseClone();
}
