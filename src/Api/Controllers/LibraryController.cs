using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Library;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The installable-game catalog read surface (<c>architecture.html §3·h/§3·i</c>, M8·a). A pure
/// scrape of this host's kgsm blueprints (via kgsm-lib), mapped to the honest <see cref="LibraryEntry"/>
/// shape — no mutation (that is <c>POST /servers</c>, M8·b), no cover-art resolution yet (reserved).
/// </summary>
[ApiController]
[Route("api/v1/library")]
[Authorize(Policy = AuthPolicy.Viewer)] // reads — viewer and up (M4·a)
public sealed class LibraryController(LibraryAggregator aggregator) : ControllerBase
{
    /// <summary>
    /// <c>GET /library?q=&amp;category=</c> — the installable games. <paramref name="q"/> is an
    /// optional case-insensitive substring filter over id + name. <paramref name="category"/> is
    /// accepted for forward-compatibility with <c>§3·h</c> but <strong>RESERVED/inert</strong> — there
    /// is no honest game-genre source on a blueprint today, so it is never applied (silently filtering
    /// on an unsourced field would fabricate a taxonomy).
    /// </summary>
    [HttpGet]
    public async Task<IReadOnlyList<LibraryEntry>> Get(
        [FromQuery] string? q,
        [FromQuery] string? category,
        CancellationToken ct)
    {
        _ = category; // reserved — see the doc remark.
        return await aggregator.GetLibraryAsync(q, ct);
    }
}
