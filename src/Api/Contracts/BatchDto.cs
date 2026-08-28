namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The request body for <c>POST /servers/commands</c> — one verb applied to a set of this host's
/// servers. The verb set is the same closed one a single command admits
/// (<see cref="CommandVerb.IsKnown"/>), because a batch is a <em>dispatcher</em>, not a second
/// command vocabulary: every member becomes an ordinary job running the ordinary path.
/// <para>
/// <see cref="RunId"/> is a client-minted correlation id. A selection can span several nodes, and
/// each node is a separate API that admits its own share; the client sends one id to all of them so
/// a person's single action can be reassembled afterwards from data the nodes hold, rather than only
/// in the browser that started it. This node stores it verbatim and never interprets it — it learns
/// nothing about any other node, and there is no coordinator.
/// </para>
/// <para>
/// <see cref="Origin"/> is the driving surface, exactly as on <see cref="CommandRequest"/>, and is
/// stamped onto every member's engine call so each one's audit row carries the same provenance a
/// hand-issued command would.
/// </para>
/// </summary>
/// <param name="Force">
/// Override the engine's node-capacity check for every member — <c>start</c> only, refused on any
/// other verb exactly as the single-command path refuses it. One decision for the whole batch rather
/// than one per member: a batch is a single intent applied N times, and an operator who has judged
/// that a blueprint's figure overstates what these games really use has judged it for the selection
/// they made. Absent ⇒ false, so the protection is what a caller gets by not asking.
/// <para>
/// It does not create memory, and a batch is where that bites hardest: forcing a selection the node
/// cannot fit does not fail one start, it invites the OOM killer to choose among everything running.
/// </para>
/// </param>
public sealed record BatchRequest(
    string? Verb,
    IReadOnlyList<string>? ServerIds,
    string? RunId = null,
    string? Origin = null,
    bool Force = false);

/// <summary>One server the batch would not accept, and the sentence explaining it — the same reason
/// text a single command's <c>409</c> would have carried.</summary>
public sealed record BatchRefusal(string ServerId, string Reason);

/// <summary>
/// The <c>202 Accepted</c> body. Both halves are stated: what this node took, and what it would not
/// take and why. A refusal is answered here, on arrival, rather than being discovered one member at
/// a time — the client asked about a set, so the answer is about the set.
/// </summary>
public sealed record BatchAccepted(
    string BatchId,
    string? RunId,
    string Verb,
    IReadOnlyList<string> Admitted,
    IReadOnlyList<BatchRefusal> Refused);

/// <summary>
/// One server's place in a batch. <see cref="JobId"/> is present for every admitted member from the
/// moment the batch is accepted — jobs are created up front, not when the worker reaches them, so
/// queued work is visible everywhere running work is. A refused member never has one.
/// </summary>
/// <remarks>
/// <see cref="QueuedPosition"/> is the member's stable ordinal within its batch (1-based, in the
/// order the client asked), not a live countdown. It answers "which of these moves next" without
/// re-publishing every other member's frame each time one settles, and it is deliberately a
/// <b>count, not a clock</b>: no completion time is offered, because how long a verb takes is not
/// something this API has measured.
/// </remarks>
public sealed record BatchMember(
    string ServerId,
    string State,
    string? JobId,
    int? QueuedPosition,
    string? Error,
    DateTimeOffset? SettledAt);

/// <summary>How a batch's members are distributed across the member states, for a surface that wants
/// the shape of a run without walking every row.</summary>
public sealed record BatchCounts(
    int Total,
    int Pending,
    int Running,
    int Succeeded,
    int Failed,
    int Refused,
    int Cancelled,
    int Unknown);

/// <summary>One batch, with its members — what <c>GET /batches</c> and <c>GET /batches/{id}</c> return.</summary>
public sealed record BatchView(
    string Id,
    string? RunId,
    string Verb,
    string State,
    string? Actor,
    string Origin,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SettledAt,
    BatchCounts Counts,
    IReadOnlyList<BatchMember> Members);

/// <summary>The <c>GET /batches</c> envelope.</summary>
public sealed record BatchList(IReadOnlyList<BatchView> Data);

/// <summary>
/// The <c>DELETE /batches/{id}</c> body: what the cancel actually stopped. A kgsm invocation already
/// under way is not interruptible, so members that were running are named as still running rather
/// than implying a clean halt.
/// </summary>
public sealed record BatchCancelled(
    string BatchId,
    IReadOnlyList<string> Cancelled,
    IReadOnlyList<string> StillRunning);

/// <summary>
/// A batch's own lifecycle. Only two states are meaningful to a reader: it is either still going to
/// do something, or it is finished. Anything finer is already on the members.
/// </summary>
public static class BatchState
{
    /// <summary>At least one member is pending or running.</summary>
    public const string Active = "active";

    /// <summary>Every member has reached a terminal state.</summary>
    public const string Settled = "settled";
}

/// <summary>
/// Where one server stands inside a batch.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the state that keeps the record honest across a restart. A member the
/// worker had started has no job record afterwards — <c>JobRegistry</c> is in memory — and the kgsm
/// invocation was a child of the process that died. Calling that <see cref="Failed"/> would claim an
/// outcome nobody observed, and re-running it could restart a server somebody deliberately stopped,
/// so it settles here instead and says so.
/// </remarks>
public static class BatchMemberState
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>Never admitted — the gate, the in-flight guard or an unknown id turned it away.</summary>
    public const string Refused = "refused";

    /// <summary>Cancelled before it ran.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Started, then the process holding its job ended. The engine is the only authority on
    /// what actually happened, and it did not say.</summary>
    public const string Unknown = "unknown";

    /// <summary>Is this a state the member will not leave? The worker only ever picks up
    /// <see cref="Pending"/>, and the batch settles when nothing is left that isn't terminal.</summary>
    public static bool IsTerminal(string state) =>
        state is Succeeded or Failed or Refused or Cancelled or Unknown;
}
