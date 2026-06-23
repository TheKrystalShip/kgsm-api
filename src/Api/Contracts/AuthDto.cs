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
public sealed record RefreshResponse(string Token, string Tier);

// GET /auth/session — the profile snapshot behind the bearer (captured at login), or 401.
public sealed record SessionResponse(SessionUser User, IReadOnlyList<string> Scopes);

public sealed record SessionUser(
    string Id,
    string Username,
    string Display,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AvatarUrl);
