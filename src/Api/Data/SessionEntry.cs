namespace TheKrystalShip.Api.Data;

/// <summary>
/// One per-login row of the M4·c session registry — the operational state that backs session
/// revocation on top of the M4 stateless-JWT bearer. One row per (login × device); keyed by
/// <see cref="Id"/> = the JWT <c>sid</c> claim (<c>sid_&lt;guid&gt;</c>), stable across a session's
/// lifetime (carried by access + refresh; survives access rotation). The row's
/// <see cref="Revoked"/> flag + <see cref="Expires"/> hard cap are the authority a cached
/// per-request validator reads to decide "is this session still alive" — what the stateless
/// JWT alone cannot answer. Identity itself stays in the JWT; this row is operational, not a
/// user profile (the user-row half of the M4·a lock stays locked — see
/// <c>Services/Auth/CLAUDE.md</c> + <c>docs/session-management-plan.md</c>).
/// <para>
/// It joins <see cref="AuditEntry"/>/<see cref="HostSettingsEntity"/>/<see cref="IntegrationEntity"/>
/// as the API's own operational metadata (the domain itself is live-scraped, never stored).
/// </para>
/// <para>
/// ⚠ <b>EnsureCreated, NOT a migration</b> (project dev authority — see <see cref="AppDbContext"/>):
/// <c>EnsureCreated</c> includes this table automatically on a fresh DB (it is registered in
/// <see cref="AppDbContext.OnModelCreating"/>). Because <c>EnsureCreated</c> no-ops on an existing
/// DB, the already-deployed <c>/var/lib/kgsm-api/kgsm-api.db</c> is brought up to date with a
/// one-shot <c>sqlite3</c> table-creation command (not kept in the repo) — no C# migration code,
/// no wipe, audit rows untouched (D11). The code assumes the table exists after that one-shot.
/// </para>
/// <para>
/// <b>Timestamps are stored as UTC ticks</b> (INTEGER) via EF <c>ValueConverter</c>s, the same
/// posture as <see cref="AuditEntry.Ts"/>, <see cref="HostSettingsEntity.UpdatedAt"/> — SQLite has
/// no date type and EF cannot translate a <c>DateTimeOffset &gt;=</c> comparison stored as TEXT,
/// which the per-request validator's <see cref="Expires"/> &gt; now query needs. Round-trips to a
/// UTC <see cref="DateTimeOffset"/> on read.
/// </para>
/// </summary>
public sealed class SessionEntry
{
    /// <summary>
    /// The opaque session id (<c>sid_&lt;guid&gt;</c>) — the JWT <c>sid</c> claim value. Stable
    /// across a session's lifetime (carried by access + refresh tokens; survives access rotation
    /// within the 30-day absolute cap). Primary key.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The user this session belongs to (<c>discord:&lt;userId&gt;</c>, the JWT <c>sub</c> claim).
    /// One user has zero or more active sessions (one per device/browser).
    /// </summary>
    public string UserId { get; set; } = "";

    /// <summary>
    /// The host this session is scoped to (== the JWT <c>aud</c>, == <c>ApiOptions.HostId</c>).
    /// Sessions are per-host — matches the per-host topology (no cross-host session control).
    /// </summary>
    public string HostId { get; set; } = "";

    /// <summary>When the session was created (the login/OAuth-callback time). UTC.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>
    /// When the session was last observed alive — bumped on each successful access-token
    /// refresh (~15min/user). The "-last seen" column for the Active Sessions display. UTC.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>
    /// The session's absolute expiry — <c>Created + KGSM_API_SESSIONS_REFRESH_ABSOLUTE_DAYS</c>
    /// (default 30 days). <b>No sliding</b> on refresh (the 30-day cap stays absolute, per the
    /// original M4·a lock rationale). A refresh within this window re-mints access with the same
    /// <see cref="Id"/>; past it the session is dead regardless of <see cref="Revoked"/>. UTC.
    /// </summary>
    public DateTimeOffset Expires { get; set; }

    /// <summary>
    /// The raw <c>User-Agent</c> string captured at login (per D5 — raw UA, no IP, for a
    /// "Chrome • Windows 10"-style display on the Settings page). Nullable when the header was
    /// absent. ⚠ PII-ish (a UA can leak exact OS/version/build); only surfaced on the
    /// viewer-self/admin <c>GET /auth/sessions</c> display, never on a public surface.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Soft-delete flag. <see langword="false"/> for an active session; flipped to
    /// <see langword="true"/> by <c>POST /auth/logout</c>, self-revoke, or admin cross-user
    /// revoke. The per-request validator rejects a revoked session (within the 5s cache window);
    /// the access-token TTL (15min hard ceiling) bounds the disconnect. A revoked row is kept
    /// (kept-for-history posture) until the GC worker deletes it after <see cref="Expires"/>.
    /// </summary>
    public bool Revoked { get; set; }

    /// <summary>
    /// When the session was revoked (UTC). <see langword="null"/> while <see cref="Revoked"/> is
    /// false. Stamped on the revoke write (idempotent — already-revoked is a no-op, the value
    /// stays the first-revoke timestamp).
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }
}