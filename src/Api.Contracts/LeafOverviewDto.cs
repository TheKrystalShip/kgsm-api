using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One instruction for <c>POST /hosts/{id}/services/scheduler/windows/{action}</c>.
/// </summary>
/// <param name="Instance">The kgsm instance the window belongs to.</param>
/// <param name="Window">The window's schedule expression, which is its id — <c>weekly.sun@04:00</c>. One
/// instance holds several appointments, so an instruction that names none is refused rather than guessed at.</param>
/// <param name="Minutes">How far a <c>postpone</c> moves the window, 1–720. Ignored by the other verbs;
/// an hour when it is absent.</param>
public sealed record SchedulerWindowAction(string? Instance, string? Window, int? Minutes = null);

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
/// <param name="Phase">Supervision phase — <c>running｜restart-pending｜maintenance｜stopped｜failed｜unknown</c>.
/// <c>maintenance</c> is a leaf holding the instance out of service for a window: drained on purpose, with
/// <see cref="Desired"/> still <c>running</c> and crash-restart suppressed until it is released.</param>
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
