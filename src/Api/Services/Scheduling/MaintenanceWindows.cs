using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Api.Services.Scheduling;

/// <summary>
/// What this API knows about a maintenance window on top of what kgsm-lib's parser and clock say: how to
/// render one onto the wire, which of its tasks interrupt the people on a server, how often it comes
/// round, and which of them this host will refuse to write.
/// </summary>
/// <remarks>
/// <b>The grammar itself is never re-implemented here.</b> Reading an expression, rendering it back, and
/// timing it are <see cref="MaintenanceWindowParser"/>'s and <see cref="ScheduleClock"/>'s — the
/// ecosystem's one implementation, which is exactly what lets this API refuse a window the scheduler
/// would also refuse, and preview a fire the scheduler will actually make.
/// </remarks>
public static class MaintenanceWindows
{
    /// <summary>The wire token for a schedule kind.</summary>
    public static string KindToken(MaintenanceScheduleKind kind) =>
        kind == MaintenanceScheduleKind.Interval ? "interval" : "appointment";

    /// <summary>The window's tasks as the grammar's tokens, in canonical run order.</summary>
    public static IReadOnlyList<string> TaskTokens(MaintenanceWindow window) =>
        [.. window.Tasks.Select(t => t.ToToken())];

    /// <summary>
    /// Whether a task interrupts the people on a server.
    /// </summary>
    /// <remarks>
    /// A backup runs against a live server — kgsm records the state an archive was captured in — so it
    /// takes nobody offline and there is nothing true to warn about. An update and a restart both bounce
    /// the instance, which is what a countdown is for.
    /// </remarks>
    public static bool IsDisruptive(MaintenanceTask task) =>
        task is MaintenanceTask.Update or MaintenanceTask.Restart;

    /// <summary>Whether anything in this window interrupts the people on the server.</summary>
    public static bool IsDisruptive(MaintenanceWindow window) => window.Tasks.Any(IsDisruptive);

    /// <summary>
    /// What a countdown for this window would say is about to happen — <c>restarting</c>, or
    /// <c>updating and restarting</c> where an update is what makes the new build the running one. Null for
    /// a window that interrupts nobody, which has no true sentence to say about it.
    /// </summary>
    public static string? DisruptionReason(MaintenanceWindow window)
    {
        if (window.Runs(MaintenanceTask.Update)) return "updating and restarting";
        if (window.Runs(MaintenanceTask.Restart)) return "restarting";
        return null;
    }

    /// <summary>
    /// How often the window comes round. An interval carries its own span; an appointment's is the length
    /// of its cadence, and a month is measured at its shortest so the answer is never longer than the real
    /// gap. Null for a window that could not be read, which comes round never.
    /// </summary>
    public static TimeSpan? PeriodOf(MaintenanceWindow window)
    {
        if (!window.IsValid) return null;
        if (window.Interval is { } interval) return interval;

        return window.Cadence switch
        {
            AppointmentCadence.Daily => TimeSpan.FromDays(1),
            AppointmentCadence.Weekly => TimeSpan.FromDays(7),
            AppointmentCadence.Monthly => TimeSpan.FromDays(28),
            _ => null,
        };
    }

    /// <summary>
    /// Why this host will not write <paramref name="window"/> against <paramref name="isContainer"/>, or
    /// null when it will.
    /// </summary>
    /// <remarks>
    /// Every disruptive task is issued through the watchdog, and the watchdog supervises native instances
    /// alone — so a container's <c>update</c> or <c>restart</c> is a window that can only ever record a
    /// skipped task. The scheduler declines it at run time whatever is written; refusing it here is what
    /// makes an operator hear about it while they are writing it rather than a week later.
    /// </remarks>
    public static string? Refusal(MaintenanceWindow window, bool isContainer)
    {
        if (!window.IsValid)
            return window.Error;

        if (!isContainer)
            return null;

        string[] refused = [.. window.Tasks.Where(IsDisruptive).Select(t => t.ToToken())];
        return refused.Length == 0
            ? null
            : $"'{window.ToExpression()}' names {string.Join(" and ", refused)}, which the watchdog "
              + "performs and the watchdog supervises native instances only — a container window can carry backup alone";
    }

    /// <summary>
    /// One instance's windows on the wire: the schedule and tasks read from kgsm config, joined by window
    /// id with the scheduler leaf's next fire and last run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// kgsm config is the source of truth for what windows exist — the leaf reads the same value, so a
    /// window written a second ago is on this list before the daemon's next poll has noticed it, with an
    /// honestly null next fire until it does.
    /// </para>
    /// <para>
    /// Validity prefers the leaf's verdict where it has one: it refuses a window that parses fine and this
    /// host's policy still will not fire (a period below the host's floor, a task this daemon does not
    /// run), and that refusal exists nowhere else.
    /// </para>
    /// </remarks>
    /// <param name="packed">The instance's <c>maintenance_windows</c> config value.</param>
    /// <param name="leaf">The scheduler's row for this instance, or null when it could not be asked.</param>
    public static IReadOnlyList<MaintenanceWindowDto> Project(string? packed, SchedulerInstanceStatus? leaf)
    {
        IReadOnlyList<MaintenanceWindow> windows = MaintenanceWindowParser.Parse(packed);
        if (windows.Count == 0)
            return [];

        var byId = new Dictionary<string, SchedulerWindowStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (SchedulerWindowStatus w in leaf?.Windows ?? [])
            byId[w.Id] = w;

        var projected = new List<MaintenanceWindowDto>(windows.Count);
        foreach (MaintenanceWindow window in windows)
        {
            byId.TryGetValue(window.Id, out SchedulerWindowStatus? row);

            bool valid = row?.Valid ?? window.IsValid;
            string? error = row is not null ? row.Error : window.Error;

            projected.Add(new MaintenanceWindowDto(
                Id: window.Id,
                Expression: window.ToExpression(),
                Kind: KindToken(window.Kind),
                Tasks: TaskTokens(window),
                Valid: valid,
                Error: error,
                NextFireUtc: row?.NextFireUtc,
                LastRun: ProjectRun(row?.LastRun)));
        }

        return projected;
    }

    private static MaintenanceRunDto? ProjectRun(SchedulerWindowRun? run)
    {
        if (run is null) return null;

        IReadOnlyList<MaintenanceTaskRunDto> tasks =
        [
            .. (run.Tasks ?? []).Select(t => new MaintenanceTaskRunDto(
                t.Name, Outcome(t.Outcome), string.IsNullOrWhiteSpace(t.Message) ? null : t.Message))
        ];

        return new MaintenanceRunDto(run.StartedUtc, run.FinishedUtc, Outcome(run.Outcome), tasks);
    }

    // The daemon writes one of four words. Anything else is a daemon this build does not understand, and
    // reading it as "ok" would report a success nobody measured.
    private static string Outcome(string? recorded) =>
        recorded is MaintenanceOutcome.Ok or MaintenanceOutcome.Failed
            or MaintenanceOutcome.Skipped or MaintenanceOutcome.Aborted
            ? recorded
            : "unknown";
}
