namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// How much of the time each server was up <em>when something wanted it up</em>, over a window.
/// </summary>
/// <remarks>
/// <para><b>The denominator is intent, not wall clock.</b> A server an operator deliberately stopped is
/// not down — it is off. Counting its idle days as outage would score a fleet by how much of it happens
/// to be running, which is a different question and a useless one. So availability is measured only
/// across the spans in which the server was <em>meant</em> to be running, and a server that was meant to
/// run for none of the window has no availability figure at all (honest null, never 100%).</para>
/// <para><b>What the figures are read from.</b> The engine's event journal — the same source the audit
/// feed is merged from. Nothing is sampled or polled for this, so it costs the host nothing and it can
/// answer for any window the journal still covers. <see cref="CoverageFrom"/> is what the journal can
/// actually speak for: a window reaching earlier is answered from there, and saying so is what keeps a
/// partial history from reading as a complete one.</para>
/// </remarks>
/// <param name="Window">The requested window, echoed (e.g. <c>7d</c>).</param>
/// <param name="From">The start of the measured span — the window's start, or <see cref="CoverageFrom"/>
/// when the journal cannot reach that far back.</param>
/// <param name="To">The end of the measured span (now).</param>
/// <param name="CoverageFrom">The oldest moment the journal can answer for, or null when it holds no
/// events. A <see cref="From"/> later than the requested window's start is explained by this.</param>
/// <param name="Truncated">True when the journal scan stopped at its byte budget before covering the
/// window. The figures are then a valid answer over <see cref="From"/>..<see cref="To"/> and not over
/// the whole window.</param>
/// <param name="EngineHistoryDegraded">True when there is no readable journal at all — every figure is
/// null rather than zero, because nothing was measured.</param>
/// <param name="Servers">Per-server figures, ordered by id.</param>
/// <param name="Fleet">The rollup across every server that had intended-up time in the window.</param>
public sealed record AvailabilityReport(
    string Window,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset? CoverageFrom,
    bool Truncated,
    bool EngineHistoryDegraded,
    IReadOnlyList<ServerAvailability> Servers,
    FleetAvailability Fleet);

/// <summary>
/// One server's uptime record over the window.
/// </summary>
/// <param name="Id">The kgsm instance id.</param>
/// <param name="Availability">Fraction in [0,1] of intended-up time the server was actually up, or null
/// when nothing wanted it up during the window (no denominator — never a fabricated 1.0).</param>
/// <param name="IntendedSeconds">How long the server was meant to be running.</param>
/// <param name="DowntimeSeconds">How much of that it was not running. Counts every span a player could
/// not connect while the server was meant to be up — a crash and the recovery gap after it, and the
/// shutdown half of a deliberate restart. Never counts a deliberate stop, which lowers
/// <paramref name="IntendedSeconds"/> instead.</param>
/// <param name="Outages">How many distinct <em>unplanned</em> down spans occurred — crashes and
/// give-ups. A restart is downtime without being an incident, so it moves
/// <paramref name="DowntimeSeconds"/> and not this.</param>
/// <param name="LastOutage">When the most recent unplanned one began, or null when there were
/// none. A server with downtime and no outage spent it all in planned restarts.</param>
/// <param name="Seeded">True when the server's state at <see cref="AvailabilityReport.From"/> was
/// established from an event <em>before</em> the window. False means the window opens on an unknown
/// state and the server only starts accruing at its first in-window transition — which is why a short
/// window on a long-idle fleet reports small denominators rather than guessing.</param>
public sealed record ServerAvailability(
    string Id,
    double? Availability,
    long IntendedSeconds,
    long DowntimeSeconds,
    int Outages,
    DateTimeOffset? LastOutage,
    bool Seeded);

/// <summary>
/// The fleet rollup — time-weighted, not an average of percentages.
/// </summary>
/// <remarks>
/// Averaging per-server percentages would let a server that was meant to run for ten minutes weigh as
/// much as one that ran all week. Summing the seconds first is the only rollup that means anything
/// across servers with unequal intent.
/// </remarks>
/// <param name="Availability">Total intended-up minus total downtime, over total intended-up, or null
/// when no server had any intended-up time.</param>
/// <param name="IntendedSeconds">Summed across servers.</param>
/// <param name="DowntimeSeconds">Summed across servers.</param>
/// <param name="Outages">Summed across servers.</param>
/// <param name="ServersCounted">How many servers contributed a denominator. A server with none is
/// excluded here and still listed in <see cref="AvailabilityReport.Servers"/> with a null figure.</param>
public sealed record FleetAvailability(
    double? Availability,
    long IntendedSeconds,
    long DowntimeSeconds,
    int Outages,
    int ServersCounted);
