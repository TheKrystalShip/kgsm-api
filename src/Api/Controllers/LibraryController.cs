using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Library;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The installable-game catalog read surface (<c>architecture.html §3·h/§3·i</c>, M8·a). A scrape of this
/// host's kgsm blueprints (via kgsm-lib) joined with this host's cached RAWG.io cover/metadata, mapped to the
/// honest <see cref="LibraryEntry"/> shape — no mutation (that is <c>POST /servers</c>, M8·b). The
/// <c>GET /library</c> listing is viewer-gated; the <c>/{id}/cover</c> + <c>/{id}/hero</c> image endpoints are
/// <see cref="AllowAnonymousAttribute">anonymous</see> (game art is not sensitive, and a CSS
/// <c>background:url(...)</c> / <c>&lt;img&gt;</c> never sends the bearer token).
/// </summary>
[ApiController]
[Route("api/v1/library")]
[Authorize(Policy = AuthPolicy.Viewer)] // reads — viewer and up (M4·a)
public sealed class LibraryController(
    LibraryAggregator aggregator, ApiOptions options, RawgHydrationWorker refresher) : ControllerBase
{
    /// <summary>
    /// <c>POST /library/refresh</c> — force an immediate full re-fetch of every blueprint's cover + metadata
    /// from Steam/RAWG (the on-demand counterpart to the periodic worker — handy right after a blueprint's
    /// Steam App ID / rawg_slug is corrected, instead of waiting for the next scheduled run). <strong>Admin</strong>:
    /// it spends the RAWG budget and rewrites the cache. Returns <c>202</c> (the sweep runs off the request
    /// thread) or <c>409</c> when a sweep is already in flight (the boot/periodic sweep, or a prior refresh).
    /// </summary>
    [HttpPost("refresh")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public IActionResult Refresh() =>
        refresher.RequestRefresh()
            ? Accepted()
            : Error(StatusCodes.Status409Conflict, "conflict", "a library refresh is already in progress");

    /// <summary>
    /// <c>GET /library?q=&amp;category=</c> — the installable games. <paramref name="q"/> is an optional
    /// case-insensitive substring filter over id + name. <paramref name="category"/> is accepted for
    /// forward-compatibility with <c>§3·h</c> but <strong>RESERVED/inert</strong> — there is no honest
    /// game-genre source on a blueprint today, so it is never applied (silently filtering on an unsourced
    /// field would fabricate a taxonomy).
    /// </summary>
    [HttpGet]
    public async Task<IReadOnlyList<LibraryEntry>> Get(
        [FromQuery] string? q,
        [FromQuery] string? category,
        CancellationToken ct)
    {
        _ = category; // reserved — see the doc remark.
        return await aggregator.GetLibraryAsync(q, BaseUrl(), ct);
    }

    /// <summary>
    /// <c>GET /library/{id}/cover</c> — stream the cached cover-art bytes (<c>image/jpeg</c> + an <c>ETag</c>,
    /// conditional-GET / 304 via <see cref="ControllerBase.PhysicalFile(string, string, string)"/>). <c>404</c>
    /// when no image is cached. <strong>Anonymous</strong> by design (see the type remark).
    /// </summary>
    [HttpGet("{id}/cover")]
    [AllowAnonymous]
    public IActionResult Cover(string id) => ServeImage(id, RawgCache.CoverSlot);

    /// <summary><c>GET /library/{id}/hero</c> — the cached hero/screenshot bytes; anonymous; 404 when absent.</summary>
    [HttpGet("{id}/hero")]
    [AllowAnonymous]
    public IActionResult Hero(string id) => ServeImage(id, RawgCache.HeroSlot);

    // Serve a cached image file from disk with an ETag (the real existence check is here — File.Exists → 404 —
    // so a manually-deleted file still 404s honestly even if a stale row claims it). PhysicalFile with an
    // EntityTagHeaderValue gives us the ETag response header + If-None-Match (304) + range handling for free.
    private IActionResult ServeImage(string id, string slot)
    {
        if (string.IsNullOrWhiteSpace(id)) return NotFound();
        string path = RawgCache.FilePath(options.RawgCacheDir, id, slot);

        // The {id} is UNTRUSTED (the route is [AllowAnonymous]). ASP.NET routing already blocks a slashed
        // traversal (single-segment match + decoded '..' collapse), but we must not depend on URL
        // normalization for filesystem safety on an anonymous endpoint behind a (varying) reverse proxy:
        // require the resolved path to stay under the cache root. A reject is a NotFound (indistinguishable
        // from a genuine miss — no info leak).
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(options.RawgCacheDir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return NotFound();

        if (!System.IO.File.Exists(full)) return NotFound();

        // ETag from the file's size+mtime — cheap, stable until the worker re-writes the bytes. A strong tag
        // (isWeak:false) is correct since the bytes are byte-stable between writes.
        var info = new FileInfo(full);
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        return PhysicalFile(full, "image/jpeg", lastModified: info.LastWriteTimeUtc, entityTag: etag);
    }

    // The absolute origin the cover/hero serving URLs are built from: the configured public base (reverse
    // proxy) when set, else request-derived ({scheme}://{host}) so it resolves per-host for the multi-host SPA.
    private string BaseUrl() =>
        !string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? options.PublicBaseUrl
            : $"{Request.Scheme}://{Request.Host}";

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
