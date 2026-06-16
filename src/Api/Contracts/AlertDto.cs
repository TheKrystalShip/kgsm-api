namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One alert — the live wire representation of a problem <em>condition</em> (architecture.html §3·c),
/// NOT a task. The server raises it while the condition is true and resolves it when the condition
/// clears (self-heal or operator); the client never writes one (no complete/dismiss/PATCH). Emitted as
/// a page element by <c>GET /alerts</c> and pushed on the <c>alerts</c> WS topic as <c>alert.raise</c>
/// (full record, upserted by <see cref="Id"/>). The crash source (M6·a) is the only producer wired
/// today — every field below is honestly sourced from the watchdog's supervision state, never invented.
/// </summary>
/// <param name="Id">Stable, condition-derived id (<c>crash:&lt;serverId&gt;</c>) — a re-fire upserts the
/// SAME record (escalation re-pushes it), never a fresh per-raise id.</param>
/// <param name="Severity">Display weight — <see cref="AlertSeverity"/> (<c>danger|warn|info</c>).</param>
/// <param name="Source">Who raised it — <see cref="AlertSource"/>. Only <c>watchdog</c> is sourced at M6·a.</param>
/// <param name="Title">Human one-line headline.</param>
/// <param name="Detail">Human detail (the watchdog's transition reason / give-up summary).</param>
/// <param name="ServerId">The affected server, or <see langword="null"/> for a panel-wide condition.</param>
/// <param name="HostId">This host (every alert carries a hostId so the SPA filters, never joins — §4·d).</param>
/// <param name="Anchor">A best-effort deep-link hint to where the operator would act, or null.</param>
/// <param name="Status">Lifecycle — <see cref="AlertStatus"/> (<c>firing|resolved</c>).</param>
/// <param name="RaisedAt">When the condition started firing (ISO-8601 UTC <c>Z</c>).</param>
/// <param name="Escalated"><see langword="true"/> ⇒ auto-recovery exhausted its retries (the supervisor
/// gave up); the alert stays loud and never auto-resolves.</param>
/// <param name="Attempts">Self-heal attempts so far (the watchdog's restart streak — drives escalation).</param>
/// <param name="ResolvedAt">When the condition cleared (resolved records only).</param>
/// <param name="Resolution">Provenance of the clear (resolved records only).</param>
public sealed record Alert(
    string Id,
    string Severity,
    string Source,
    string Title,
    string Detail,
    string? ServerId,
    string? HostId,
    AlertAnchor? Anchor,
    string Status,
    DateTimeOffset RaisedAt,
    bool Escalated,
    int Attempts,
    DateTimeOffset? ResolvedAt = null,
    AlertResolution? Resolution = null);

/// <summary>A deep-link hint to where the operator would act on a condition (architecture.html §3·c
/// <c>anchor</c>). Advisory — the client routes from <see cref="AlertResolution"/>/<c>serverId</c> when
/// it doesn't recognize a surface. For a crash this points at the server.</summary>
public sealed record AlertAnchor(string Surface, string? HostId, string? Tab = null, string? Ref = null);

/// <summary>
/// Provenance of a resolution (architecture.html §3·c <c>resolution</c>) — carried by a resolved record
/// and the <c>alert.resolve</c> WS message. <see cref="By"/> is <em>always</em> <c>system</c> (the server
/// observed the clear; the client never resolves). <see cref="ActionId"/> is the one-way bridge to the
/// audit action that fixed it (<see cref="AuditRecord.Id"/>) — the alert↔audit link — or null when no
/// single action cleared it (never fabricated). At M6·a it is set only for an OPERATOR/api start|restart
/// recovery; an autonomous watchdog auto-restart emits no audited action, so an auto-heal links to null.
/// </summary>
public sealed record AlertResolution(string By, string Source, string Reason, string? ActionId);

/// <summary>The <c>alert.resolve</c> WS payload (architecture.html §3·c): <c>{ id, resolution }</c>. The
/// client stamps <c>resolvedAt</c> and moves the record it already holds to the rear-view; the
/// authoritative <c>resolvedAt</c> is on the REST resolved record. Shares the <see cref="Alert.Id"/>
/// coalesce key with <c>alert.raise</c>, so a resolve supersedes a still-queued raise (the
/// ServerPatch/ServerRemoved precedent).</summary>
public sealed record AlertResolved(string Id, AlertResolution Resolution);

/// <summary>The <c>alert.retract</c> WS payload (architecture.html §3·c): <c>{ id }</c> — the thing was
/// never an actionable condition (or its subject is simply gone, e.g. the instance was uninstalled). No
/// rear-view, no resolution. The client drops it by id.</summary>
public sealed record AlertRetracted(string Id);

/// <summary>The <c>GET /alerts</c> response envelope — <c>{ data }</c>, consistent with the other list
/// surfaces. The alert feed trends toward empty ("all clear") and is small, so it is unpaginated (no
/// cursor); the durable, growing record lives in <c>/audit</c>.</summary>
public sealed record AlertPage(IReadOnlyList<Alert> Data);

/// <summary>Alert lifecycle status (architecture.html §3·c <c>status</c>).</summary>
public static class AlertStatus
{
    public const string Firing = "firing";
    public const string Resolved = "resolved";
}

/// <summary>Display weight for an alert (architecture.html §3·c <c>severity</c>) — a strict subset of
/// <see cref="AuditSeverity"/> (no <c>success</c>: a firing condition is never a success).</summary>
public static class AlertSeverity
{
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Danger = "danger";
}

/// <summary>Who raised an alert (architecture.html §3·c <c>source</c>). Only <see cref="Watchdog"/> is
/// sourced at M6·a (the crash condition); the others are reserved for later increments (host-monitor /
/// metrics thresholds, the assistant) and are NOT emitted until their producer lands — never fabricated.</summary>
public static class AlertSource
{
    public const string Watchdog = "watchdog";
    public const string HostMonitor = "host-monitor";
    public const string Metrics = "metrics";
    public const string Assistant = "assistant";
}

/// <summary><see cref="AlertResolution.By"/> is always this — the server observed the clear, never the
/// client (architecture.html §3·c: "always system").</summary>
public static class AlertResolvedBy
{
    public const string System = "system";
}

/// <summary>The <see cref="AlertAnchor.Surface"/> values the API emits. A crash anchors to the server it
/// affects; the value is a hint the frontend may map or ignore (it always has <c>serverId</c> to route
/// from). The closed set grows with the surfaces alerts can point at.</summary>
public static class AlertSurface
{
    public const string Server = "server";
}
