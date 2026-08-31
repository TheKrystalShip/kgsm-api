namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The scrollback shape: <c>{ "lines": [...], "start": N, "end": N, "hasEarlier": bool }</c>. The
/// offsets are the byte range of the run's log these lines came from; <c>start</c> is the cursor a
/// caller passes back as <c>?before=</c> to read what precedes them, and <c>hasEarlier</c> says
/// whether there is anything there — a watchdog too old to report the range answers 0/0/false, so a
/// caller offers no way back rather than one that would re-serve the same lines.
/// </summary>
public sealed record ConsoleScrollback(IReadOnlyList<string> Lines, long Start, long End, bool HasEarlier)
{
    /// <summary>Nothing to show — a run with no output, or a supervisor that could not be asked.</summary>
    public static ConsoleScrollback Empty { get; } = new([], 0, 0, false);
}
