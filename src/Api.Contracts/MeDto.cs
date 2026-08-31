namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// <c>GET /me</c> — the caller's own identity (the login-time Discord snapshot) + the authorization
/// <c>tier</c> resolved on this host + the granted <c>scopes</c>. The SPA gates its controls on
/// <c>tier</c>; <c>user</c> reuses the §3·f <see cref="SessionUser"/> shape.
/// </summary>
/// <remarks>
/// <b>Read-only — an honest-vs-aspirational divergence (frozen, like M1·b / M8·a).</b> The
/// architecture surface table lists <c>/me</c> as GET+PATCH ("Profile: display name, handle, density").
/// The editable half is a different resource: a density, and anything else that must follow a person
/// across devices, is a preference, and preferences live at <c>/me/preferences</c> — keyed per device,
/// with an account-level sync switch, and opaque about what any of them mean. So <c>/me</c> surfaces
/// only what the bearer already carries. <c>display</c>/<c>username</c>
/// are the Discord snapshot, never a guessed label — and the snapshot is the login-time capture, not a
/// fresh live fetch (the §3·f no-Discord-token-retained divergence, shared with <c>/auth/session</c>).
/// <para/>
/// <b><c>recentLogins</c> (M4·c Increment 7, Group E #11) is <c>/me</c>'s FIRST DB read</b> — every
/// field above this one is pure-claims (projected off the bearer, no I/O). It stays honest without
/// widening the persistence surface: <c>auth.login</c> is already the single, direct-write audit action
/// for a login (no kgsm event backs it — <c>Services/Audit/CLAUDE.md</c>'s "no double-write" invariant
/// is untouched, this is a READ), there is no <c>lastLogin</c> column and no user row anywhere (the
/// user-row half of the M4 lock stays locked). Complements, and is intentionally NOT the same surface
/// as, <c>GET /auth/sessions</c> (Increment 6) — that reads the live session REGISTRY (can be revoked,
/// answers "what's active now"); this reads the audit LOG (append-only provenance, answers "what
/// happened recently", including sessions since revoked or expired).
/// </remarks>
/// <param name="Status">
/// The state of the KGSM account behind the caller: <c>active</c>, <c>pending</c>, or <c>unknown</c>
/// when this identity proves no account on this host.
/// </param>
/// <remarks>
/// <b><c>status</c> is why a <c>none</c> tier is not one fact.</b> Someone awaiting approval and
/// someone nothing here has ever heard of both hold <c>none</c>, and a panel owes them different
/// sentences — one is being told to wait, the other that this is not their host. Reported as
/// <c>unknown</c> rather than guessed at when the store cannot be read.
/// </remarks>
public sealed record MeResponse(SessionUser User, string Tier, IReadOnlyList<string> Scopes,
    IReadOnlyList<RecentLogin> RecentLogins, string Status);

/// <summary>
/// The mutable half of <see cref="MeResponse"/>, pushed on the <c>me</c> stream topic as
/// <c>me.patch</c>: what the caller's own account may do here and what state it is in.
/// </summary>
/// <remarks>
/// Both fields carry the same wire vocabulary <c>GET /me</c> answers in — <c>tier</c> is a
/// <c>KgsmTiers</c> string, <c>status</c> is a <c>UserStatuses</c> one or <c>unknown</c> — so a client
/// merges a frame straight over what it hydrated with no second mapping. The identity half is left
/// out because nothing on this path changes it: a tier is a grant on this host, and a display name is
/// the identity provider's login-time snapshot.
/// </remarks>
public sealed record MeStanding(string Tier, string Status);

/// <summary>One <c>auth.login</c> audit row, shaped for the <c>/me</c> recent-logins list. <c>Device</c>
/// is the login's <c>User-Agent</c> header (threaded into the audit row's <c>meta.userAgent</c> at login
/// — see <c>AuthController.RecordAuthAsync</c>), or <see langword="null"/> when the caller sent none
/// (honest-unknown, never a guessed label).</summary>
public sealed record RecentLogin(DateTimeOffset Ts, string? Device);
