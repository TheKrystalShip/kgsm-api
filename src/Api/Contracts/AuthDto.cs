using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The auth wire contracts (architecture.html §3·f, Model A). camelCase like the rest of the
/// surface (§6) — the §3·f examples' <c>user_id</c> snake_case is illustrative; we keep one casing.
/// </summary>
/// <remarks>
/// The bearer mechanism is stateless JWT (the M4 decision): no session table, no user row. The
/// callback returns the access token (+ a refresh token when issued); the client stores them in
/// <c>sessionStorage</c> (the frontend's half) and rotates via <c>/auth/session/refresh</c>.
/// </remarks>
// GET /auth/discord/callback — bootstrap a host session. verdict "ok" (200) | "denied" (403).
// userId is the prefixed handle (discord:{id}) — the only identity handle the panel references.
public sealed record CallbackResult(
    string Verdict,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Token,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Refresh,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserId);

// POST /auth/session/refresh — a freshly minted access token (no Discord round-trip).
// `tier` rides along (from the refresh token's own claims, not a re-check) so a client
// that rotates a session WITHOUT a /me round-trip — e.g. a returning visitor whose
// in-memory tier is gone after a browser close — still knows its role for UI gating.
// POST /auth/session/refresh — rotate the access token (+ rotate the refresh token — M4·c rotation).
    // The `token` field is the new ACCESS bearer (15min); `refresh` is the new refresh token (rotated,
    // 30d sliding window). The SPA MUST adopt BOTH on every refresh call — the previous refresh token
    // is dead (its jti no longer matches the session row's stored CurrentJti → reuse detection → 401
    // on the next /refresh). `tier` rides along so a client without a /me round-trip knows its role.
    public sealed record RefreshResponse(string Token, string Refresh, string Tier);

// GET /auth/session — the profile snapshot behind the bearer (captured at login), or 401.
public sealed record SessionResponse(SessionUser User, IReadOnlyList<string> Scopes);

public sealed record SessionUser(
    string Id,
    string Username,
    string Display,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AvatarUrl);

// ── M4·c Increment 6 — the revocation surface (docs/session-management-plan.md §5) ──────────────

// GET /auth/sessions — the caller's own active-session set (or, for an admin passing ?userId=,
// another user's). `data` mirrors the SessionEntry registry row-for-row (D5 fields), never the
// audit placebo. `current` is true on exactly the row whose sid matches the calling bearer's own
// sid (so the SPA can label "this device").
public sealed record SessionsPage(IReadOnlyList<SessionRecord> Data);

public sealed record SessionRecord(
    string Sid,
    string UserId,
    DateTimeOffset Created,
    DateTimeOffset LastSeen,
    DateTimeOffset Expires,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserAgent,
    bool Current);

// POST /auth/session/revoke — the viewer self-revoke body. Exactly one of `sid`/`all` is meaningful:
// `all:true` revokes every session the caller owns (incl. the calling one — "log out everywhere");
// `sid` revokes that one session, but ONLY if it belongs to the caller (else 404 — a viewer must not
// be able to revoke another user's session by guessing/leaking a sid; that power is admin-only, see
// POST /auth/sessions/{sid}/revoke). Neither set ⇒ revoke the calling session (logout-equivalent).
public sealed record RevokeRequest(string? Sid, bool? All);
