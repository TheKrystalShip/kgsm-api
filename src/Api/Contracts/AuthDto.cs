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
// M4·c Increment 7 (Group E #12) — AccessTokenExpiresAt/RefreshExpiresAt ride along at mint time so
// the SPA learns expiry without decoding the JWT itself. Both are WhenWritingNull-omitted (like the
// existing Tier/Token/Refresh/UserId fields), so the "denied" branch — which passes null for both —
// stays byte-for-byte wire-identical to before this increment (additive-only, invariant #4).
public sealed record CallbackResult(
    string Verdict,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Token,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Refresh,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? AccessTokenExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RefreshExpiresAt);

// POST /auth/session/refresh — a freshly minted access token (no Discord round-trip).
// `tier` rides along (from the refresh token's own claims, not a re-check) so a client
// that rotates a session WITHOUT a /me round-trip — e.g. a returning visitor whose
// in-memory tier is gone after a browser close — still knows its role for UI gating.
// POST /auth/session/refresh — rotate the access token (+ rotate the refresh token — M4·c rotation).
    // The `token` field is the new ACCESS bearer (15min); `refresh` is the new refresh token (rotated,
    // 30d sliding window). The SPA MUST adopt BOTH on every refresh call — the previous refresh token
    // is dead (its jti no longer matches the session row's stored CurrentJti → reuse detection → 401
    // on the next /refresh). `tier` rides along so a client without a /me round-trip knows its role.
    // `expiresAt` (M4·c Increment 7, Group E #12) is the new ACCESS token's expiry — always present
    // (non-nullable) on a successful refresh, so the SPA can schedule its own proactive re-refresh
    // instead of waiting for a 401. Tail-additive; every existing field/consumer is unaffected.
    public sealed record RefreshResponse(string Token, string Refresh, string Tier, DateTimeOffset ExpiresAt);

/// <summary>
/// What a member hands back when it takes up a session the cluster's auth anchor minted.
/// </summary>
/// <remarks>
/// One token, and deliberately no refresh token. The bearer is this member's own and works here
/// alone; the session's absolute cap stays with the anchor, which holds the only refresh token that
/// exists for it. A client keeps presenting the anchor's refresh token — to the anchor when it can
/// reach it, and to this member when it cannot.
/// </remarks>
/// <param name="Token">A bearer minted by this member, audienced to this member.</param>
/// <param name="Tier">What the caller may do, resolved from this member's own replica.</param>
/// <param name="ExpiresAt">When this bearer dies. Never later than the anchor's cap on the session.</param>
public sealed record ClusterExchangeResponse(string Token, string Tier, DateTimeOffset ExpiresAt);

// GET /auth/session — the profile snapshot behind the bearer (captured at login), or 401.
public sealed record SessionResponse(SessionUser User, IReadOnlyList<string> Scopes);

public sealed record SessionUser(
    string Id,
    string Username,
    string Display,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AvatarUrl);

// ── The session-revocation surface (authority: Services/Auth/CLAUDE.md) ────────────────────────

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

// ── Cluster SSO vouch (PLAN-peers.md) — a peer node presents a valid cluster service token and
// asserts an already-authenticated user's identity; this node mints its OWN native session for that
// user, no Discord round-trip. ──────────────────────────────────────────────────────────────────

// POST /auth/cluster-session — the vouch request body. `tier` is a single resolved tier string
// (viewer/operator/admin), NOT a roles array — the vouching peer already resolved it via its own
// guild-role lookup; an unparseable/unknown/empty value floors to viewer (KgsmTiers.Parse's safe
// default) rather than denying or escalating an ambiguous assertion.
public sealed record ClusterSessionRequest(string DiscordId, string Username, string DisplayName, string Tier);

// The minted session — the same access/refresh/sid/expiry shape the OAuth callback hands the SPA,
// just reached via the vouch path instead of a Discord round-trip. `ExpiresAt` is the ACCESS token's
// expiry (mirrors CallbackResult.AccessTokenExpiresAt).
public sealed record ClusterSessionResult(string AccessToken, string RefreshToken, string Sid, DateTimeOffset ExpiresAt);

// POST /auth/cluster-session/request — the user-authed vouch INITIATOR body (PLAN-peers.md §7, gap
// G2): the browser calls this on the node it's already logged into, naming only the target node.
// The caller's identity/tier ride the caller's own validated session claims on THIS node, never this
// body — a request body cannot assert a different identity, foreclosing privilege laundering.
public sealed record ClusterVouchRequest(string NodeId);
