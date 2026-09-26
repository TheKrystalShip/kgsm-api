using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// <c>GET /api/v1/me/access</c> — what the caller may do on this node, already evaluated, per target.
/// </summary>
/// <remarks>
/// <para>
/// The panel gates a control by looking its action up here; it holds no copy of the rules. The answer
/// is read from this node's replica on every request, so it needs nothing else ready first and never
/// waits on a page it gates. The <c>me</c> topic pushes a fresh one when the replica changes.
/// </para>
/// <para>
/// A replica that cannot be read is <c>503 authority_unavailable</c> — "nothing here can say" is not
/// "you may do nothing". Every authenticated caller may ask, as with <c>/me</c>: somebody holding nothing
/// is exactly who needs to see that.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/me/access")]
[Authorize]
public sealed class MeAccessController(NodeAccess access) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AccessReport>> Get(CancellationToken ct)
    {
        (MemberAccessCaller caller, AccessReport? report) = await access.ReportAsync(User, ct);

        return caller.Refusal switch
        {
            MemberAccessRefusal.None => report!,
            MemberAccessRefusal.Unavailable => StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ErrorEnvelope(new ErrorBody("authority_unavailable", caller.Reason ?? "This node's authority replica could not be read."))),
            MemberAccessRefusal.AccountDisabled => StatusCode(StatusCodes.Status403Forbidden,
                new ErrorEnvelope(new ErrorBody("account_disabled", "This account has been switched off."))),
            _ => StatusCode(StatusCodes.Status403Forbidden,
                new ErrorEnvelope(new ErrorBody("no_account", "No account on this node matches this session."))),
        };
    }
}
