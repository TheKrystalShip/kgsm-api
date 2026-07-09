using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Auth — Discord per-host, Model A (architecture.html §3·f, keystone O5). Identity is a global
/// Discord SSO anchor; authorization is a short-lived host-scoped JWT this host mints after verifying
/// identity once (<c>/users/@me</c>, then the Discord token is discarded) and resolving the role via
/// the host's bot. Stateless — no user row, no session table (the M4 bearer decision).
/// <para>
/// <b>M4·a built:</b> the JWT mint/refresh/session/logout machinery + the verdict logic, all behind
/// the <see cref="IDiscordIdentityResolver"/> seam (fake-tested). <b>M4·b (live):</b> the real Discord
/// code exchange + bot-token role lookup, validated once on the trusted host when the Discord app /
/// bot token / guild / role-map are supplied — until then the login endpoints 503.
/// </para>
/// </summary>
[ApiController]
public sealed class AuthController(
    IDiscordIdentityResolver discord,
    ISessionTokenService tokens,
    SessionStore sessions,
    ISessionValidator sessionValidator,
    ApiOptions options,
    AuditService audit,
    ILogger<AuthController> logger) : ControllerBase
{
    // The OAuth CSRF state cookie — set at /start, verified at /callback. This is the stateless
    // double-submit guard: the random nonce rides BOTH the cookie (HttpOnly, our origin) and the
    // authorize URL's `state` (which Discord echoes back), and the callback requires them equal. No
    // server-side store, so it honors the no-session-table decision. One-time, short-lived.
    private const string StateCookie = "kgsm_oauth_state";
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Begin the OAuth bounce — 302 to Discord's authorize URL (the API owns client id / redirect /
    /// scopes). <c>prompt=none</c> is silent SSO; the client retries with <c>consent</c> on
    /// <c>login_required</c>. Sets the one-time CSRF state cookie verified at the callback. 503 until
    /// Discord is configured (M4·b).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/auth/discord/start")]
    public IActionResult Start([FromQuery] string? prompt)
    {
        if (!options.DiscordConfigured)
            return Error(StatusCodes.Status503ServiceUnavailable, "auth_unconfigured",
                "Discord auth is not configured on this host.");

        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Response.Cookies.Append(StateCookie, state, StateCookieOptions());
        string url = discord.BuildAuthorizeUrl(state, prompt ?? "none");
        return Redirect(url);
    }

    /// <summary>
    /// The OAuth landing — exchange the code, verify identity, resolve the tier, mint the bearer.
    /// <list type="bullet">
    /// <item><c>200</c> <c>{ verdict:"ok", tier, token, refresh, userId }</c> — authorized.</item>
    /// <item><c>403</c> <c>{ verdict:"denied", userId }</c> — identity verified, no role on this host (terminal).</item>
    /// <item><c>400</c> — bad/forged state (<c>invalid_state</c>) or missing code · <c>401</c> — bad/expired code · <c>502</c> — Discord unreachable · <c>503</c> — unconfigured.</item>
    /// </list>
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/auth/discord/callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, CancellationToken ct)
    {
        if (!options.DiscordConfigured)
            return Fail(StatusCodes.Status503ServiceUnavailable, "auth_unconfigured",
                "Discord auth is not configured on this host.");

        // CSRF gate: the state Discord echoes back must equal the nonce we set in the cookie at
        // /start. The cookie is one-time — clear it whatever the outcome (no replay). A missing
        // cookie (expired/never-started) or a mismatch is a forged/stale login -> 400, never a grant.
        // This runs BEFORE any redirect or exchange — the redirect handoff never weakens the gate.
        string? expectedState = Request.Cookies[StateCookie];
        if (expectedState is not null)
            Response.Cookies.Delete(StateCookie, StateCookieOptions());
        if (!StateMatches(state, expectedState))
            return Fail(StatusCodes.Status400BadRequest, "invalid_state",
                "the OAuth state did not validate (possible CSRF, or the login expired — start again).");

        if (string.IsNullOrWhiteSpace(code))
            return Fail(StatusCodes.Status400BadRequest, "bad_request", "missing authorization code");

        ResolvedPrincipal? resolved;
        try
        {
            resolved = await discord.ResolveAsync(code, ct);
        }
        catch (DiscordAuthException ex)
        {
            // Couldn't reach/parse Discord — an honest upstream error, NEVER a default grant.
            logger.LogWarning(ex, "Discord auth exchange failed.");
            return Fail(StatusCodes.Status502BadGateway, "auth_provider_error",
                "Could not complete authentication with Discord.");
        }

        // The code couldn't be exchanged into a verified identity (bad/expired/reused).
        if (resolved is null)
            return Fail(StatusCodes.Status401Unauthorized, "login_required",
                "The authorization code was invalid or expired.");

        string userHandle = $"discord:{resolved.Identity.UserId}";

        // Verified identity, but no role on this host -> terminal 403 (never auto-re-authed).
        if (resolved.Tier == AuthTier.None)
            return options.FrontendRedirectEnabled
                ? FrontendRedirect(Frag(("error", "denied")))
                : StatusCode(StatusCodes.Status403Forbidden,
                    // M4·c Increment 7: no tokens are minted on a denial, so both expiry fields stay
                    // null — WhenWritingNull omits them, keeping this branch's wire shape unchanged.
                    new CallbackResult("denied", null, null, null, userHandle, null, null));

        // M4·c — generate the session id, mint both tokens carrying it (each also carries its own jti
        // for reuse-detection on the refresh path), and persist the session row (the registry is the
        // authority the per-request validator reads to decide "is this session still alive"). The row's
        // `Expires` starts at now + SessionsRefreshAbsoluteDays (the 30d cap, kept in lockstep with
        // SessionTokenService.RefreshTtl); the window is now SLIDING (user directive) — each successful
        // /refresh slides Expires forward + rotates CurrentJti, so a user who opens the panel at least
        // once inside the window stays logged in indefinitely. The row stores the refresh's jti as its
        // initial CurrentJti (the reuse-detection key the refresh action validates against).
        string sessionId = "sid_" + Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MintedToken access = tokens.MintAccess(resolved.Identity, resolved.Tier, sessionId);
        MintedToken refresh = tokens.MintRefresh(resolved.Identity, resolved.Tier, sessionId);

        // Persist the session row. Best-effort: a failed write must never break login — BUT log it
        // loudly, because the per-request validator rejects a token whose sid has no row (D10 clean break,
        // live since Increment 4), so a silent insert failure would defeat the milestone at the next
        // request. The honest recovery for a missing row is a forced relogin (≤15min, the access TTL hard
        // ceiling); we surface the failure as a warning so the operator notices. The row stores the
        // refresh's jti as its initial CurrentJti — the reuse-detection key the refresh action validates.
        string? userAgent = Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent)) userAgent = null;
        try
        {
            await sessions.CreateAsync(
                sessionId, userHandle, options.HostId,
                created: now,
                expires: now + TimeSpan.FromDays(options.SessionsRefreshAbsoluteDays),
                userAgent, refresh.Jti, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Session row insert failed (non-fatal to login, but the validator will reject sid={Sid} — forced relogin follows)",
                sessionId);
        }

        // M5: an auth.login is an API-internal action (no kgsm event), so it is written directly here
        // — no double-write risk. Best-effort: a failed audit write must never break the login. M4·c
        // adds the `sid` to Meta so a login event links to its session row (forensics: "this login
        // created session sid_X, which was later revoked"). M4·c Increment 7 (Group E #11) additionally
        // stamps `userAgent` (the same value just persisted on the session row above) so `/me`'s
        // recent-logins read can honestly label "which device" without a second UA capture — additive
        // to the existing direct-write, NOT a new writer (invariant #5).
        await RecordAuthAsync(AuditAction.AuthLogin, resolved.Identity, resolved.Tier,
            $"{resolved.Identity.Display} logged in", sessionId, userAgent, ct);

        // SPA handoff (when a frontend URL is configured): 302 to the SPA with the tokens in the URL
        // FRAGMENT — never the query, so they don't reach access logs or the Referer header. The SPA
        // reads them, adopts the session, and strips the fragment. Otherwise return the JSON contract
        // (API-only deployments + the auth tests). The redirect target is the single configured URL.
        // M4·c Increment 7 (Group E #12): the JSON contract carries both tokens' mint-time expiry so
        // the SPA can schedule proactive refresh instead of reacting to a 401. Out of scope: the SPA
        // fragment-redirect handoff above is untouched — expiresAt is a JSON-contract-only addition
        // this increment (the fragment already omits `tier` too; adding query params there is a
        // separate, not-yet-needed contract change).
        return options.FrontendRedirectEnabled
            ? FrontendRedirect(Frag(("access", access.Token), ("refresh", refresh.Token)))
            : Ok(new CallbackResult("ok", AuthTiers.ToWire(resolved.Tier), access.Token, refresh.Token, userHandle,
                access.ExpiresAt, refresh.ExpiresAt));
    }

    /// <summary>302 the SPA with the OAuth outcome in the URL fragment. The target is the single
    /// configured <see cref="ApiOptions.AuthFrontendUrl"/> — never a request-supplied URL, so there is
    /// no open-redirect surface.</summary>
    private IActionResult FrontendRedirect(string fragment) =>
        Redirect($"{options.AuthFrontendUrl}#{fragment}");

    /// <summary>A failed/denied/unconfigured outcome: when the SPA handoff is on, redirect with
    /// <c>#error=&lt;code&gt;</c>; otherwise the unchanged JSON error envelope.</summary>
    private IActionResult Fail(int status, string code, string message) =>
        options.FrontendRedirectEnabled
            ? FrontendRedirect(Frag(("error", code)))
            : Error(status, code, message);

    /// <summary>Build a URL fragment <c>k=v&amp;k=v</c>, percent-encoding values and dropping empties.</summary>
    private static string Frag(params (string Key, string? Value)[] parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach ((string key, string? value) in parts)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (sb.Length > 0) sb.Append('&');
            sb.Append(key).Append('=').Append(Uri.EscapeDataString(value!));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Rotate the access AND refresh tokens from a still-valid refresh token (presented as the
    /// <c>Authorization: Bearer</c>), no Discord round-trip. M4·c with rotation: the session row's
    /// <c>CurrentJti</c> is validated against the presented refresh's <c>jti</c> (reuse detection — a
    /// stale jti = an OLD/STOLEN refresh token → <c>401</c>); on a match, BOTH tokens are re-minted
    /// (the refresh's <c>jti</c> rotates, the row slides its <c>Expires</c> forward — the rolling 30-day
    /// window the user directive opened — and bumps <c>LastSeen</c>). The SPA MUST adopt the new
    /// refresh token from the response body on every call (the old one is dead). Role is NOT re-checked
    /// here (the long-term "transparent role changes" idea, deferred).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("/auth/session/refresh")]
    public async Task<IActionResult> Refresh()
    {
        string? token = BearerToken();
        if (token is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "missing refresh token");

        RefreshClaims? claims = await tokens.ReadRefreshAsync(token);
        if (claims is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized",
                "the refresh token is invalid or expired");

        // M4·c rotation — mint BOTH tokens upfront. Each gets a fresh jti; the access jti is
        // informational only (the per-request validator doesn't check jti), the refresh jti is the
        // reuse-detection key that becomes the row's stored CurrentJti. We mint before the row-update
        // so the row-update can store the new refresh's jti in a single serialized round-trip. Both
        // carry the SAME sid (D7/D8: same sid across a session's lifetime). The new refresh
        // SURFACES from the response to the SPA.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MintedToken access = tokens.MintAccess(claims.Identity, claims.Tier, claims.SessionId);
        MintedToken refresh = tokens.MintRefresh(claims.Identity, claims.Tier, claims.SessionId);

        // Validate the presented refresh's jti against the row's stored CurrentJti + slide Expires
        // + bump LastSeen + store the new refresh's jti (rotation). Returns false when: no row / row
        // revoked / jti mismatch (stale/old/stolen refresh — reuse detection). The 401 here is the
        // SPA's signal to re-authenticate via Discord OAuth; an attacker with an old refresh token
        // keeps getting 401s while the legit user's NEW token (from their previous refresh) succeeds.
        // ⚠ No grace period — a tab race resolves to ONE tab 401ing (re-auth on reload); acceptable
        // for a small panel (the alternative — grace-period jti tracking — needs another column).
        DateTimeOffset newExpires = now + TimeSpan.FromDays(options.SessionsRefreshAbsoluteDays);
        bool rotated = await sessions.UpdateForRefreshAsync(
            claims.SessionId, claims.Jti, refresh.Jti, newExpires, CancellationToken.None);
        if (!rotated)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized",
                "the refresh token is invalid, expired, or has been superseded");

        return Ok(new RefreshResponse(access.Token, refresh.Token, AuthTiers.ToWire(claims.Tier), access.ExpiresAt));
    }

    /// <summary>
    /// The profile snapshot behind the bearer (captured at login), or <c>401</c>. §3·f divergence:
    /// this is the login-time snapshot, NOT a fresh live fetch — we keep no Discord token to re-fetch with.
    /// </summary>
    [Authorize]
    [HttpGet("/auth/session")]
    public ActionResult<SessionResponse> Session()
    {
        DiscordIdentity? id = User.Identity is System.Security.Claims.ClaimsIdentity ci
            ? SessionClaims.ReadIdentity(ci)
            : null;
        if (id is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no session");

        return new SessionResponse(
            new SessionUser($"discord:{id.UserId}", id.Username, id.Display, id.AvatarUrl),
            id.Scopes);
    }

    /// <summary>
    /// End the session — server-side (M4·c). Reads the <c>sid</c> off the bearer, revokes the session
    /// row (<c>Revoked=true</c>) and evicts it from the validator cache, so every token carrying that
    /// sid (the access bearer AND the refresh token) stops authorizing on its next request (~instant via
    /// the cache eviction; the ≤15min access TTL is the hard ceiling regardless). Always <c>204</c>
    /// (idempotent — an already-revoked or absent session is a no-op). If the call carries a resolvable
    /// bearer we also record an <c>auth.logout</c> audit row (best-effort); an unauthenticated logout
    /// (no bearer) can't be attributed, so it simply returns 204 with no revoke and no row.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("/auth/logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (User.Identity is ClaimsIdentity ci && ci.IsAuthenticated
            && SessionClaims.ReadIdentity(ci) is { } id)
        {
            // M4·c — revoke the session server-side. Read the sid off the bearer, flip the row to
            // Revoked=true (idempotent) and evict the validator cache so the next request on this sid
            // 401s immediately rather than waiting for the 5s TTL backstop. Best-effort: a failed
            // revoke must never turn a logout into an error (the client is dropping its tokens anyway,
            // and the access TTL still bounds the window) — log it, then still write the audit row.
            string? sid = SessionClaims.ReadSessionId(ci);
            if (!string.IsNullOrEmpty(sid))
            {
                try
                {
                    await sessions.RevokeAsync(sid, ct);
                    sessionValidator.Evict(sid);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "session revoke on logout failed (non-fatal) sid={Sid}", sid);
                }
            }
            // The audit Meta carries the sid (forensics: links logout → the revoked session row). No
            // userAgent here — recentLogins reads `auth.login` rows only, and logout's own UA isn't
            // otherwise useful; keep the meta minimal for the action it's on.
            await RecordAuthAsync(AuditAction.AuthLogout, id, SessionClaims.ReadTier(ci),
                $"{id.Display} logged out", sid, null, ct);
        }
        return NoContent();
    }

    // The bearer from the Authorization header, or null. Used by /refresh (which can't use [Authorize]:
    // the refresh token is deliberately rejected as an access bearer by the JwtBearer pipeline).
    private string? BearerToken()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        string token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));

    // Write an API-internal auth.* audit row. origin = "ui": an interactive Discord OAuth login/logout
    // is a human acting through the web surface (there is no headless login path). actor = the Discord
    // identity (kind=user, provider=discord). Best-effort: a failed audit write is logged, never fatal.
    // M4·c — the `sid` lands in Meta so a login/logout event links to its session row (forensics).
    // M4·c Increment 7 — `userAgent` lands in Meta (additive to the existing direct-write, not a new
    // action or a second writer) so `/me`'s recent-logins read can honestly report "which device";
    // omitted (not empty-string) when blank, matching the never-fabricate posture elsewhere in Meta.
    private async Task RecordAuthAsync(string action, DiscordIdentity id, AuthTier tier, string summary,
        string? sid, string? userAgent, CancellationToken ct)
    {
        try
        {
            var meta = new Dictionary<string, string> { ["tier"] = AuthTiers.ToWire(tier) };
            if (!string.IsNullOrEmpty(sid))
                meta["sid"] = sid;
            if (!string.IsNullOrEmpty(userAgent))
                meta["userAgent"] = userAgent;
            await audit.AppendAsync(new AuditWrite(
                Ts: DateTimeOffset.UtcNow,
                Origin: AuditOrigin.Ui,
                Actor: new AuditActor(ActorKind.User, id.Username, ActorProvider.Discord),
                Action: action,
                Severity: AuditSeverity.Info,
                Target: null,
                ServerId: null,
                HostId: options.HostId,
                Summary: summary,
                Meta: meta),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "audit {Action} write failed (non-fatal)", action);
        }
    }

    // The CSRF state cookie's attributes — shared by the set (at /start) and the delete (at /callback,
    // where Path must match for the deletion to take). Secure tracks the scheme so it works on an http
    // loopback dev host yet is Secure on a real https host; SameSite=Lax (NOT Strict) so the cookie
    // still rides Discord's top-level cross-site redirect back to the callback.
    private CookieOptions StateCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/auth/discord",
        IsEssential = true,
        MaxAge = StateTtl,
    };

    // Constant-time compare of the echoed state against the cookie nonce; either missing => no match.
    private static bool StateMatches(string? echoed, string? expected)
    {
        if (string.IsNullOrEmpty(echoed) || string.IsNullOrEmpty(expected)) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(echoed),
            System.Text.Encoding.ASCII.GetBytes(expected));
    }
}
