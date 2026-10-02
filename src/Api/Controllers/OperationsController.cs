using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Auth.Access;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// <c>GET /api/v1/operations</c> — every gated route this node serves and the action it requires.
/// </summary>
/// <remarks>
/// A client reads this to know which action a request needs, and <c>/me/access</c> for whether the
/// caller holds it; it names no action of its own. Built from the routes as this build maps them
/// (<see cref="ApiOperations"/>), so it changes when the routes do and at no other time. Any signed-in
/// caller may read it: it describes this build, not anybody's access.
/// </remarks>
[ApiController]
[Route("api/v1/operations")]
[Authorize]
public sealed class OperationsController(EndpointDataSource endpoints) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() =>
        new JsonResult(ApiOperations.Build(endpoints), AccessJsonContext.Default.Options);
}
