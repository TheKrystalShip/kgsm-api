namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The <c>POST /api/v1/servers/{id}/transfers</c> body, sent to the node that holds the server: the member
/// it is to move to.
/// </summary>
/// <param name="TargetMemberId">The target node's member id, as the roster names it.</param>
public sealed record TransferCreateRequest(string? TargetMemberId);

/// <summary>
/// What is about to move, as the source measured it.
/// </summary>
/// <param name="Instance">The instance id on the source.</param>
/// <param name="DisplayName">Its label, or null when it has none of its own.</param>
/// <param name="Blueprint">The blueprint it runs.</param>
/// <param name="Runtime"><c>native</c> or <c>container</c>.</param>
/// <param name="Bytes">The sum of every part's size: the tree's measured bytes and each image's.</param>
/// <param name="GameName">The game name the cluster's DNS anchor published for it on the source, or null
/// when it holds none.</param>
public sealed record TransferSummary(
    string Instance,
    string? DisplayName,
    string Blueprint,
    string Runtime,
    long Bytes,
    string? GameName);

/// <summary>
/// The <c>201</c> answer to a transfer's mint on the source: the transfer, and the ticket the target
/// presents back to the source for it.
/// </summary>
/// <param name="TransferId">The transfer's id, the same on both nodes.</param>
/// <param name="Ticket">The single-transfer credential the browser hands the target. The source keeps
/// only its hash.</param>
/// <param name="SourceMemberId">The source node's member id, which the target calls back.</param>
/// <param name="ExpiresAt">When the ticket stops being redeemable unless the lock has been taken.</param>
/// <param name="Summary">What is about to move.</param>
public sealed record TransferTicket(
    string TransferId,
    string Ticket,
    string SourceMemberId,
    DateTimeOffset ExpiresAt,
    TransferSummary Summary);

/// <summary>
/// The body of <c>POST /api/v1/transfers/preflight</c> and <c>POST /api/v1/transfers</c>, sent to the target
/// node.
/// </summary>
/// <param name="SourceMemberId">The source node's member id, from the ticket.</param>
/// <param name="TransferId">The transfer's id, from the ticket.</param>
/// <param name="Ticket">The ticket itself.</param>
/// <param name="Library">The target library by name; absent takes the target's default.</param>
public sealed record TransferStartRequest(
    string? SourceMemberId,
    string? TransferId,
    string? Ticket,
    string? Library = null);

/// <summary>One reason a transfer cannot go ahead.</summary>
/// <param name="Code">A <see cref="TransferRefusalCodes"/> value, or the engine's own code for a refusal
/// the engine names that this list does not (a library or blueprint problem on the target).</param>
/// <param name="Detail">The words of whoever refused, for a person.</param>
public sealed record TransferRefusal(string Code, string Detail);

/// <summary>
/// The target's verdict on a transfer before anything starts. Nothing is written and the ticket is not
/// used up.
/// </summary>
/// <param name="Ok">True when the target would accept the transfer.</param>
/// <param name="Refusals">Every refusal at once; empty when <paramref name="Ok"/>.</param>
/// <param name="Summary">What would move, or null when the source could not be asked.</param>
public sealed record TransferPreflight(
    bool Ok,
    IReadOnlyList<TransferRefusal> Refusals,
    TransferSummary? Summary);

/// <summary>The <c>202</c> answer to a transfer's start on the target.</summary>
/// <param name="JobId">The job the browser watches.</param>
/// <param name="TransferId">The transfer it runs.</param>
public sealed record TransferAccepted(string JobId, string TransferId);

/// <summary>
/// A server's transfer while one holds it, carried on <see cref="Server.Transfer"/> on the source.
/// </summary>
/// <param name="TransferId">The transfer's id, the same on both nodes.</param>
/// <param name="PeerMemberId">The other node's member id.</param>
/// <param name="Direction"><see cref="TransferDirection.Out"/> on the source,
/// <see cref="TransferDirection.In"/> on the target.</param>
/// <param name="State"><see cref="TransferState.Ticketed"/> or <see cref="TransferState.Locked"/>.</param>
public sealed record ServerTransfer(
    string TransferId,
    string PeerMemberId,
    string Direction,
    string State);

/// <summary>
/// A transfer job's progress, carried on <see cref="Job.Transfer"/>.
/// </summary>
/// <param name="Phase">A <see cref="TransferPhase"/> value.</param>
/// <param name="PartId">The part being streamed while <see cref="TransferPhase.Transferring"/>; null
/// otherwise.</param>
/// <param name="BytesDone">Bytes received across every part so far, a part sent again counted from its
/// start.</param>
/// <param name="BytesTotal">The sum of every part's size from the manifest; 0 until the manifest is read.</param>
/// <param name="TargetInstanceId">The instance id the target minted, null until the import finishes.</param>
public sealed record JobTransfer(
    string Phase,
    string? PartId,
    long BytesDone,
    long BytesTotal,
    string? TargetInstanceId);

/// <summary>
/// The target's answer to the source asking how a transfer ended
/// (<c>GET /api/v1/members/self/transfers/{transferId}</c> on the target).
/// </summary>
/// <param name="State"><see cref="TransferState.Running"/>, <see cref="TransferState.Committed"/> or
/// <see cref="TransferState.Aborted"/>.</param>
/// <param name="TargetInstanceId">The instance the target holds once committed; null before.</param>
/// <param name="Reason">The refusal code an aborted transfer ended with, or null.</param>
public sealed record TransferOutcome(string State, string? TargetInstanceId, string? Reason = null);

/// <summary>
/// The optional body of the source's abort route: why the target gave up, and where.
/// </summary>
/// <param name="Reason">A <see cref="TransferRefusalCodes"/> value.</param>
/// <param name="Phase">The <see cref="TransferPhase"/> the target was in.</param>
public sealed record TransferAbortRequest(string? Reason, string? Phase);

/// <summary>The hash of one part as the source last sent it whole.</summary>
/// <param name="Sha256">Lower-case hex SHA-256 of the bytes written.</param>
public sealed record TransferPartHash(string Sha256);

/// <summary>
/// The cluster-bus payload of <see cref="TransferMessageTypes.Commit"/>, target to source: the target
/// holds the server now, so the source removes its copy.
/// </summary>
/// <param name="TransferId">The transfer committed.</param>
/// <param name="SourceInstanceId">The instance on the source.</param>
/// <param name="TargetInstanceId">The instance the target minted.</param>
public sealed record TransferCommit(string TransferId, string SourceInstanceId, string TargetInstanceId);

/// <summary>The cluster-bus message types a transfer sends.</summary>
public static class TransferMessageTypes
{
    /// <summary>The target tells the source a transfer committed.</summary>
    public const string Commit = "server.transfer.commit";
}

/// <summary>The request header carrying a transfer's ticket on the source's member routes.</summary>
public static class TransferHeaders
{
    /// <summary>The ticket, as minted.</summary>
    public const string Ticket = "KGSM-Transfer-Ticket";

    /// <summary>
    /// On the source's manifest and lock answers: the game name the source holds for the instance, absent
    /// when it holds none. Beside the manifest rather than in it, because the manifest is the engine's
    /// record and travels as the engine wrote it.
    /// </summary>
    public const string GameName = "KGSM-Transfer-Game-Name";
}

/// <summary>Which side of a transfer a node is.</summary>
public static class TransferDirection
{
    /// <summary>The source: the server leaves this node.</summary>
    public const string Out = "out";

    /// <summary>The target: the server arrives on this node.</summary>
    public const string In = "in";
}

/// <summary>A transfer's state on one node.</summary>
public static class TransferState
{
    /// <summary>Source: a ticket is minted and no lock taken.</summary>
    public const string Ticketed = "ticketed";

    /// <summary>Source: the engine holds the transfer lock.</summary>
    public const string Locked = "locked";

    /// <summary>Target: the job is under way.</summary>
    public const string Running = "running";

    /// <summary>The target holds the server; on the source, its copy is removed.</summary>
    public const string Committed = "committed";

    /// <summary>The transfer ended without moving the server.</summary>
    public const string Aborted = "aborted";

    /// <summary>Whether a transfer in this state is over.</summary>
    public static bool IsTerminal(string state) => state is Committed or Aborted;
}

/// <summary>Where a transfer job is.</summary>
public static class TransferPhase
{
    public const string Preflight = "preflight";
    public const string Locking = "locking";
    public const string Transferring = "transferring";
    public const string Importing = "importing";
    public const string Committing = "committing";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>
/// Why a transfer is refused or failed: the code in an error envelope, a preflight refusal, a failed
/// job's <c>error</c> and the <c>Reason</c> of a <c>server.transfer.failed</c> line.
/// </summary>
public static class TransferRefusalCodes
{
    public const string InstanceRunning = "instance_running";
    public const string InstanceLocked = "instance_locked";
    public const string TransferInProgress = "transfer_in_progress";
    public const string TicketInvalid = "ticket_invalid";
    public const string TargetNotNode = "target_not_node";
    public const string MemberUnreachable = "member_unreachable";
    public const string EngineVersionMismatch = "engine_version_mismatch";
    public const string RequirementMissing = "requirement_missing";
    public const string InsufficientDisk = "insufficient_disk";
    public const string PortConflict = "port_conflict";
    public const string IntegrityFailed = "integrity_failed";
    public const string StartFailed = "start_failed";
    public const string TransferCommitted = "transfer_committed";

    /// <summary>A part was asked for before the source took the lock.</summary>
    public const string TransferNotLocked = "transfer_not_locked";

    /// <summary>The target's API stopped while the transfer was under way; its recovery ended it.</summary>
    public const string TransferInterrupted = "transfer_interrupted";

    /// <summary>A person ended the transfer on the source.</summary>
    public const string TransferCancelled = "transfer_cancelled";
}
