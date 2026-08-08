using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The scheduler leaf's whole board — every instance it supervises, with the cadence it was configured
/// with and the outcome of what it last ran. The per-server settings surface reads ONE row of this same
/// snapshot; this serves it entire, because "what is due next across this host" is a question no
/// per-server view can answer.
/// <para>
/// Relayed as the leaf reports it. Every computed field (<c>nextFireUtc</c>, <c>nextBackupUtc</c>) is the
/// scheduler's own arithmetic over its own clock — this API re-derives nothing, so the panel and the leaf
/// can never disagree about when something fires. A null is the leaf's honest "not scheduled" or "hasn't
/// run yet", never a gap this API filled in.
/// </para>
/// </summary>
public sealed record SchedulerBoard(IReadOnlyList<Services.Leaves.SchedulerInstanceStatus> Data);

/// <summary>
/// The watchdog leaf's supervision table: what it intends for each instance, what the kernel says is
/// actually true, and why it last changed its mind.
/// <para>
/// <see cref="Ready"/> is the supervisor's own readiness — whether it is in-slice and able to spawn at all
/// — which is a separate axis from the rows: a daemon that is up but cannot supervise reports
/// <c>ready:false</c> with every instance still tabled. Reported first for that reason.
/// </para>
/// </summary>
/// <param name="Ready">The supervisor is in-slice and able to spawn. Null when it could not be asked.</param>
/// <param name="Detail">The supervisor's own words for its readiness (the precise reason when not ready).</param>
/// <param name="Data">One row per supervised instance, in the daemon's own order.</param>
public sealed record WatchdogSupervision(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Ready,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail,
    IReadOnlyList<SupervisedInstance> Data);

/// <summary>
/// One instance under watchdog supervision. The pairing that matters is <see cref="Desired"/> against
/// <see cref="Populated"/>: the first is the intent the daemon holds, the second is measured from the
/// instance's <c>cgroup.events</c> by the kernel. They disagreeing is the single most actionable fact this
/// API can report about a native server, and neither is ever derived from the other.
/// </summary>
/// <param name="Name">The instance name.</param>
/// <param name="Desired">Runtime intent — <c>running</c> or <c>stopped</c>.</param>
/// <param name="Phase">Supervision phase — <c>running｜restart-pending｜stopped｜failed｜unknown</c>.</param>
/// <param name="Populated">Measured liveness from the cgroup, never inferred from the phase.</param>
/// <param name="Enabled">In the persisted boot-autostart set. Orthogonal to whether it is running now.</param>
/// <param name="Pid">The spawned leader pid, when known; null when not running or unknown.</param>
/// <param name="Restarts">Consecutive-failure streak since last stability — 0 when healthy.</param>
/// <param name="Reason">The daemon's own words for the last transition (e.g. <c>"crashed (exit 139);
/// restart in 2s"</c>). Empty when it has nothing to say, never filled in.</param>
public sealed record SupervisedInstance(
    string Name,
    string Desired,
    string Phase,
    bool Populated,
    bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Pid,
    int Restarts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);
