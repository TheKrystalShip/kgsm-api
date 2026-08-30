using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// KGSM accounts — the host's own identity store.
/// </summary>
/// <remarks>
/// <para>
/// An account exists on its own and carries the tier; an external identity is a credential attached
/// to one, never the source of one. So this surface is where authority on this host is decided, and
/// every write it takes is a privilege event with an audit row behind it.
/// </para>
/// <para>
/// <b>Admin-gated throughout, with two deliberate exceptions:</b> a caller reading or changing their
/// own account. Everything else — creating, approving, disabling, retiering, deleting — is admin,
/// because each of them changes what somebody else may do.
/// </para>
/// <para>
/// A store that could not be opened answers <c>503</c> here rather than taking the panel down; see
/// <see cref="UserDirectory"/> for why that is the right shape.
/// </para>
/// </remarks>
[ApiController]
public sealed class UsersController(
    UserDirectory users,
    StreamHub stream,
    ApiJournal journal) : ControllerBase
{
    /// <summary><c>GET /auth/users</c> — every account on this host, oldest first (admin).</summary>
    [Authorize(Policy = AuthPolicy.Admin)]
    [HttpGet("/auth/users")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        IReadOnlyList<KgsmUser> all = await users.Store.ListAsync(ct);

        List<UserRecord> records = [];
        foreach (KgsmUser user in all)
            records.Add(await ToRecordAsync(user, ct));

        return Ok(new UsersPage(records));
    }

    /// <summary><c>GET /auth/users/{userId}</c> — one account (admin).</summary>
    [Authorize(Policy = AuthPolicy.Admin)]
    [HttpGet("/auth/users/{userId}")]
    public async Task<IActionResult> Get(string userId, CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        KgsmUser? user = await users.Store.FindByIdAsync(userId, ct);
        return user is null ? NotFoundUser(userId) : Ok(await ToRecordAsync(user, ct));
    }

    /// <summary>
    /// <c>POST /auth/users</c> — create an account (admin).
    /// <list type="bullet">
    /// <item><c>201</c> <see cref="UserRecord"/>.</item>
    /// <item><c>400</c> <c>bad_request</c> — an unusable username, an unknown tier, or an unknown status.</item>
    /// <item><c>409</c> <c>username_taken</c>.</item>
    /// </list>
    /// </summary>
    [Authorize(Policy = AuthPolicy.Admin)]
    [AnchorHoldsAuth]
    [HttpPost("/auth/users")]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest? body, CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        if (body is null || !Usernames.IsValid(body.Username))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                $"username must be {Usernames.MinLength}–{Usernames.MaxLength} characters of letters, " +
                "digits, '.', '_' or '-', beginning with a letter or a digit.");

        if (ParseTier(body.Tier) is not { } tier)
            return BadTier(body.Tier);

        // Only the two states an admin can mean. Creating an account already disabled is a shape with
        // no use — an admin who wants that creates it and disables it, and the audit trail then says
        // both things happened.
        UserStatus status = UserStatuses.Parse(body.Status ?? UserStatuses.Active);
        if (status == UserStatus.Disabled)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                $"status must be '{UserStatuses.Active}' or '{UserStatuses.Pending}'.");

        // A password is optional here — an account can be created for somebody who will only ever
        // arrive through a provider. One that IS set answers to the same floor as every other, or
        // the door with the least scrutiny is the one that admits the weakest password on the host.
        if (!string.IsNullOrEmpty(body.Password) && PasswordProblem(body.Password) is { } weak)
            return weak;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        KgsmUser user = new(
            UserIds.NewUserId(),
            body.Username!.Trim(),
            string.IsNullOrWhiteSpace(body.DisplayName) ? body.Username.Trim() : body.DisplayName.Trim(),
            tier,
            // An admin picking a tier in the panel IS the deliberate grant this records.
            TierSource.Granted,
            status,
            now,
            now);

        try
        {
            await users.Store.CreateAsync(user, ct);
        }
        catch (DuplicateUsernameException)
        {
            return Error(StatusCodes.Status409Conflict, "username_taken",
                $"'{body.Username}' is already taken on this host.");
        }

        if (!string.IsNullOrEmpty(body.Password))
            await users.SignIn.SetPasswordAsync(user.UserId, body.Password, now, ct);

        await RecordAsync(ApiJournal.UserProvisionedEvent, user,
            toTier: KgsmTiers.ToWire(tier), toStatus: UserStatuses.ToWire(status), ct: ct);

        return StatusCode(StatusCodes.Status201Created, await ToRecordAsync(user, ct));
    }

    /// <summary>
    /// <c>PATCH /auth/users/{userId}</c> — change an account (admin). Absent fields are left alone.
    /// </summary>
    /// <remarks>
    /// A tier change and a status change each get their own audit row rather than one "updated" row,
    /// because they answer different questions and an access review reads for exactly one of them.
    /// </remarks>
    [Authorize(Policy = AuthPolicy.Admin)]
    [AnchorHoldsAuth]
    [HttpPatch("/auth/users/{userId}")]
    public async Task<IActionResult> Update(string userId, [FromBody] UpdateUserRequest? body, CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        if (body is null)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "a body is required");

        KgsmUser? existing = await users.Store.FindByIdAsync(userId, ct);
        if (existing is null)
            return NotFoundUser(userId);

        if (body.Username is not null && !Usernames.IsValid(body.Username))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "that username is not usable.");

        KgsmTier tier = existing.Tier;
        if (body.Tier is not null)
        {
            if (ParseTier(body.Tier) is not { } parsed)
                return BadTier(body.Tier);
            tier = parsed;
        }

        UserStatus status = existing.Status;
        if (body.Status is not null)
        {
            status = UserStatuses.Parse(body.Status);
            // Parse is fail-closed, so an unrecognised value would silently disable somebody. Refuse
            // instead: a typo in a status field must not be a way to lock an admin out.
            if (UserStatuses.ToWire(status) != body.Status.Trim().ToLowerInvariant())
                return Error(StatusCodes.Status400BadRequest, "bad_request",
                    $"status must be one of '{UserStatuses.Active}', '{UserStatuses.Pending}', '{UserStatuses.Disabled}'.");
        }

        // Guard the one change nobody can undo: an admin removing the last way to administer this
        // host. Both a demotion and a disable get there, so both are checked.
        if (existing.Status == UserStatus.Active && existing.Tier == KgsmTier.Admin
            && (tier != KgsmTier.Admin || status != UserStatus.Active)
            && await CountsActiveAdminsAsync(ct) <= 1)
        {
            return Error(StatusCodes.Status409Conflict, "last_admin",
                "That is the only active admin account on this host. Give someone else the admin tier first.");
        }

        KgsmUser updated = existing with
        {
            Username = body.Username?.Trim() ?? existing.Username,
            DisplayName = string.IsNullOrWhiteSpace(body.DisplayName)
                ? existing.DisplayName
                : body.DisplayName.Trim(),
            Tier = tier,
            // Any tier an admin sets here is a deliberate grant, which is exactly what distinguishes
            // it from one seeded from a mapping and never looked at since.
            TierSource = tier == existing.Tier ? existing.TierSource : TierSource.Granted,
            Status = status,
            Updated = DateTimeOffset.UtcNow,
        };

        try
        {
            if (!await users.Store.UpdateAsync(updated, ct))
                return NotFoundUser(userId);
        }
        catch (DuplicateUsernameException)
        {
            return Error(StatusCodes.Status409Conflict, "username_taken",
                $"'{body.Username}' is already taken on this host.");
        }

        // Authority is resolved per request from a short-lived cache, so drop this account's entries
        // rather than making the admin who just made the change watch it not take effect. Every way
        // the account can be proved is dropped, because a session caches under the handle it signed in
        // with. Other surfaces on this host hold their own caches and pick it up within their own TTL.
        await users.ForgetAsync(userId, ct);

        // Every panel this person has open re-gates itself and is told, on the connection it already
        // holds. Without it a demotion is a surface that keeps rendering controls the next click
        // discovers are gone, and an approval is somebody staring at "awaiting approval" until they
        // think to reload.
        stream.AuthorityChanged(
            updated.UserId, updated.EffectiveTier, UserStatuses.ToWire(updated.Status));

        await RecordChangesAsync(existing, updated, ct);
        return Ok(await ToRecordAsync(updated, ct));
    }

    /// <summary>
    /// <c>DELETE /auth/users/{userId}</c> — erase an account and its credentials (admin).
    /// </summary>
    /// <remarks>
    /// Disabling is what an admin normally wants, and is what keeps the audit trail legible — a
    /// deleted account's past rows still name it, and nothing explains who that was. This exists for
    /// a row that should never have been created.
    /// </remarks>
    [Authorize(Policy = AuthPolicy.Admin)]
    [AnchorHoldsAuth]
    [HttpDelete("/auth/users/{userId}")]
    public async Task<IActionResult> Delete(string userId, CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        KgsmUser? existing = await users.Store.FindByIdAsync(userId, ct);
        if (existing is null)
            return NotFoundUser(userId);

        if (existing.Status == UserStatus.Active && existing.Tier == KgsmTier.Admin
            && await CountsActiveAdminsAsync(ct) <= 1)
        {
            return Error(StatusCodes.Status409Conflict, "last_admin",
                "That is the only active admin account on this host.");
        }

        // Before the row goes: the handles are read off its credentials, and they are about to be
        // deleted with it.
        await users.ForgetAsync(userId, ct);

        await users.Store.DeleteAsync(userId, ct);

        // The account is gone, which is what an identity that proves none reads as everywhere else on
        // this surface: no tier, and a status this host cannot name. Their open streams re-gate to
        // that and are told, rather than running on at a tier whose record no longer exists.
        stream.AuthorityChanged(userId, KgsmTier.None, AccountStanding.UnknownStatus);

        await RecordAsync(ApiJournal.UserDeletedEvent, existing,
            fromTier: KgsmTiers.ToWire(existing.Tier),
            fromStatus: UserStatuses.ToWire(existing.Status), ct: ct);

        return NoContent();
    }

    /// <summary>
    /// <c>POST /auth/users/{userId}/password</c> — an admin sets someone's password (admin).
    /// </summary>
    /// <remarks>
    /// The reset path, and deliberately the only one: there is no email anywhere in this ecosystem,
    /// so a forgotten password is a conversation with an admin rather than a mail round-trip.
    /// </remarks>
    [Authorize(Policy = AuthPolicy.Admin)]
    [AnchorHoldsAuth]
    [HttpPost("/auth/users/{userId}/password")]
    public async Task<IActionResult> SetPassword(
        string userId, [FromBody] SetPasswordRequest? body, CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        if (PasswordProblem(body?.Password) is { } problem)
            return problem;

        KgsmUser? existing = await users.Store.FindByIdAsync(userId, ct);
        if (existing is null)
            return NotFoundUser(userId);

        await users.SignIn.SetPasswordAsync(userId, body!.Password!, DateTimeOffset.UtcNow, ct);
        await RecordAsync(ApiJournal.UserPasswordChangedEvent, existing, byHolder: false, ct: ct);

        return NoContent();
    }

    /// <summary>
    /// <c>POST /auth/password</c> — the caller changes their own password, proving the current one.
    /// </summary>
    /// <remarks>
    /// Proving the current password matters even though the caller already holds a valid session: a
    /// session can be a borrowed laptop, and letting one change the password that would take the
    /// account back is how a temporary compromise becomes a permanent one.
    /// </remarks>
    [Authorize(Policy = AuthPolicy.Viewer)]
    [AnchorHoldsAuth]
    [HttpPost("/auth/password")]
    public async Task<IActionResult> ChangeOwnPassword(
        [FromBody] ChangePasswordRequest? body, CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        if (Caller() is not { } caller)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no valid session");

        if (caller.Provider != KgsmActorProvider.Local)
            return Error(StatusCodes.Status409Conflict, "not_a_local_session",
                "This session was established through an identity provider, which has no KGSM password " +
                "to change. Sign in with your KGSM password to change it.");

        if (PasswordProblem(body?.NewPassword) is { } problem)
            return problem;

        KgsmUser? user = await users.Store.FindByIdAsync(caller.Subject, ct);
        if (user is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no such account");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        LocalSignInResult check = await users.SignIn
            .SignInAsync(user.Username, body!.CurrentPassword, now, ct);

        if (check.Outcome != LocalSignInOutcome.Success)
            return Error(StatusCodes.Status403Forbidden, "invalid_credentials",
                "The current password is not correct.");

        await users.SignIn.SetPasswordAsync(user.UserId, body.NewPassword!, now, ct);
        await RecordAsync(ApiJournal.UserPasswordChangedEvent, user, byHolder: true, ct: ct);

        return NoContent();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The caller's identity off their own validated claims, never off a request body.</summary>
    private KgsmIdentity? Caller() =>
        User.Identity is ClaimsIdentity ci ? SessionClaims.ReadIdentity(ci) : null;

    /// <summary>The 503 to return when the store could not be opened, or null when it is fine.</summary>
    private ObjectResult? Unavailable() =>
        users.Available
            ? null
            : Error(StatusCodes.Status503ServiceUnavailable, "users_unavailable",
                users.UnavailableReason ?? "The KGSM account store is unavailable on this host.");

    /// <summary>
    /// The tier a request named, or null when it named none this build knows.
    /// </summary>
    /// <remarks>
    /// <see cref="KgsmTiers.Parse"/> alone is fail-closed and maps anything unrecognised to
    /// <see cref="KgsmTier.None"/> — right for reading a token, wrong here, where it would silently
    /// turn a misspelled "opreator" into an account that can do nothing. An admin gets told instead.
    /// </remarks>
    private static KgsmTier? ParseTier(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
            return KgsmTier.Viewer;

        KgsmTier parsed = KgsmTiers.Parse(wire);
        return parsed == KgsmTier.None && wire.Trim().ToLowerInvariant() != KgsmTiers.None
            ? null
            : parsed;
    }

    private ObjectResult BadTier(string? wire) =>
        Error(StatusCodes.Status400BadRequest, "bad_request",
            $"'{wire}' is not a tier. Use one of '{KgsmTiers.None}', '{KgsmTiers.Viewer}', " +
            $"'{KgsmTiers.Operator}', '{KgsmTiers.Admin}'.");

    private ObjectResult NotFoundUser(string userId) =>
        Error(StatusCodes.Status404NotFound, "not_found", $"no account '{userId}' on this host");

    /// <summary>
    /// Whether a password is one this host will store.
    /// </summary>
    /// <remarks>
    /// Length only, following NIST 800-63B: composition rules ("one capital, one symbol") push people
    /// toward predictable substitutions and buy less than the length they discourage.
    /// </remarks>
    /// <summary>
    /// The password floor, refused as the frozen envelope. The rule itself is
    /// <see cref="Passwords"/>, beside the store that holds the hash, so the three doors that set a
    /// password — registration, this admin reset, and a holder changing their own — cannot drift
    /// apart.
    /// </summary>
    private ObjectResult? PasswordProblem(string? password) =>
        Passwords.IsAcceptable(password)
            ? null
            : Error(StatusCodes.Status400BadRequest, "bad_request",
                $"a password must be at least {Passwords.MinLength} characters");

    /// <summary>How many active admins this host has — the count the last-admin guard reads.</summary>
    private async Task<int> CountsActiveAdminsAsync(CancellationToken ct) =>
        (await users.Store.ListAsync(ct))
            .Count(u => u.Status == UserStatus.Active && u.Tier == KgsmTier.Admin);

    /// <summary>An account as the wire shows it — credentials listed, secrets never.</summary>
    private async Task<UserRecord> ToRecordAsync(KgsmUser user, CancellationToken ct)
    {
        IReadOnlyList<UserCredential> credentials = await users.Store.ListCredentialsAsync(user.UserId, ct);

        return new UserRecord(
            user.UserId,
            user.Username,
            user.DisplayName,
            KgsmTiers.ToWire(user.Tier),
            TierSources.ToWire(user.TierSource),
            UserStatuses.ToWire(user.Status),
            credentials.Any(c => c.Kind == CredentialKind.Password && c.Secret is not null),
            [.. credentials
                .Where(c => c.Kind == CredentialKind.Identity)
                .Select(c => new UserIdentityRecord(
                    c.CredentialId,
                    KgsmActor.TryParse(c.Handle, out string provider, out _) ? provider : c.Handle,
                    c.Handle, c.Label, c.Created, c.LastUsed))],
            user.Created,
            user.Updated);
    }

    /// <summary>
    /// Write one audit row per fact that changed, rather than one "updated" row. An access review
    /// reads for a tier change or for a disable, and a combined row makes both queries a text search.
    /// </summary>
    private async Task RecordChangesAsync(KgsmUser before, KgsmUser after, CancellationToken ct)
    {
        if (before.Tier != after.Tier)
        {
            await RecordAsync(ApiJournal.UserTierChangedEvent, after,
                fromTier: KgsmTiers.ToWire(before.Tier),
                toTier: KgsmTiers.ToWire(after.Tier), ct: ct);
        }

        if (before.Status != after.Status)
        {
            // Which event this is comes from where the account LANDED, not from a verb chosen here:
            // the from/to pair travels on the row, so a reader can tell "disabled" from "returned to
            // awaiting approval" without either being spelled into the record.
            string type = after.Status == UserStatus.Active
                ? ApiJournal.UserApprovedEvent
                : ApiJournal.UserDisabledEvent;

            await RecordAsync(type, after,
                fromStatus: UserStatuses.ToWire(before.Status),
                toStatus: UserStatuses.ToWire(after.Status), ct: ct);
        }
    }

    /// <summary>
    /// Write an API-internal <c>user.*</c>/<c>identity.*</c> row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A direct write, the <c>auth.*</c> posture: kgsm runs nothing for an account change and emits no
    /// event, so there is no echo to wait for and no double-write to risk.
    /// </para>
    /// <para>
    /// The actor is the admin who acted, and the account acted <em>upon</em> rides in meta as
    /// <c>userId</c> — the same split every admin action in this API uses, so "who did this" and "to
    /// whom" never have to be told apart by reading a summary.
    /// </para>
    /// <para>
    /// A failed audit write is logged and never fatal, and no meta this method writes ever carries a
    /// password in any form.
    /// </para>
    /// </remarks>
    private Task RecordAsync(
        string type, KgsmUser subject, string? fromTier = null, string? toTier = null,
        string? fromStatus = null, string? toStatus = null, bool? byHolder = null,
        CancellationToken ct = default)
    {
        KgsmIdentity? caller = Caller();

        return journal.AccountAsync(
            type,
            // The account acted UPON. The admin who acted is the actor — the same split every admin
            // action in this API uses, so "who did this" and "to whom" never have to be told apart by
            // reading a sentence.
            userId: subject.UserId,
            username: subject.Username,
            fromTier: fromTier,
            toTier: toTier,
            fromStatus: fromStatus,
            toStatus: toStatus,
            byHolder: byHolder,
            actor: caller is null
                ? "system:api"
                : KgsmActor.Format(caller.Provider, caller.Username),
            origin: AuditOrigin.Ui,
            ct: ct);
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
