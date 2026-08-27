namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// Settings for one server instance — a typed façade over kgsm instance config joined with watchdog
/// desired-state and the scheduler leaf's own reading of the maintenance windows.
/// </summary>
/// <remarks>
/// Nothing here is fabricated: a field is <c>null</c> when its backing authority is absent or unreachable,
/// or when the kgsm config key is unset — never defaulted to a guess.
/// </remarks>
/// <param name="ServerId">The kgsm instance id these settings belong to.</param>
/// <param name="AutoUpdate">The <c>auto_update</c> config key.</param>
/// <param name="Autostart">Whether the watchdog starts this instance at boot. Null when the watchdog is
/// absent or unreachable — a missing entry and a down daemon are not the same fact.</param>
/// <param name="CpuPriority"><c>low｜normal｜high</c>, from the <c>cpu_priority</c> config key.</param>
/// <param name="MemoryCapMb">The <c>memory_cap_mb</c> config key; 0 is uncapped.</param>
/// <param name="MaintenanceWindows">Every window written on this instance, in the order it is written,
/// each carrying the leaf's next fire and last run when the leaf is reachable.</param>
/// <param name="Timezone">The IANA zone appointments are read in. Intervals ignore it by construction.</param>
/// <param name="BackupRetention">How many archives a scheduled prune keeps.</param>
/// <param name="CrashRestart">Whether the watchdog restarts this instance after a crash.</param>
/// <param name="CrashMaxRestarts">How many consecutive crash-restarts it attempts before giving up.</param>
public sealed record ServerSettings(
    string ServerId,
    bool AutoUpdate,
    bool? Autostart,
    string? CpuPriority,
    int? MemoryCapMb,
    IReadOnlyList<MaintenanceWindowDto> MaintenanceWindows,
    string? Timezone,
    int? BackupRetention,
    bool? CrashRestart,
    int? CrashMaxRestarts);

/// <summary>
/// One maintenance window: an appointment plus the ordered set of tasks that run when it fires.
/// </summary>
/// <remarks>
/// <para>
/// <b>The id is the schedule expression.</b> <c>weekly.sun@04:00</c> names the window for postpone, skip
/// and run-now — unique within an instance, stable across edits to the task set, and stored nowhere
/// because it is derived. Editing the schedule produces a different window.
/// </para>
/// <para>
/// <b>Two authorities, joined.</b> The schedule and the tasks are read out of kgsm config with the
/// ecosystem's one parser; the next fire and the last run come from the scheduler leaf, and are null when
/// it is absent or unreachable. <see cref="Valid"/>/<see cref="Error"/> is the leaf's verdict where it has
/// one — it also refuses a window this host's policy forbids — and the parser's otherwise.
/// </para>
/// </remarks>
/// <param name="Id">The schedule expression, in canonical form.</param>
/// <param name="Expression">The whole window — <c>&lt;schedule&gt;/&lt;tasks&gt;</c>. What a PATCH sends back.</param>
/// <param name="Kind"><c>appointment</c> (timezone-anchored) or <c>interval</c> (epoch-aligned, timezone-free).</param>
/// <param name="Tasks"><c>backup</c>, <c>update</c>, <c>restart</c> — always in canonical run order.</param>
/// <param name="Valid">Whether the window will fire.</param>
/// <param name="Error">What stops it, naming the offending text. Null when <see cref="Valid"/>.</param>
/// <param name="NextFireUtc">The next fire, from the scheduler. Null on an invalid window, and null when
/// the leaf could not be asked — <see cref="Valid"/> beside it is what tells the two apart.</param>
/// <param name="LastRun">The leaf's record of the last run, or null when it holds none.</param>
public sealed record MaintenanceWindowDto(
    string Id,
    string Expression,
    string Kind,
    IReadOnlyList<string> Tasks,
    bool Valid,
    string? Error,
    DateTimeOffset? NextFireUtc,
    MaintenanceRunDto? LastRun);

/// <summary>
/// One window run, as the scheduler recorded it.
/// </summary>
/// <param name="StartedUtc">When the run opened.</param>
/// <param name="FinishedUtc">When it closed.</param>
/// <param name="Outcome"><c>ok｜failed｜skipped｜aborted</c> for the window as a whole.</param>
/// <param name="Tasks">One row per task the run got to, in the order it ran them.</param>
public sealed record MaintenanceRunDto(
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc,
    string Outcome,
    IReadOnlyList<MaintenanceTaskRunDto> Tasks);

/// <summary>
/// One task inside a run. The four outcomes are carried as they were recorded — a <c>skipped</c> task did
/// not apply and a <c>failed</c> one was owed, and reading either as the other misstates what happened.
/// </summary>
/// <param name="Name"><c>backup</c>, <c>update</c> or <c>restart</c>.</param>
/// <param name="Outcome"><c>ok｜failed｜skipped｜aborted</c>.</param>
/// <param name="Message">The daemon's own words for why. Null when it had nothing to say.</param>
public sealed record MaintenanceTaskRunDto(string Name, string Outcome, string? Message);

/// <summary>
/// PATCH body for <c>PATCH /servers/{id}/settings</c>. Sparse: only non-null fields are applied.
/// </summary>
/// <remarks>
/// <para>
/// Each field is one kgsm config key, except <see cref="Autostart"/>, which is watchdog desired-state.
/// The scheduler leaf re-reads kgsm config as its source of truth, so persisting a key is the whole apply
/// — this API never pushes a schedule at the daemon.
/// </para>
/// <para>
/// <b><see cref="MaintenanceWindows"/> is replaced wholesale.</b> It is a list of window expressions
/// (<c>daily@05:00/backup</c>), and sending it replaces every window on the instance with exactly what it
/// carries. That is the only way to express deleting a window, which a sparse field-by-field patch cannot;
/// an empty list is "no maintenance". Each expression is read with the ecosystem's one parser, and an
/// expression that will not fire is a 400 carrying the parse error rather than a window that silently
/// never runs.
/// </para>
/// </remarks>
public sealed record ServerSettingsPatch(
    bool? AutoUpdate,
    bool? Autostart,
    string? CpuPriority,
    int? MemoryCapMb,
    IReadOnlyList<string>? MaintenanceWindows,
    string? Timezone,
    int? BackupRetention,
    bool? CrashRestart = null,
    int? CrashMaxRestarts = null,
    string? Origin = null);

/// <summary>
/// The <c>PATCH /servers/{id}/settings</c> success body: the camelCase field names that were applied, plus
/// the fresh post-write settings (so the client need not re-GET). Returned on a fully-applied <c>200</c>.
/// </summary>
public sealed record ServerSettingsApplied(IReadOnlyList<string> Applied, ServerSettings Settings);

/// <summary>
/// Request body for <c>POST /servers/{id}/settings/maintenance/preview</c> — what a candidate window would
/// do, before anybody saves it.
/// </summary>
/// <param name="Expression">One window expression, e.g. <c>weekly.sun@04:00/backup,restart</c>.</param>
/// <param name="Count">How many fires to return. Defaults to five and is clamped to twenty — an editor
/// shows the next few, and past that the arithmetic answers a question nobody asked.</param>
/// <param name="Timezone">The IANA zone to read the appointment in. Defaults to the instance's own, so a
/// preview of an unsaved timezone change is possible without saving it first. Intervals ignore it.</param>
public sealed record MaintenancePreviewRequest(
    string? Expression,
    int? Count = null,
    string? Timezone = null);

/// <summary>
/// What a candidate window means: how it was read, and when it would fire.
/// </summary>
/// <remarks>
/// The fires are computed with the same clock the scheduler fires on, which is the whole reason the parser
/// and the arithmetic live in kgsm-lib rather than in either process — a preview and a daemon that
/// disagreed across a daylight-saving boundary would each be telling the truth about different code.
/// </remarks>
/// <param name="Id">The window's identity — its schedule expression in canonical form.</param>
/// <param name="Expression">The whole window in canonical form, tasks in canonical run order.</param>
/// <param name="Kind"><c>appointment</c> or <c>interval</c>.</param>
/// <param name="Tasks">The tasks it would run, in canonical order.</param>
/// <param name="Valid">Whether the expression was read successfully.</param>
/// <param name="Error">What stopped it being read, naming the offending text. Null when <see cref="Valid"/>.</param>
/// <param name="Timezone">The zone the fires were computed in.</param>
/// <param name="Fires">Ascending UTC instants. Empty for a window that could not be read — an invalid
/// window has no next fire, and saying so is what distinguishes it from one that is simply not due.</param>
public sealed record MaintenancePreviewResult(
    string Id,
    string Expression,
    string Kind,
    IReadOnlyList<string> Tasks,
    bool Valid,
    string? Error,
    string Timezone,
    IReadOnlyList<DateTimeOffset> Fires);
