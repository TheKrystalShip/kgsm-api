namespace TheKrystalShip.Api.Data;

/// <summary>
/// One batch: a verb, and the set of this host's servers it was asked to run against.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a table rather than a list in memory because a batch outlives the request that started
/// it.</b> Ten servers updated two at a time is a half-hour of work, and nothing about it should
/// depend on the browser that fired it staying open — or on this process staying up. A run held only
/// in a client is a run that ends when a tab closes.
/// </para>
/// <para>
/// <b>EnsureCreated, NOT a migration</b> (the project's dev authority — see <see cref="AppDbContext"/>).
/// On a fresh database the model creates this; on an already-deployed one
/// <see cref="Services.Commands.BatchStore"/>'s idempotent <c>CREATE TABLE IF NOT EXISTS</c> adds it,
/// so the shared audit log is never wiped to gain a table.
/// </para>
/// </remarks>
public sealed class BatchEntity
{
    public string Id { get; set; } = "";

    /// <summary>
    /// The client-minted correlation id shared by every node taking part in one person's action.
    /// Stored verbatim and never interpreted: it is what lets a cluster-wide run be reassembled from
    /// the nodes afterwards, without any node knowing another exists.
    /// </summary>
    public string? RunId { get; set; }

    public string Verb { get; set; } = "";

    /// <summary><see cref="Contracts.BatchState"/>.</summary>
    public string State { get; set; } = "";

    /// <summary>The bearer identity that asked for it, stamped onto every member's engine call.</summary>
    public string? Actor { get; set; }

    /// <summary>The declared driving surface, stamped the same way.</summary>
    public string Origin { get; set; } = "";

    /// <summary>
    /// Whether this batch overrides the engine's node-capacity check.
    /// </summary>
    /// <remarks>
    /// Stored rather than held in memory because the worker reaches most members long after the
    /// request that asked for it, and a member that ran without the override its batch was granted
    /// would be refused for a reason its operator had already answered.
    /// </remarks>
    public bool Force { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the last member reached a terminal state. Null while the batch is active.</summary>
    public DateTimeOffset? SettledAt { get; set; }
}

/// <summary>
/// One server's place in a batch — the durable half of what the in-memory
/// <see cref="Services.Commands.JobRegistry"/> holds for the moment a member is actually running.
/// </summary>
/// <remarks>
/// The two are not redundant. The registry answers "what is happening right now" and is rebuilt from
/// nothing on every start; this answers "what was this batch asked to do, and how far did it get",
/// which has to survive the restart that empties the registry.
/// </remarks>
public sealed class BatchMemberEntity
{
    public string BatchId { get; set; } = "";

    public string ServerId { get; set; } = "";

    /// <summary><see cref="Contracts.BatchMemberState"/>.</summary>
    public string State { get; set; } = "";

    /// <summary>
    /// The job created for this member when the batch was accepted. Present for every admitted
    /// member from the start — jobs exist while work is queued, not only once it runs — and null
    /// only for a member that was refused and therefore never had one.
    /// </summary>
    public string? JobId { get; set; }

    /// <summary>The member's stable 1-based ordinal within its batch, in the order the client asked.
    /// Null for a refused member, which never joins the queue.</summary>
    public int? Position { get; set; }

    /// <summary>The refusal's reason, or the engine's real failure detail — never a fabricated
    /// summary of either.</summary>
    public string? Error { get; set; }

    public DateTimeOffset? SettledAt { get; set; }
}
