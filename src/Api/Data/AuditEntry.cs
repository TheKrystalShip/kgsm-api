namespace TheKrystalShip.Api.Data;

/// <summary>
/// One immutable row of the append-only audit log (M5, architecture.html §3·d) — a past-tense
/// fact: a thing a human or the system <em>did</em>. The table is the durable archive the alerts
/// feed ages into; rows are never updated or deleted (a correction is a new row).
/// <para>
/// <b>Storage = the §3·d SQLite schema</b> (scalar columns for the scoped/filterable fields,
/// <see cref="Meta"/> as a JSON blob, the integer <see cref="RowId"/> doubling as the opaque
/// keyset cursor). It is created via <c>EnsureCreated</c>, NOT an EF migration — the project has
/// greenfield/dev authority and wipes the DB on a schema change rather than carrying migrations
/// (PLAN.md M5; the CLAUDE.md EnsureCreated-vs-migrations note).
/// </para>
/// <para>
/// <b>Honest divergence from §3·d (recorded in PLAN §6):</b> <see cref="Origin"/> is <em>nullable</em>
/// where the doc's DDL says <c>NOT NULL</c>. A bare-CLI kgsm action has no product surface, so the
/// engine emits <c>Origin = null</c>; we persist that null rather than fabricate a surface (the
/// never-fabricate invariant, the security/provenance analog of never-fabricate-a-status).
/// </para>
/// </summary>
public sealed class AuditEntry
{
    /// <summary>
    /// Monotonic integer key — an alias for SQLite's <c>rowid</c> (an <c>INTEGER PRIMARY KEY</c>),
    /// store-generated on insert. Doubles as the opaque keyset <c>cursor</c>: pages return rows with
    /// <c>RowId &lt; cursor</c> ordered descending. Never on the wire (the public id is <see cref="Id"/>).
    /// </summary>
    public long RowId { get; set; }

    /// <summary>The opaque, stable, public event id (<c>evt_…</c>). Unique.</summary>
    public string Id { get; set; } = "";

    /// <summary>When the action happened (UTC). Sourced from the kgsm event's <c>Timestamp</c>; for an
    /// API-internal action (auth) it is the moment the row was written. Stored ISO-8601 (SQLite TEXT).</summary>
    public DateTimeOffset Ts { get; set; }

    /// <summary>The surface that drove the action (<c>ui|assistant|discord|system|api</c>), or
    /// <see langword="null"/> when none was declared (e.g. a direct-CLI kgsm action — never fabricated).</summary>
    public string? Origin { get; set; }

    /// <summary>The identity's kind: <c>user|system|token</c>.</summary>
    public string ActorKind { get; set; } = "";
    /// <summary>The identity's display name (a Discord username, an OS user, or <c>system</c>).</summary>
    public string ActorName { get; set; } = "";
    /// <summary>The identity source: <c>discord|system|api</c> (nullable, per the §3·d schema).</summary>
    public string? ActorProvider { get; set; }

    /// <summary>The closed, dotted action vocabulary (<c>server.start</c>, <c>backup.create</c>, …).</summary>
    public string Action { get; set; } = "";
    /// <summary>Display weight: <c>info|success|warn|danger</c>.</summary>
    public string Severity { get; set; } = "";

    /// <summary>What the action acted on (<c>server</c>/<c>host</c>/…); the trio is nullable (a
    /// panel-wide action has no target).</summary>
    public string? TargetKind { get; set; }
    public string? TargetId { get; set; }
    public string? TargetName { get; set; }

    /// <summary>Denormalized scope key for the <c>?serverId=</c> filter; null if the action has no server.</summary>
    public string? ServerId { get; set; }
    /// <summary>Denormalized scope key for host filtering; this host's id for engine/auth actions.</summary>
    public string? HostId { get; set; }

    /// <summary>A human one-line summary (feed, Discord, a11y).</summary>
    public string Summary { get; set; } = "";

    /// <summary>Free-form, action-specific detail as a JSON object string (e.g. <c>{"oldVersion":…}</c>),
    /// or <see langword="null"/>. NB the §3·d example's <c>meta.jobId</c> is not populatable in the
    /// event-sourced model — no correlation id round-trips through the stateless engine.</summary>
    public string? Meta { get; set; }
}
