namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// <c>GET /me</c> — the caller's own identity (the login-time Discord snapshot) + the authorization
/// <c>tier</c> resolved on this host + the granted <c>scopes</c>. The SPA gates its controls on
/// <c>tier</c>; <c>user</c> reuses the §3·f <see cref="SessionUser"/> shape.
/// </summary>
/// <remarks>
/// <b>Read-only — an honest-vs-aspirational divergence (frozen, like M1·b / M8·a).</b> The
/// architecture surface table lists <c>/me</c> as GET+PATCH ("Profile: display name, handle, density").
/// The editable half (a custom display name, the UI density preference) needs a per-panel preference
/// store keyed off identity — <b>deliberately not built</b> (architecture.html's statelessness note:
/// "anything that must follow a user across devices … deliberately not built"). So <c>/me</c> surfaces
/// only what the bearer already carries; PATCH + density wait for that store. <c>display</c>/<c>username</c>
/// are the Discord snapshot, never a guessed label — and the snapshot is the login-time capture, not a
/// fresh live fetch (the §3·f no-Discord-token-retained divergence, shared with <c>/auth/session</c>).
/// </remarks>
public sealed record MeResponse(SessionUser User, string Tier, IReadOnlyList<string> Scopes);
