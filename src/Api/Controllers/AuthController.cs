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
            return Error(StatusCodes.Status503ServiceUnavailable, "auth_unconfigured",
                "Discord auth is not configured on this host.");

        // CSRF gate: the state Discord echoes back must equal the nonce we set in the cookie at
        // /start. The cookie is one-time — clear it whatever the outcome (no replay). A missing
        // cookie (expired/never-started) or a mismatch is a forged/stale login -> 400, never a grant.
        string? expectedState = Request.Cookies[StateCookie];
        if (expectedState is not null)
            Response.Cookies.Delete(StateCookie, StateCookieOptions());
        if (!StateMatches(state, expectedState))
            return Error(StatusCodes.Status400BadRequest, "invalid_state",
                "the OAuth state did not validate (possible CSRF, or the login expired — start again).");

        if (string.IsNullOrWhiteSpace(code))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "missing authorization code");

        ResolvedPrincipal? resolved;
        try
        {
            resolved = await discord.ResolveAsync(code, ct);
        }
        catch (DiscordAuthException ex)
        {
            // Couldn't reach/parse Discord — an honest upstream error, NEVER a default grant.
            logger.LogWarning(ex, "Discord auth exchange failed.");
            return Error(StatusCodes.Status502BadGateway, "auth_provider_error",
                "Could not complete authentication with Discord.");
        }

        // The code couldn't be exchanged into a verified identity (bad/expired/reused).
        if (resolved is null)
            return Error(StatusCodes.Status401Unauthorized, "login_required",
                "The authorization code was invalid or expired.");

        string userHandle = $"discord:{resolved.Identity.UserId}";

        // Verified identity, but no role on this host -> terminal 403 (never auto-re-authed).
        if (resolved.Tier == AuthTier.None)
            return StatusCode(StatusCodes.Status403Forbidden,
                new CallbackResult("denied", null, null, null, userHandle));

        string access = tokens.MintAccess(resolved.Identity, resolved.Tier);
        string refresh = tokens.MintRefresh(resolved.Identity, resolved.Tier);

        // M5: an auth.login is an API-internal action (no kgsm event), so it is written directly here
        // — no double-write risk. Best-effort: a failed audit write must never break the login.
        await RecordAuthAsync(AuditAction.AuthLogin, resolved.Identity, resolved.Tier,
            $"{resolved.Identity.Display} logged in", ct);

        return Ok(new CallbackResult("ok", AuthTiers.ToWire(resolved.Tier), access, refresh, userHandle));
    }

    /// <summary>
    /// Rotate the access token from a still-valid refresh token (presented as the <c>Authorization:
    /// Bearer</c>), no Discord round-trip. Past the 8h cap the refresh token is invalid → <c>401</c>
    /// → the client re-bounces through the anchor. Role is not re-checked here (see <see cref="RefreshClaims"/>).
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

        string access = tokens.MintAccess(claims.Identity, claims.Tier);
        return Ok(new RefreshResponse(access));
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
    /// End the session. Stateless JWT — there is nothing server-side to revoke, so the client drops
    /// its tokens; the short access TTL bounds the window. Always <c>204</c> (idempotent). If the call
    /// carries a resolvable bearer we record an <c>auth.logout</c> (best-effort) — anonymous, so we
    /// can't attribute an unauthenticated logout and simply skip the row.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("/auth/logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (User.Identity is ClaimsIdentity ci && ci.IsAuthenticated
            && SessionClaims.ReadIdentity(ci) is { } id)
        {
            await RecordAuthAsync(AuditAction.AuthLogout, id, SessionClaims.ReadTier(ci),
                $"{id.Display} logged out", ct);
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
    private async Task RecordAuthAsync(string action, DiscordIdentity id, AuthTier tier, string summary,
        CancellationToken ct)
    {
        try
        {
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
                Meta: new Dictionary<string, string> { ["tier"] = AuthTiers.ToWire(tier) }),
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
