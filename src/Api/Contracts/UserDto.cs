using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

// The KGSM account surface. An account is the primary identity object on a host: it exists on its
// own, carries the tier, and an external provider is one credential attached to it rather than the
// source of it. camelCase like the rest of /api/v1 (§6).

/// <summary>
/// <c>POST /auth/login</c> — sign in with a KGSM password. The path that needs no identity provider
/// configured on this host at all.
/// </summary>
public sealed record LoginRequest(string? Username, string? Password);

/// <summary>
/// A minted local session. Mirrors the OAuth callback's successful shape so the SPA adopts a session
/// the same way whichever door it came through.
/// </summary>
/// <param name="Status">
/// <c>active</c> or <c>pending</c>. A pending account authenticates and holds <c>none</c> — which is
/// what lets the panel say "awaiting approval" rather than showing someone who just proved who they
/// are a bare denial.
/// </param>
public sealed record LoginResult(
    string Token,
    string Refresh,
    string Tier,
    string UserId,
    string Status,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset RefreshExpiresAt);

/// <summary>An account, as an admin sees it. Never carries a secret in any form.</summary>
/// <param name="Id">The opaque <c>usr_…</c> id, stable across a rename.</param>
/// <param name="TierSource">
/// <c>granted</c> (an admin chose it) or <c>derived</c> (seeded from a mapping). The compensating
/// control for the store being sole authority: it is what an access review reads to tell a
/// deliberate grant from one nobody has looked at since it was seeded.
/// </param>
/// <param name="HasPassword">Whether a password can sign this account in at all.</param>
/// <param name="Identities">Linked external identities, as <c>provider:subject</c> handles.</param>
public sealed record UserRecord(
    string Id,
    string Username,
    string DisplayName,
    string Tier,
    string TierSource,
    string Status,
    bool HasPassword,
    IReadOnlyList<UserIdentityRecord> Identities,
    DateTimeOffset Created,
    DateTimeOffset Updated);

/// <summary>One external identity attached to an account.</summary>
public sealed record UserIdentityRecord(
    string Id,
    string Provider,
    string Handle,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label,
    DateTimeOffset Created,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastUsed);

/// <summary>Every account on this host, oldest first. No paging — a host has tens, not thousands.</summary>
public sealed record UsersPage(IReadOnlyList<UserRecord> Data);

/// <summary>
/// <c>POST /auth/users</c> — create an account.
/// </summary>
/// <param name="Password">
/// Optional. Omitting it creates an account that no password can sign in to yet, which is the shape
/// an invite or a link-only account starts as.
/// </param>
/// <param name="Status">
/// Optional, <c>active</c> or <c>pending</c>; defaults to <c>active</c>. An account an admin created
/// deliberately does not need approving by the same admin.
/// </param>
public sealed record CreateUserRequest(
    string? Username,
    string? DisplayName,
    string? Tier,
    string? Password,
    string? Status);

/// <summary>
/// <c>PATCH /auth/users/{id}</c> — change what an account is or may do. Every field is optional; an
/// absent one is left alone, which is what makes this safe to call from a form that edits one thing.
/// </summary>
public sealed record UpdateUserRequest(
    string? Username,
    string? DisplayName,
    string? Tier,
    string? Status);

/// <summary><c>POST /auth/users/{id}/password</c> — an admin sets someone's password.</summary>
public sealed record SetPasswordRequest(string? Password);

/// <summary>
/// <c>POST /auth/password</c> — the caller changes their own password, proving the current one.
/// </summary>
/// <remarks>
/// The current password is required even though the caller already holds a valid session: a session
/// can be a borrowed laptop, and letting one change the password that would take it back is how a
/// temporary compromise becomes a permanent one.
/// </remarks>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
