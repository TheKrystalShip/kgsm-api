using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Connected accounts — what can sign the caller in.
/// </summary>
/// <remarks>
/// <para>
/// A KGSM account is the primary object and a provider identity is one credential attached to it, so
/// <b>any Discord account can be attached to any KGSM account</b> and which server it is in says
/// nothing about either. Attaching is a deliberate act by the person who already holds the account:
/// nothing here ever matches on an email or a username, because providers disagree about what
/// "verified" means and matching on one is a documented account-takeover route.
/// </para>
/// <para>
/// Everything here is <b>self-service</b> — the caller's own account, read off their own validated
/// claims, never off a request body. An admin changes what an account may do (that is
/// <see cref="UsersController"/>); only its holder changes what proves it.
/// </para>
/// <para>
/// Both writes need a recently proved credential (<see cref="ReauthGate"/>), because a link outlives
/// the session that makes it: afterwards, whoever holds that provider account can sign in as this one
/// forever, and a live session alone can be a borrowed unlocked laptop.
/// </para>
/// </remarks>
[ApiController]
public sealed class IdentitiesController(
    UserDirectory users,
    IAuthProviderCatalog providers,
    ReauthGate reauth,
    LinkTicketStore tickets,
    SessionStore sessions,
    ISessionValidator sessionValidator,
    ApiOptions options,
    ApiJournal journal,
    ILogger<IdentitiesController> logger) : ControllerBase
{
    /// <summary>
    /// The in-flight link cookie — set at <c>/start</c>, consumed at <c>/callback</c>. It carries an
    /// opaque ticket and nothing else: which account is being changed stays server-side, because a
    /// cookie is a value the browser holds and the browser is not the authority on whose account this
    /// is. HttpOnly, our origin, one-time, short-lived.
    /// </summary>
    private const string TicketCookie = "kgsm_link_ticket";

    /// <summary>
    /// <c>GET /auth/identities</c> — the caller's sign-in methods, and what else this host offers.
    /// </summary>
    [Authorize(Policy = AuthPolicy.Viewer)]
    [HttpGet("/auth/identities")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        if (await CallerAccountAsync(ct) is not { } account)
            return NoAccount();

        return Ok(await SnapshotAsync(account, ct));
    }

    /// <summary>
    /// <c>POST /auth/reauth</c> — prove the caller's KGSM password again, opening the window in which
    /// they may attach or detach a sign-in method.
    /// <list type="bullet">
    /// <item><c>200</c> <see cref="ReauthResult"/> — proved, with when it lapses.</item>
    /// <item><c>403</c> <c>invalid_credentials</c> — wrong password.</item>
    /// <item><c>409</c> <c>no_password</c> — this account has no KGSM password to prove. Signing in
    /// again is the way through, and it is not a dead end: a sign-in stamps the session it mints.</item>
    /// <item><c>429</c> <c>too_many_attempts</c> — the same lockout a login gets, because this is the
    /// same check and an unbounded one here would be a way around it.</item>
    /// </list>
    /// </summary>
    [Authorize(Policy = AuthPolicy.Viewer)]
    [HttpPost("/auth/reauth")]
    public async Task<IActionResult> Reauth([FromBody] ReauthRequest? body, CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        if (await CallerAccountAsync(ct) is not { } account)
            return NoAccount();

        if (body is null || string.IsNullOrEmpty(body.Password))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "a password is required");

        IReadOnlyList<UserCredential> credentials = await users.Store.ListCredentialsAsync(account.UserId, ct);
        if (!credentials.Any(c => c.Kind == CredentialKind.Password && c.Secret is not null))
            return Error(StatusCodes.Status409Conflict, "no_password",
                "This account has no KGSM password. Sign in again to change your connected accounts.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        LocalSignInResult result = await users.SignIn.SignInAsync(account.Username, body.Password, now, ct);

        if (result.Outcome == LocalSignInOutcome.LockedOut)
        {
            int seconds = Math.Max(1, (int)Math.Ceiling(((result.RetryAfter ?? now) - now).TotalSeconds));
            Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Error(StatusCodes.Status429TooManyRequests, "too_many_attempts",
                $"Too many failed attempts. Try again in {seconds}s.");
        }

        if (result.Outcome != LocalSignInOutcome.Success)
            return Error(StatusCodes.Status403Forbidden, "invalid_credentials", "That password is not correct.");

        string? sid = SessionId();
        reauth.Stamp(sid);
        return Ok(new ReauthResult(reauth.FreshUntil(sid) ?? now + reauth.Window));
    }

    /// <summary>
    /// <c>POST /auth/identities/{provider}/start</c> — begin attaching an account at that provider.
    /// <list type="bullet">
    /// <item><c>200</c> <see cref="LinkStartResponse"/> — the authorize URL to send the browser to,
    /// with the one-time ticket cookie set alongside it.</item>
    /// <item><c>403</c> <c>reauth_required</c> — this session has not proved a credential recently.</item>
    /// <item><c>409</c> <c>already_linked</c> — an account at that provider is attached already.
    /// Detach it first, so swapping one for another is two deliberate acts rather than a silent
    /// replacement.</item>
    /// <item><c>503</c> <c>auth_unconfigured</c> — this host does not offer that provider.</item>
    /// </list>
    /// </summary>
    [Authorize(Policy = AuthPolicy.Viewer)]
    [HttpPost("/auth/identities/{provider}/start")]
    public async Task<IActionResult> StartLink(string provider, CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        if (providers.Link(provider) is not { } identityProvider)
            return Error(StatusCodes.Status503ServiceUnavailable, "auth_unconfigured",
                $"Connecting a {provider} account is not configured on this host.");

        if (await CallerAccountAsync(ct) is not { } account)
            return NoAccount();

        string? sid = SessionId();
        if (!reauth.IsFresh(sid))
            return ReauthRequired();

        // One account per provider, per KGSM account. The store's own constraint is stricter in a
        // different direction — an identity belongs to exactly one account, table-wide — so it would
        // let somebody attach a second GitHub account here and never say which one signs them in.
        IReadOnlyList<UserCredential> credentials = await users.Store.ListCredentialsAsync(account.UserId, ct);
        if (credentials.Any(c =>
                c.Kind == CredentialKind.Identity
                && string.Equals(Provider(c.Handle), identityProvider.Provider, StringComparison.OrdinalIgnoreCase)))
            return Error(StatusCodes.Status409Conflict, "already_linked",
                $"A {identityProvider.Provider} account is already connected. Disconnect it first.");

        OAuthHandshake handshake = OAuthHandshake.Create();
        string ticket = tickets.Issue(account.UserId, sid ?? string.Empty, handshake);
        Response.Cookies.Append(TicketCookie, ticket, TicketCookieOptions());

        // `consent` rather than the silent `none` a login uses: someone attaching an account is
        // choosing WHICH account, and a silent bounce would attach whichever one that browser happens
        // to be signed into without ever showing them which.
        return Ok(new LinkStartResponse(
            identityProvider.BuildAuthorizeUrl(handshake.State, handshake.CodeChallenge, "consent")));
    }

    /// <summary>
    /// <c>GET /auth/identities/{provider}/callback</c> — where the provider returns the browser,
    /// attaching the verified identity to the account that started the link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anonymous by necessity: this is a top-level navigation from the provider and a bearer does not
    /// survive one. What authorizes it is the ticket — issued to a session that had proved a credential
    /// minutes ago, single-use, and holding the account id server-side so the browser never carries it.
    /// </para>
    /// <para>
    /// Freshness is checked when the link is <em>started</em> and not again here: the bounce takes as
    /// long as it takes, and re-checking would fail a link somebody legitimately began while adding
    /// nothing — the ticket is already one-use, short-lived, and unforgeable.
    /// </para>
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("/auth/identities/{provider}/callback")]
    public async Task<IActionResult> CompleteLink(
        string provider, [FromQuery] string? code, [FromQuery] string? state, CancellationToken ct)
    {
        string? cookie = Request.Cookies[TicketCookie];
        if (cookie is not null)
            Response.Cookies.Delete(TicketCookie, TicketCookieOptions());

        LinkTicket? ticket = tickets.Redeem(cookie, state);
        if (ticket is null)
            return LinkFailed(StatusCodes.Status400BadRequest, "invalid_state",
                "That link request did not validate (possible CSRF, or it expired — start again).");

        if (string.IsNullOrWhiteSpace(code))
            return LinkFailed(StatusCodes.Status400BadRequest, "bad_request", "missing authorization code");

        if (!users.Available)
            return LinkFailed(StatusCodes.Status502BadGateway, "authority_unavailable",
                users.UnavailableReason ?? "The KGSM account store is unavailable on this host.");

        if (providers.Link(provider) is not { } identityProvider)
            return LinkFailed(StatusCodes.Status503ServiceUnavailable, "auth_unconfigured",
                $"Connecting a {provider} account is not configured on this host.");

        KgsmIdentity? verified;
        try
        {
            verified = await identityProvider.VerifyAsync(code, ticket.Handshake.CodeVerifier, ct);
        }
        catch (KgsmAuthProviderException ex)
        {
            logger.LogWarning(ex, "{Provider} link exchange failed.", identityProvider.Provider);
            return LinkFailed(StatusCodes.Status502BadGateway, "auth_provider_error",
                "Could not complete authentication with the identity provider.");
        }

        if (verified is null)
            return LinkFailed(StatusCodes.Status401Unauthorized, "login_required",
                "The authorization code was invalid or expired.");

        LinkResult link;
        try
        {
            link = await users.Linking.LinkAsync(ticket.UserId, verified, DateTimeOffset.UtcNow, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not attach {Handle} to {UserId}.", verified.Handle, ticket.UserId);
            return LinkFailed(StatusCodes.Status502BadGateway, "authority_unavailable",
                "The KGSM account store could not be written.");
        }

        // Attached to somebody else. Refused rather than moved: re-pointing a credential hands one
        // person another's account, and the person on the other end would never learn it had happened.
        if (link.Outcome == LinkOutcome.AlreadyLinked)
            return link.User is null
                ? LinkFailed(StatusCodes.Status404NotFound, "no_account", "That account no longer exists.")
                : LinkFailed(StatusCodes.Status409Conflict, "identity_taken",
                    "That Discord account is already connected to another KGSM account.");

        KgsmUser account = link.User!;

        // Provisioned here means the credential was added — Existing means it was already on this same
        // account, which is a repeated click and not something to write a privilege event about.
        if (link.Outcome == LinkOutcome.Provisioned)
        {
            await RecordAsync(ApiJournal.IdentityLinkedEvent, account, verified.Provider, verified.Handle, ct);
        }

        return options.FrontendRedirectEnabled
            ? Redirect($"{options.AuthFrontendUrl}#linked={Uri.EscapeDataString(verified.Provider)}")
            : Ok(await SnapshotAsync(account, ct));
    }

    /// <summary>
    /// <c>DELETE /auth/identities/{credentialId}</c> — detach one of the caller's sign-in methods.
    /// <list type="bullet">
    /// <item><c>204</c> — detached. Every session that identity established on this host is revoked
    /// with it: the point of disconnecting an account is that it no longer gets in.</item>
    /// <item><c>403</c> <c>reauth_required</c> — this session has not proved a credential recently.</item>
    /// <item><c>404</c> <c>not_found</c> — not one of the caller's connected identities. The same
    /// answer for one that belongs to somebody else, so an id can never be probed for existence.</item>
    /// <item><c>409</c> <c>last_credential</c> — it is the only thing that can sign this account in.</item>
    /// </list>
    /// </summary>
    [Authorize(Policy = AuthPolicy.Viewer)]
    [HttpDelete("/auth/identities/{credentialId}")]
    public async Task<IActionResult> Unlink(string credentialId, CancellationToken ct)
    {
        if (Unavailable() is { } unavailable)
            return unavailable;

        if (await CallerAccountAsync(ct) is not { } account)
            return NoAccount();

        if (!reauth.IsFresh(SessionId()))
            return ReauthRequired();

        IReadOnlyList<UserCredential> credentials = await users.Store.ListCredentialsAsync(account.UserId, ct);
        UserCredential? credential = credentials.FirstOrDefault(
            c => c.CredentialId == credentialId && c.Kind == CredentialKind.Identity);

        if (credential is null)
            return Error(StatusCodes.Status404NotFound, "not_found",
                "That is not one of your connected accounts.");

        UnlinkOutcome outcome = await users.Linking.UnlinkAsync(account.UserId, credentialId, ct);

        if (outcome == UnlinkOutcome.LastCredential)
            return Error(StatusCodes.Status409Conflict, "last_credential",
                "That is the only way you can sign in. Set a KGSM password first.");

        if (outcome == UnlinkOutcome.NotFound)
            return Error(StatusCodes.Status404NotFound, "not_found",
                "That is not one of your connected accounts.");

        // The sessions that identity established stop working now, not whenever they happen to expire.
        // The session registry keys a row by the handle it was minted under, so this is exactly the
        // sessions that came in through the credential just removed — the caller's password session, if
        // they are holding one, is untouched.
        try
        {
            foreach (string sid in await sessions.RevokeAllForUserAsync(credential.Handle, options.HostId, ct))
                sessionValidator.Evict(sid);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not revoke the sessions behind {Handle} after detaching it (non-fatal).", credential.Handle);
        }

        await users.ForgetAsync(account.UserId, ct);
        await RecordAsync(ApiJournal.IdentityUnlinkedEvent, account,
            Provider(credential.Handle), credential.Handle, ct);

        return NoContent();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The caller's identity off their own validated claims, never off a request body.</summary>
    private KgsmIdentity? Caller() =>
        User.Identity is ClaimsIdentity ci ? SessionClaims.ReadIdentity(ci) : null;

    private string? SessionId() =>
        User.Identity is ClaimsIdentity ci ? SessionClaims.ReadSessionId(ci) : null;

    /// <summary>The KGSM account behind the caller's session, whichever credential proved it.</summary>
    private async Task<KgsmUser?> CallerAccountAsync(CancellationToken ct) =>
        Caller() is { } caller ? await users.Authority.FindAsync(caller, ct) : null;

    private async Task<IdentitiesResponse> SnapshotAsync(KgsmUser account, CancellationToken ct)
    {
        IReadOnlyList<UserCredential> credentials = await users.Store.ListCredentialsAsync(account.UserId, ct);

        List<UserIdentityRecord> identities =
        [
            .. credentials
                .Where(c => c.Kind == CredentialKind.Identity)
                .Select(c => new UserIdentityRecord(
                    c.CredentialId, Provider(c.Handle), c.Handle, c.Label, c.Created, c.LastUsed)),
        ];

        DateTimeOffset? freshUntil = reauth.FreshUntil(SessionId());

        return new IdentitiesResponse(
            account.UserId,
            account.Username,
            credentials.Any(c => c.Kind == CredentialKind.Password && c.Secret is not null),
            identities,
            [
                .. providers.Registered.Select(p => new LinkableProvider(
                    p,
                    Configured: providers.IsConfigured(p),
                    Linked: identities.Any(i => string.Equals(i.Provider, p, StringComparison.OrdinalIgnoreCase)))),
            ],
            new ReauthState(freshUntil is not null, freshUntil, (int)reauth.Window.TotalMinutes));
    }

    /// <summary>The provider half of a credential handle, or the whole handle when it carries none.</summary>
    private static string Provider(string handle) =>
        KgsmActor.TryParse(handle, out string provider, out _) ? provider : handle;

    private ObjectResult? Unavailable() =>
        users.Available
            ? null
            : Error(StatusCodes.Status503ServiceUnavailable, "users_unavailable",
                users.UnavailableReason ?? "The KGSM account store is unavailable on this host.");

    private ObjectResult NoAccount() =>
        Error(StatusCodes.Status404NotFound, "no_account", "This session has no KGSM account on this host.");

    private ObjectResult ReauthRequired() =>
        Error(StatusCodes.Status403Forbidden, "reauth_required",
            "Confirm your password before changing how you sign in.");

    /// <summary>
    /// A failed link: when the SPA handoff is on, 302 back to it with the reason in the fragment;
    /// otherwise the JSON error envelope. Same shape the OAuth login's failures take, so a browser
    /// always lands somewhere it can render.
    /// </summary>
    private IActionResult LinkFailed(int status, string code, string message) =>
        options.FrontendRedirectEnabled
            ? Redirect($"{options.AuthFrontendUrl}#link_error={Uri.EscapeDataString(code)}")
            : Error(status, code, message);

    /// <summary>
    /// Record an identity being attached to or detached from an account, in this API's own journal.
    /// Nothing runs on the engine for a credential change, so this API is the author of the fact.
    /// </summary>
    /// <remarks>
    /// The actor is the person who acted, which here is always the account's own holder. The handle
    /// that was attached or detached is on the row — never a token, and there is none to carry: a link
    /// keeps no credential at the provider.
    /// </remarks>
    private Task RecordAsync(
        string type, KgsmUser account, string provider, string handle, CancellationToken ct)
    {
        KgsmIdentity? caller = Caller();

        return journal.IdentityAsync(
            type,
            userId: account.UserId,
            username: account.Username,
            provider: provider,
            handle: handle,
            actor: caller is null
                ? "system:api"
                : KgsmActor.Format(caller.Provider, caller.Username),
            origin: AuditOrigin.Ui,
            ct: ct);
    }

    /// <summary>
    /// The ticket cookie's attributes — shared by the set and the delete, where Path must match for the
    /// deletion to take. Secure tracks the scheme so a loopback dev host works; SameSite=Lax (never
    /// Strict) so the cookie still rides Discord's top-level redirect back.
    /// </summary>
    private CookieOptions TicketCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/auth/identities",
        IsEssential = true,
        MaxAge = LinkTicketStore.Ttl,
    };

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
