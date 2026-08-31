using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Commands;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// How a command that was accepted actually ended — <c>GET /jobs/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// A command is accepted with <c>202</c> and settles later, so the caller that issued it needs
/// somewhere to read the outcome. A browser watches the <c>jobs</c> stream topic and needs nothing
/// here; a caller acting on somebody's behalf and reporting back to them in one turn cannot hold a
/// stream open for a start that takes half a minute.
/// </para>
/// <para>
/// <b>It is read here and not inferred from the server's run-state.</b> A start that the engine
/// refused leaves the server stopped, which is indistinguishable from a start that was never issued;
/// the reason it failed exists only on the job, and a caller reconstructing an outcome from run-state
/// would report the refusal as a server that simply is not running.
/// </para>
/// <para>
/// A job outlives its own settling deliberately: it is the record of what happened, and forgetting it
/// the moment it finished would make the outcome unreadable exactly when it is asked for. An id the
/// registry has never held is a <c>404</c> — never a fabricated "probably fine".
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/jobs")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class JobsController(JobRegistry jobs) : ControllerBase
{
    /// <summary><c>GET /jobs/{id}</c> → the <see cref="Job"/>, or <c>404</c> for an id this host has no
    /// record of.</summary>
    [HttpGet("{id}")]
    public IActionResult Get(string id) =>
        jobs.Get(id) is { } job ? Ok(job) : NotFound();
}
