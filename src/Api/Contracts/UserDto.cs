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

// ── Connected accounts ────────────────────────────────────────────────────────
// The caller's own sign-in methods. An account is the primary object and a provider identity is one
// credential attached to it, so this surface is about what proves an account — never about what it
// may do, which is the admin surface above and comes from the account alone.

/// <summary>
/// <c>GET /auth/identities</c> — what can sign the caller in, and what more could.
/// </summary>
/// <param name="Reauth">
/// Whether this session has proved a credential recently enough to change what proves it. The panel
/// reads it to ask for a password before starting a link, rather than after.
/// </param>
public sealed record IdentitiesResponse(
    string UserId,
    string Username,
    bool HasPassword,
    IReadOnlyList<UserIdentityRecord> Identities,
    IReadOnlyList<LinkableProvider> Providers,
    ReauthState Reauth);

/// <summary>
/// <c>GET /auth/providers</c> — the providers this host can sign somebody in through, in the order a
/// login page should offer them.
/// </summary>
/// <remarks>
/// Read anonymously, by a browser deciding which buttons to draw. It carries names and nothing else:
/// what each one is called in a UI, and what its mark looks like, are the client's business, and a
/// host that shipped labels here would be telling four surfaces how to render.
/// </remarks>
public sealed record AuthProvidersResponse(IReadOnlyList<string> Providers);

/// <summary>A provider this host can attach, and whether it is attached already.</summary>
/// <param name="Configured">
/// Whether this host is wired to it at all. Unconfigured is not an error and not a failure to report
/// later — the panel simply does not offer it.
/// </param>
public sealed record LinkableProvider(string Provider, bool Configured, bool Linked);

/// <summary>
/// How long the caller may keep changing their sign-in methods, and whether they may right now.
/// </summary>
/// <param name="ExpiresAt">When the current proof lapses, or null when there is none.</param>
/// <param name="WindowMinutes">How long a proof lasts on this host, so the panel can say so.</param>
public sealed record ReauthState(bool Fresh, DateTimeOffset? ExpiresAt, int WindowMinutes);

/// <summary><c>POST /auth/reauth</c> — prove the caller's KGSM password again.</summary>
public sealed record ReauthRequest(string? Password);

/// <summary>The proof, and when it lapses.</summary>
public sealed record ReauthResult(DateTimeOffset ExpiresAt);

/// <summary>
/// <c>POST /auth/identities/discord/start</c> — where to send the browser to attach a Discord account.
/// </summary>
/// <remarks>
/// A URL rather than a redirect: the caller is an authenticated XHR carrying a bearer, and a bearer
/// does not survive a top-level navigation. The SPA follows this itself, and the ticket cookie set
/// alongside it is what the callback comes back with.
/// </remarks>
public sealed record LinkStartResponse(string Url);
