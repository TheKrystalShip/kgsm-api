using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// <c>GET /me</c> — the caller's own identity on this host (reference/node-api.md me surface, the "Profile"
/// resource). It projects the access token's claims: who the provider says the caller is. What the caller
/// may do is <see cref="MeAccessController"/>.
/// </summary>
/// <remarks>
/// <b>Read-only (a documented divergence — see <see cref="MeResponse"/>):</b> everything here is
/// projected from the bearer, so there is nothing on this resource for a PATCH to write. The editable half
/// of the Profile page — the UI density, and anything else that must follow a person rather than a
/// browser — is a preference, and preferences are their own resource: <see cref="MePreferencesController"/>.
/// <para/>
/// Gated at <c>[Authorize]</c> — any authenticated caller — so somebody whose token lists no action can
/// still read "who am I".
/// <para/>
/// <c>status</c> is <c>active</c> for every caller: the provider issues a token only to an account it
/// admits. <c>recentLogins</c> is empty: sign-ins are the provider's to record and are not in this node's
/// audit.
/// </remarks>
[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController : ControllerBase
{
    private const string ActiveStatus = "active";

    [HttpGet]
    public ActionResult<MeResponse> Get()
    {
        if (CallerId.Of(User) is not { } caller || User.Identity is not ClaimsIdentity identity)
            return StatusCode(StatusCodes.Status401Unauthorized,
                new ErrorEnvelope(new ErrorBody("unauthorized", "no session")));

        string username = identity.FindFirst("preferred_username")?.Value is { Length: > 0 } preferred
            ? preferred
            : caller.DisplayName;
        string? avatar = identity.FindFirst("picture")?.Value is { Length: > 0 } picture ? picture : null;
        IReadOnlyList<string> scopes = identity.FindFirst("scope")?.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

        return new MeResponse(
            new SessionUser(caller.Subject, username, caller.DisplayName, avatar),
            scopes,
            [],
            ActiveStatus);
    }
}
