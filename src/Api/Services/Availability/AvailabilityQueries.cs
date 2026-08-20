using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Availability;

/// <summary>
/// Folds the engine's lifecycle events into per-server uptime — the read behind
/// <c>GET /servers/availability</c>. Pure query logic over an <see cref="IEventJournalHistory"/>, so it
/// is testable against a fake reader with no live kgsm.
/// </summary>
/// <remarks>
/// <para><b>Why the journal.</b> Uptime is a property of what happened, and what happened is already on
/// disk. Deriving it here means no sampler, no extra table, and no daemon: a host whose optional leaves
/// are all absent still answers. It also means the answer is only as old as the journal's retention,
/// which the report states rather than papers over.</para>
/// <para><b>Why not <c>Server.StartedAt</c>.</b> That field is one instant about now; availability is a
/// span about the past. A roster reading cannot say whether a server was down on Tuesday, and it is
/// null in practice anyway (see <see cref="Server.StartedAt"/>).</para>
/// </remarks>
public static class AvailabilityQueries
{
    /// <summary>The default window when the caller names none.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    /// <summary>The longest window this will answer for — beyond it the scan stops being cheap.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(90);

    /// <summary>
    /// How far before the window to read, purely to learn each server's state as the window opens.
    /// An idle fleet emits nothing for days, so without this a Monday-morning read of a week-long
    /// window would find no events and report an unmeasured fleet — while the journal plainly shows
    /// every server was started a fortnight ago and never stopped.
    /// </summary>
    private static readonly TimeSpan SeedLookback = TimeSpan.FromDays(60);

    /// <summary>Pages of one event type this walks before giving up. Bounds the work on a busy host.</summary>
    private const int MaxPagesPerType = 12;

    private const int PageSize = 1000;

    /// <summary>
    /// The raw engine event types that move a server between "meant to be running" and "actually
    /// running". Everything else in the journal — ports, backups, players, thresholds — says nothing
    /// about either and is not read.
    /// </summary>
    internal static readonly string[] LifecycleTypes =
    [
        "instance_started",
        "instance_ready",
        "instance_stopped",
        "instance_restarted",
        "instance_crashed",
        "instance_failed",
        // The middle of a deliberate restart: the old run is down and the new one is not spawned yet.
        // Audit-silent (the catalog weighs it Phase, so no dotted action is shaped from it) and read
        // here anyway — a player could not connect during it, and downtime is measured from what
        // happened rather than from what the feed chose to print.
        "instance_restart_stopped",
        "instance_installed",
        "instance_uninstalled",
    ];

    /// <summary>
    /// Parse a window label (<c>24h</c>, <c>7d</c>, <c>30d</c>) into a span, clamped to
    /// <see cref="MaxWindow"/>. An unparseable or absent label yields <see cref="DefaultWindow"/>.
    /// </summary>
    public static TimeSpan ParseWindow(string? window)
    {
        if (string.IsNullOrWhiteSpace(window)) return DefaultWindow;

        string w = window.Trim().ToLowerInvariant();
        char unit = w[^1];
        if (!int.TryParse(w[..^1], out int n) || n <= 0) return DefaultWindow;

        TimeSpan span = unit switch
        {
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => DefaultWindow,
        };
        return span > MaxWindow ? MaxWindow : span;
    }

    /// <summary>
    /// Build the report for <paramref name="instanceIds"/> over <paramref name="window"/> ending at
    /// <paramref name="now"/>. A null or unreadable journal returns a fully-null report with
    /// <see cref="AvailabilityReport.EngineHistoryDegraded"/> set — never zeros.
    /// </summary>
    public static async Task<AvailabilityReport> BuildAsync(
        IEventJournalHistory? journal,
        IReadOnlyCollection<string> instanceIds,
        TimeSpan window,
        string windowLabel,
        DateTimeOffset now,
        CancellationToken ct)
    {
        DateTimeOffset windowStart = now - window;

        if (journal is null)
            return Degraded(windowLabel, windowStart, now, instanceIds);

        (List<EventHistoryEntry> events, DateTimeOffset? coverageFrom, bool truncated, bool readable) =
            await ReadLifecycleAsync(journal, windowStart - SeedLookback, now, ct).ConfigureAwait(false);

        if (!readable)
            return Degraded(windowLabel, windowStart, now, instanceIds);

        // The journal cannot answer for a moment before its oldest surviving segment, so the measured
        // span starts there when the window reaches further back. Reporting the requested start while
        // measuring from later is exactly the overclaim CoverageFrom exists to prevent.
        DateTimeOffset from = coverageFrom is { } cf && cf > windowStart ? cf : windowStart;

        Dictionary<string, List<EventHistoryEntry>> byInstance = new(StringComparer.Ordinal);
        foreach (string id in instanceIds) byInstance[id] = [];
        foreach (EventHistoryEntry e in events)
            if (e.Instance is { } inst && byInstance.TryGetValue(inst, out List<EventHistoryEntry>? bucket))
                bucket.Add(e);

        List<ServerAvailability> rows = [];
        foreach (string id in instanceIds.OrderBy(static i => i, StringComparer.Ordinal))
            rows.Add(Fold(id, byInstance[id], from, now));

        return new AvailabilityReport(
            windowLabel, from, now, coverageFrom, truncated, EngineHistoryDegraded: false,
            rows, Roll(rows));
    }

    /// <summary>
    /// The time-weighted rollup. Servers with no denominator contribute nothing and are not counted —
    /// including them as 100% would let an idle fleet report perfect availability it never earned.
    /// </summary>
    internal static FleetAvailability Roll(IReadOnlyList<ServerAvailability> rows)
    {
        long intended = 0, down = 0;
        int outages = 0, counted = 0;
        foreach (ServerAvailability r in rows)
        {
            intended += r.IntendedSeconds;
            down += r.DowntimeSeconds;
            outages += r.Outages;
            if (r.IntendedSeconds > 0) counted++;
        }

        double? availability = intended > 0
            ? Math.Clamp((intended - down) / (double)intended, 0d, 1d)
            : null;
        return new FleetAvailability(availability, intended, down, outages, counted);
    }

    /// <summary>
    /// Replay one server's events into intended-up seconds, unintended-down seconds and outage count.
    /// </summary>
    /// <remarks>
    /// Two independent booleans, deliberately: <em>intent</em> (does anything want this running) and
    /// <em>reality</em> (is it running). A crash moves only the second — which is the whole point, since
    /// a crash is downtime precisely because intent did not change. A stop moves both, which is why a
    /// deliberately stopped server accrues neither numerator nor denominator.
    /// </remarks>
    internal static ServerAvailability Fold(
        string id, List<EventHistoryEntry> events, DateTimeOffset from, DateTimeOffset to)
    {
        events.Sort(static (a, b) =>
        {
            int c = a.Ts.CompareTo(b.Ts);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });

        bool intendedUp = false, actuallyUp = false, gone = false;
        // `known` gates accrual: nothing accrues across a span whose state was never established.
        // `seededAtStart` is the narrower fact the report publishes — that the state as the window
        // OPENED came from a real event before it, rather than from the first thing to happen inside.
        bool known = false, seededAtStart = false;
        long intendedSec = 0, downSec = 0;
        int outages = 0;
        DateTimeOffset? lastOutage = null;
        DateTimeOffset cursor = from;

        void Accrue(DateTimeOffset until)
        {
            if (gone || !known || !intendedUp) return;
            DateTimeOffset a = cursor < from ? from : cursor;
            DateTimeOffset b = until > to ? to : until;
            if (b <= a) return;
            long sec = (long)(b - a).TotalSeconds;
            intendedSec += sec;
            if (!actuallyUp) downSec += sec;
        }

        foreach (EventHistoryEntry e in events)
        {
            if (e.Ts > to) break;

            // Before the window, events only establish state; the accrual clamps to [from, to] anyway,
            // and `seeded` is what records that the state at `from` was learned rather than assumed.
            if (e.Ts > from) Accrue(e.Ts);
            else { known = true; seededAtStart = true; }

            bool wasDown = intendedUp && !actuallyUp;
            bool unplanned = false;
            switch (e.Type)
            {
                case "instance_started":
                case "instance_restarted":
                    intendedUp = true; actuallyUp = true; gone = false; break;
                // Ready is the moment people can connect. It cannot lower intent, and it implies it:
                // nothing finishes loading that was not wanted up.
                case "instance_ready":
                    intendedUp = true; actuallyUp = true; gone = false; break;
                case "instance_stopped":
                    intendedUp = false; actuallyUp = false; break;
                // A crash and a give-up leave intent alone on purpose — the supervisor still wants this
                // running, which is what makes the gap that follows an outage rather than idle time.
                case "instance_crashed":
                case "instance_failed":
                    actuallyUp = false; unplanned = true; break;
                // The same shape, deliberately NOT flagged unplanned: intent never wavered and the gap
                // is somebody pressing Restart. It costs availability, because the server really was
                // unreachable, and it is not an incident.
                case "instance_restart_stopped":
                    actuallyUp = false; break;
                case "instance_installed":
                    intendedUp = false; actuallyUp = false; gone = false; break;
                // An uninstalled instance stops having uptime at all. Accruing past this would charge a
                // server for the time after it ceased to exist.
                case "instance_uninstalled":
                    intendedUp = false; actuallyUp = false; gone = true; break;
            }

            bool nowDown = intendedUp && !actuallyUp;
            if (nowDown && !wasDown && !gone && unplanned && e.Ts >= from)
            {
                outages++;
                lastOutage = e.Ts;
            }

            cursor = e.Ts;
            known = true;
        }

        Accrue(to);

        double? availability = intendedSec > 0
            ? Math.Clamp((intendedSec - downSec) / (double)intendedSec, 0d, 1d)
            : null;
        return new ServerAvailability(id, availability, intendedSec, downSec, outages, lastOutage, seededAtStart);
    }

    /// <summary>
    /// Every lifecycle event in <c>[from, to]</c>, read one type at a time. Typed queries rather than one
    /// unfiltered walk because the journal also carries player, port, backup and threshold events — a
    /// week of those is orders of magnitude more lines than the handful this needs.
    /// </summary>
    private static async Task<(List<EventHistoryEntry> Events, DateTimeOffset? CoverageFrom, bool Truncated, bool Readable)>
        ReadLifecycleAsync(IEventJournalHistory journal, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        List<EventHistoryEntry> all = [];
        DateTimeOffset? coverageFrom = null;
        bool truncated = false;
        bool anyReadable = false;

        long fromMs = from.ToUnixTimeMilliseconds();
        long toMs = to.ToUnixTimeMilliseconds();

        foreach (string type in LifecycleTypes)
        {
            long? beforeTs = null;
            string? beforeId = null;

            for (int page = 0; page < MaxPagesPerType; page++)
            {
                ct.ThrowIfCancellationRequested();

                EventHistoryPage p = await journal.QueryAsync(new EventHistoryQuery
                {
                    Type = type,
                    SinceMs = fromMs,
                    UntilMs = toMs,
                    BeforeTsMs = beforeTs,
                    BeforeId = beforeId,
                    Limit = PageSize,
                }, ct).ConfigureAwait(false);

                if (!p.JournalReadable) break;
                anyReadable = true;
                truncated |= p.Truncated;

                // Coverage is the ENGINE journal's, not the merged page's. The page collapses every
                // producer it read — assistant, bot, monitor — into one conservative floor, and those
                // leaves were provisioned long after kgsm and carry none of these event types. Taking
                // that floor would clamp the window to the newest leaf's first day and report a fleet
                // as unmeasured over days the engine has full history for.
                if (CoverageOf(p) is { } cf && (coverageFrom is null || cf > coverageFrom))
                    coverageFrom = cf;

                all.AddRange(p.Events);

                if (p.NextCursorTsMs is null) break;
                beforeTs = p.NextCursorTsMs;
                beforeId = p.NextCursorId;

                // A type that keeps paging to the budget means the walk is a prefix, not the whole answer.
                if (page == MaxPagesPerType - 1) truncated = true;
            }
        }

        return (all, coverageFrom, truncated, anyReadable);
    }

    /// <summary>
    /// How far back the ENGINE's journal reaches on this page. Every type read here is an
    /// <c>instance_*</c> event, which only kgsm emits, so its journal is the only one whose retention
    /// bounds the answer. Falls back to the page's collapsed floor when the reader named no producers
    /// (a single-journal reader), which is then the engine's by construction.
    /// </summary>
    private static DateTimeOffset? CoverageOf(EventHistoryPage page)
    {
        if (page.Journals is not { Count: > 0 } journals) return page.CoverageFrom;

        foreach (JournalCoverage j in journals)
            if (string.Equals(j.Producer, JournalProducer.Kgsm, StringComparison.Ordinal))
                return j.CoverageFrom;

        return page.CoverageFrom;
    }

    /// <summary>
    /// No journal, or none readable: every figure null and the flag set. A host with no engine history
    /// has not measured 100% uptime — it has measured nothing, and the report says so.
    /// </summary>
    private static AvailabilityReport Degraded(
        string windowLabel, DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string> ids)
    {
        List<ServerAvailability> rows = [.. ids
            .OrderBy(static i => i, StringComparer.Ordinal)
            .Select(id => new ServerAvailability(id, null, 0, 0, 0, null, Seeded: false))];
        return new AvailabilityReport(windowLabel, from, to, null, Truncated: false,
            EngineHistoryDegraded: true, rows, new FleetAvailability(null, 0, 0, 0, 0));
    }
}
