using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// <c>GET /api/v1/me/access</c> — what the caller may do on this node, already evaluated.
/// </summary>
/// <remarks>
/// <para>
/// The panel gates a control by looking its action up here; it holds no copy of the rules. The answer is
/// the actions the caller's token lists, among those this node performs, so it needs nothing else ready
/// first and never waits on a page it gates. A role change reaches a person at their next token renewal.
/// </para>
/// <para>
/// Every authenticated caller may ask, as with <c>/me</c>: somebody holding nothing is exactly who needs
/// to see that.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/me/access")]
[Authorize]
public sealed class MeAccessController(NodeAccess access) : ControllerBase
{
    [HttpGet]
    public ActionResult<AccessReport> Get() => access.ReportFor(User);
}
