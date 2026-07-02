namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One aggregated host-log line (architecture.html §3, <c>GET /hosts/{id}/logs</c>) — a single entry
/// read from the host's <b>systemd journal</b> and tagged with the leaf it came from. The whole feature
/// is host-OS introspection (journald is the system's own merged log bus), NOT engine domain data, so it
/// is sourced by the api directly — never through kgsm-lib (the C#↔engine chokepoint is for kgsm/watchdog
/// domain reads, not the host journal). Logs are NEVER fabricated: a line is exactly what journald stored.
/// </summary>
/// <param name="Id">The opaque journald <c>__CURSOR</c> of this entry — also the keyset pagination cursor
/// (pass the page's <see cref="LogPage.NextCursor"/> back as <c>?cursor=</c> to walk older).</param>
/// <param name="At">When the line was logged (from <c>__REALTIME_TIMESTAMP</c>; ISO-8601 UTC <c>Z</c>).</param>
/// <param name="Source">The friendly leaf id the line came from (<c>watchdog|monitor|assistant|firewall|
/// api|bot</c>) — mapped from the journal's <c>_SYSTEMD_UNIT</c> via the configured source map.</param>
/// <param name="Level">Display weight mapped from the syslog <c>PRIORITY</c> (<see cref="LogLineLevel"/>):
/// <c>error|warn|info|debug</c>.</param>
/// <param name="Text">The rendered log message (<c>MESSAGE</c>).</param>
public sealed record LogLine(
    string Id,
    DateTimeOffset At,
    string Source,
    string Level,
    string Text);

/// <summary>
/// A keyset page of host-log lines (architecture.html §6 cursor pagination): <c>{ data, nextCursor }</c>,
/// newest first. <see cref="NextCursor"/> is the journald cursor of the last (oldest) line returned — pass
/// it back as <c>?cursor=</c> for the next (older) page — or <see langword="null"/> when no older lines
/// remain (the page came back short).
/// </summary>
public sealed record LogPage(IReadOnlyList<LogLine> Data, string? NextCursor);

/// <summary>
/// One configured host-log source (architecture.html §3) — the identity the frontend's source dropdown
/// renders. Derived from the canonical <see cref="Services.Leaves.LeafCatalog"/> via
/// <see cref="ApiOptions.LogSources"/>, so the set is stable and matches the Services board. The frontend
/// uses this to populate the dropdown regardless of whether a source has recent journal entries (quiet
/// services remain selectable).
/// </summary>
/// <param name="Id">The source id (<c>watchdog</c>, <c>monitor</c>, etc.) — matches the <c>source</c>
/// field on <see cref="LogLine"/>.</param>
/// <param name="Label">Human-friendly display name for the dropdown.</param>
/// <param name="Unit">The systemd unit whose journal carries this source's lines.</param>
public sealed record LogSourceInfo(string Id, string Label, string Unit);

/// <summary>Display weight for a log line, mapped from the syslog priority (matches the frontend
/// <c>LogConsole</c> level vocabulary). Never fabricated — an entry with no priority is <see cref="Info"/>.</summary>
public static class LogLineLevel
{
    public const string Error = "error";   // syslog 0..3 (emerg..err)
    public const string Warn = "warn";     // syslog 4 (warning)
    public const string Info = "info";     // syslog 5..6 (notice, info)
    public const string Debug = "debug";   // syslog 7 (debug)
}
