using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// <c>GET /me</c> — the caller's own identity and what it may do on this host (architecture.html §3·f
/// surface, the "Profile" resource). It projects the session bearer's claims: the Discord identity
/// snapshot captured at login plus the resolved authorization <c>tier</c> and granted <c>scopes</c>.
/// The SPA gates which controls it renders on <c>tier</c>, so this is the surface it reads on load.
/// </summary>
/// <remarks>
/// <b>Read-only (a documented divergence — see <see cref="MeResponse"/>):</b> the editable Profile half
/// (display name, density) needs a per-panel preference store that is deliberately not built, so PATCH is
/// deferred. The honest delta this adds over <c>GET /auth/session</c> (which returns <c>{ user, scopes }</c>)
/// is the <c>tier</c> — the one fact the SPA needs to decide what to show, and the reason <c>/me</c> exists
/// as its own resource rather than the SPA inferring authority from a 403.
/// <para/>
/// Gated at <c>[Authorize]</c> — any authenticated caller, mirroring <c>/auth/session</c>, NOT viewer — so a
/// <c>none</c>-tier caller (identity verified, no role on this host) can still read "who am I / why am I 403
/// elsewhere" honestly instead of being shut out of their own identity. The tier is read from the bearer's
/// claim verbatim; no role re-check happens here (that lives in the login/refresh path).
/// </remarks>
[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController : ControllerBase
{
    [HttpGet]
    public ActionResult<MeResponse> Get()
    {
        if (User.Identity is not ClaimsIdentity ci || SessionClaims.ReadIdentity(ci) is not { } id)
            return StatusCode(StatusCodes.Status401Unauthorized,
                new ErrorEnvelope(new ErrorBody("unauthorized", "no session")));

        return new MeResponse(
            new SessionUser($"discord:{id.UserId}", id.Username, id.Display, id.AvatarUrl),
            AuthTiers.ToWire(SessionClaims.ReadTier(ci)),
            id.Scopes);
    }
}
