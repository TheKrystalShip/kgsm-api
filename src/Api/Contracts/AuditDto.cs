namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The identity an action ran as (architecture.html §3·d <c>actor</c>). <see cref="Kind"/> is
/// <c>user|system|token</c>; <see cref="Provider"/> the identity source (<c>discord|system|api</c>,
/// nullable). The pair with <see cref="AuditRecord.Origin"/> answers both <em>whose authority</em>
/// (this) and <em>through which surface</em> (origin) — never collapsed (the user's actor-vs-origin
/// requirement).
/// </summary>
public sealed record AuditActor(string Kind, string Name, string? Provider);

/// <summary>What an action acted on (architecture.html §3·d <c>target</c>). Null when the action is
/// panel-wide (no target).</summary>
public sealed record AuditTarget(string Kind, string Id, string? Name);

/// <summary>
/// One audit record — the wire shape of an append-only action fact (architecture.html §3·d). Emitted
/// by <c>GET /audit</c> (a page element) and pushed on the <c>audit</c> WS topic as <c>audit.append</c>.
/// </summary>
/// <param name="Id">Opaque, stable, public event id (<c>evt_…</c>).</param>
/// <param name="Ts">When it happened (ISO-8601 UTC <c>Z</c>).</param>
/// <param name="Origin">The driving surface (<c>ui|assistant|discord|system|api</c>) or
/// <see langword="null"/> — a §6 divergence from the doc's NOT-NULL <c>origin</c>: a direct-CLI engine
/// action has no surface, so null (never fabricated).</param>
/// <param name="Actor">Whose authority it carried.</param>
/// <param name="Action">The closed dotted vocabulary (<see cref="AuditAction"/>).</param>
/// <param name="Severity">Display weight (<see cref="AuditSeverity"/>).</param>
/// <param name="Target">What it acted on, or null.</param>
/// <param name="ServerId">Denormalized scope key for <c>?serverId=</c>; null if none.</param>
/// <param name="HostId">Denormalized scope key (this host) for host scoping.</param>
/// <param name="Summary">Human one-line.</param>
/// <param name="Meta">Free-form, action-specific detail (string-valued for now), or null.</param>
public sealed record AuditRecord(
    string Id,
    DateTimeOffset Ts,
    string? Origin,
    AuditActor Actor,
    string Action,
    string Severity,
    AuditTarget? Target,
    string? ServerId,
    string? HostId,
    string Summary,
    IReadOnlyDictionary<string, string>? Meta);

/// <summary>
/// A keyset page of audit records (architecture.html §6 cursor pagination): <c>{ data, nextCursor }</c>,
/// newest first. <see cref="NextCursor"/> is the <c>rowid</c> of the last (oldest) row returned — pass it
/// back as <c>?cursor=</c> for the next page — or <see langword="null"/> when there are no older rows.
/// </summary>
public sealed record AuditPage(IReadOnlyList<AuditRecord> Data, string? NextCursor);

/// <summary>
/// The closed, server-defined action vocabulary (architecture.html §3·d). Clients and the model can't
/// invent one; an unknown action is rejected at write time. The subset wired in M5 is what the engine
/// event stream + API-internal auth actions can honestly source today.
/// </summary>
public static class AuditAction
{
    // server.* — sourced from kgsm lifecycle events (the engine owns these; no API double-write).
    public const string ServerStart = "server.start";
    public const string ServerStop = "server.stop";
    public const string ServerRestart = "server.restart";
    public const string ServerUpdate = "server.update";
    public const string ServerInstall = "server.install";
    public const string ServerUninstall = "server.uninstall";
    // server.crash is in the closed vocab but has no source yet (no kgsm crash event; the watchdog
    // detects crashes — M6). Not emitted in M5.

    // backup.* — sourced from kgsm backup events.
    public const string BackupCreate = "backup.create";
    public const string BackupRestore = "backup.restore";

    // auth.* — API-internal (no kgsm event → written directly, no double-write risk).
    public const string AuthLogin = "auth.login";
    public const string AuthLogout = "auth.logout";
}

/// <summary>Display weight for an audit record (architecture.html §3·d <c>severity</c>).</summary>
public static class AuditSeverity
{
    public const string Info = "info";
    public const string Success = "success";
    public const string Warn = "warn";
    public const string Danger = "danger";
}

/// <summary>Actor kinds (architecture.html §3·d <c>actor.kind</c>).</summary>
public static class ActorKind
{
    public const string User = "user";
    public const string System = "system";
    public const string Token = "token";
}

/// <summary>Identity providers (architecture.html §3·d <c>actor.provider</c>).</summary>
public static class ActorProvider
{
    public const string Discord = "discord";
    public const string System = "system";
    public const string Api = "api";
}

/// <summary>Target kinds (architecture.html §3·d <c>target.kind</c>).</summary>
public static class AuditTargetKind
{
    public const string Server = "server";
    public const string Host = "host";
}

/// <summary>
/// The closed origin set (architecture.html §3·d). <see cref="System"/> is reserved for the
/// engine/watchdog path (stamped at the kgsm level via <c>KGSM_EVENT_ORIGIN</c>); the API never
/// emits it. <see cref="IsCallerDeclarable"/> is the subset a request may declare on the command path.
/// </summary>
public static class AuditOrigin
{
    public const string Ui = "ui";
    public const string Assistant = "assistant";
    public const string Discord = "discord";
    public const string System = "system";
    public const string Api = "api";

    /// <summary>True if <paramref name="origin"/> is one of the closed set (used to normalize an event's
    /// origin; an unrecognized value is treated as null — never fabricated).</summary>
    public static bool IsKnown(string? origin) =>
        origin is Ui or Assistant or Discord or System or Api;

    /// <summary>True if a client may declare <paramref name="origin"/> on the command path —
    /// everything except <see cref="System"/> (reserved for autonomous engine actions).</summary>
    public static bool IsCallerDeclarable(string origin) =>
        origin is Ui or Assistant or Discord or Api;
}
