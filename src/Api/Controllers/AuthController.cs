using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
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
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>
    /// Begin the OAuth bounce — 302 to Discord's authorize URL (the API owns client id / redirect /
    /// scopes). <c>prompt=none</c> is silent SSO; the client retries with <c>consent</c> on
    /// <c>login_required</c>. 503 until Discord is configured (M4·b).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/auth/discord/start")]
    public IActionResult Start([FromQuery] string? prompt)
    {
        if (!options.DiscordConfigured)
            return Error(StatusCodes.Status503ServiceUnavailable, "auth_unconfigured",
                "Discord auth is not configured on this host.");

        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string url = discord.BuildAuthorizeUrl(state, prompt ?? "none");
        return Redirect(url);
    }

    /// <summary>
    /// The OAuth landing — exchange the code, verify identity, resolve the tier, mint the bearer.
    /// <list type="bullet">
    /// <item><c>200</c> <c>{ verdict:"ok", tier, token, refresh, userId }</c> — authorized.</item>
    /// <item><c>403</c> <c>{ verdict:"denied", userId }</c> — identity verified, no role on this host (terminal).</item>
    /// <item><c>400</c> — missing code · <c>401</c> — bad/expired code · <c>502</c> — Discord unreachable · <c>503</c> — unconfigured.</item>
    /// </list>
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/auth/discord/callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, CancellationToken ct)
    {
        if (!options.DiscordConfigured)
            return Error(StatusCodes.Status503ServiceUnavailable, "auth_unconfigured",
                "Discord auth is not configured on this host.");
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
    /// its tokens; the short access TTL bounds the window. Always <c>204</c> (idempotent).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("/auth/logout")]
    public IActionResult Logout() => NoContent();

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
}
