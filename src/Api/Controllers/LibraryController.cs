using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

using TheKrystalShip.KGSM.Auth;

using TheKrystalShip.KGSM.Auth.Sessions;

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
    LibraryAggregator aggregator,
    ApiOptions options,
    LibraryHydrationWorker refresher,
    BlueprintCache blueprints) : ControllerBase
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

    // ---- the blueprint file editor (GET read / PUT save / DELETE revert) --------------------------
    //
    // Reads are OPERATOR, writes are ADMIN. The class-level [Authorize(Viewer)] is AND-combined by
    // ASP.NET, so the action-level attributes tighten it (the same idiom as POST /library/refresh above).
    // A viewer therefore cannot even read a blueprint file: unlike the catalog metadata, the file is the
    // engine's operational definition of how a game server is launched.
    //
    // Every path resolution, jail check, engine validation and byte write lives in kgsm-lib's
    // IBlueprintFiles behind IBlueprintFileService — this controller only maps outcomes to status codes
    // and shapes DTOs. No audit row is written here: kgsm emits blueprint.created/.updated/.removed for
    // these writes, so the trail arrives as an event echo like every other engine action. What the
    // controller must do instead is thread actor+origin down into the emit, which is what keeps the
    // echoed row attributed to the real admin rather than the service account.

    /// <summary>
    /// <c>GET /library/scaffold</c> — the engine's blueprint skeleton (<c>blueprint.tp</c>), for seeding a
    /// new blueprint's editor buffer. <strong>Operator+</strong>: an operator cannot <c>POST</c> a blueprint
    /// but does reach the create page, where the buffer loads read-only alongside the assistant hand-off.
    /// <list type="bullet">
    /// <item><c>200</c> — the template (<see cref="BlueprintScaffoldDto"/>).</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned, or reported no templates directory / an
    ///   unreadable template. There is no API-composed fallback skeleton.</item>
    /// </list>
    /// </summary>
    [HttpGet("scaffold")]
    [Authorize(Policy = AuthPolicy.Operator)]
    public IActionResult Scaffold()
    {
        if (Resolve() is not { } files) return EngineUnavailable();

        BlueprintScaffoldResult r = files.Scaffold();
        return r.Status == BlueprintFileOp.Ok
            ? Ok(new BlueprintScaffoldDto(r.Content!))
            : EngineUnavailable();
    }

    /// <summary>
    /// <c>POST /library</c> — create a new blueprint from editor text. <strong>Admin only</strong>, matching
    /// the save path: authoring a blueprint defines how a game server is installed and launched. The file
    /// lands in kgsm's USER blueprints directory and the ENGINE validates it before anything is committed;
    /// the audit trail arrives as the echo of the <c>blueprint.created</c> kgsm emits.
    /// <list type="bullet">
    /// <item><c>200</c> — created (<see cref="SaveBlueprintResultDto"/>).</item>
    /// <item><c>400</c> — missing <c>name</c>/<c>content</c> or bad origin (<c>bad_request</c>), or the
    ///   engine rejected the content (<c>blueprint_invalid</c>, with its own <c>errors[]</c> in the details).</item>
    /// <item><c>404</c> — the name is not a safe slug.</item>
    /// <item><c>409</c> — <c>name_taken</c> (a blueprint of that name already resolves — edit it via
    ///   <c>PUT /library/{id}/file</c> instead), or <c>file_too_large</c>.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned, or returned no validation verdict at all.</item>
    /// </list>
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicy.Admin)]
    public IActionResult CreateBlueprint([FromBody] CreateBlueprintRequest? body)
    {
        if (body?.Name is not { Length: > 0 } name)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "name is required");

        if (body.Content is not string content)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "content is required");

        if (!TryResolveOrigin(body.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (Resolve() is not { } files) return EngineUnavailable();

        BlueprintSaveResult r = files.Create(
            name, content, options.BlueprintMaxEditBytes,
            actor: AuditPrincipal.ActorString(User), origin: origin);

        switch (r.Status)
        {
            case BlueprintFileOp.Ok:
                return Ok(new SaveBlueprintResultDto(
                    r.Etag!, r.SizeBytes, r.Mtime, BlueprintTierName.User, r.OverridesSystem, r.CreatedOverride));
            case BlueprintFileOp.NameTaken:
                return Error(StatusCodes.Status409Conflict, "name_taken",
                    "a blueprint with this name already exists — edit it instead, or pick another name");
            case BlueprintFileOp.Invalid:
                return StatusCode(StatusCodes.Status400BadRequest, new ErrorEnvelope(new ErrorBody(
                    "blueprint_invalid", "the engine rejected this blueprint", ErrorDetails(r.Errors))));
            case BlueprintFileOp.TooLarge:
                return Error(StatusCodes.Status409Conflict, "file_too_large",
                    "the content exceeds the edit-size limit");
            case BlueprintFileOp.Unavailable:
                return EngineUnavailable();
            default:
                return NotFound(); // OutOfJail (unsafe name) / IoError
        }
    }

    /// <summary>
    /// <c>GET /library/{id}/file</c> — a blueprint's raw <c>.bp.yaml</c> text plus an sha256 etag, read
    /// from whichever blueprints directory the engine resolves it in. <strong>Operator+</strong>.
    /// <list type="bullet">
    /// <item><c>200</c> — the file (<see cref="BlueprintFileDto"/>).</item>
    /// <item><c>404</c> — no blueprint of that name, or the name/resolved path escapes the jail.</item>
    /// <item><c>409</c> — the file is binary (<c>file_binary</c>), too large (<c>file_too_large</c>), or
    ///   not a regular file (<c>conflict</c>) — can't open it honestly.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned (or reported no blueprints directory).</item>
    /// </list>
    /// </summary>
    [HttpGet("{id}/file")]
    [Authorize(Policy = AuthPolicy.Operator)]
    public IActionResult ReadFile(string id)
    {
        if (Resolve() is not { } files) return EngineUnavailable();

        BlueprintReadResult r = files.Read(id, options.BlueprintMaxEditBytes);
        return r.Status switch
        {
            BlueprintFileOp.Ok => Ok(new BlueprintFileDto(
                r.Name, r.Content!, "utf-8", r.SizeBytes, r.Mtime, r.Etag!,
                r.Tier, r.OverridesSystem,
                // canRevert == overridesSystem: reverting is only meaningful when a shipped original
                // exists to fall back to. Surfaced so the client never derives the rule itself; the
                // DELETE below refuses independently regardless.
                CanRevert: r.OverridesSystem,
                ReadOnly: !CallerCanWrite(),
                Runtime: RuntimeOf(id),
                HostId: options.HostId)),
            BlueprintFileOp.Binary => Error(StatusCodes.Status409Conflict, "file_binary",
                "this blueprint file is binary and can't be opened in the editor"),
            BlueprintFileOp.TooLarge => Error(StatusCodes.Status409Conflict, "file_too_large",
                "this blueprint file is too large to open in the editor"),
            BlueprintFileOp.NotAFile => Error(StatusCodes.Status409Conflict, "conflict",
                "the resolved blueprint path is not a regular file"),
            BlueprintFileOp.Unavailable => EngineUnavailable(),
            _ => NotFound(), // NotFound / OutOfJail (folded in — never reveal a host path) / IoError
        };
    }

    /// <summary>
    /// <c>PUT /library/{id}/file</c> — save the blueprint's file text. <strong>Admin only</strong>.
    /// The write always lands in kgsm's USER blueprints directory, so saving an edit to a shipped
    /// blueprint creates an override that shadows it permanently (<c>createdOverride</c> reports that
    /// transition); the shipped directory is the engine deploy's rsync target and is structurally
    /// unreachable from here. The ENGINE validates the content before anything is committed.
    /// <list type="bullet">
    /// <item><c>200</c> — saved (<see cref="SaveBlueprintResultDto"/>).</item>
    /// <item><c>400</c> — missing <c>content</c> / bad origin (<c>bad_request</c>), or the engine rejected
    ///   the content (<c>blueprint_invalid</c>, with the engine's own <c>errors[]</c> in the details).</item>
    /// <item><c>404</c> — the name escapes the jail.</item>
    /// <item><c>409</c> — the content exceeds the edit-size limit (<c>file_too_large</c>).</item>
    /// <item><c>412</c> — the <c>etag</c> no longer matches (the file changed on disk).</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned, or returned no validation verdict at all.</item>
    /// </list>
    /// </summary>
    [HttpPut("{id}/file")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public IActionResult SaveFile(string id, [FromBody] SaveBlueprintRequest? body)
    {
        if (body?.Content is not string content)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "content is required");

        if (!TryResolveOrigin(body.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (Resolve() is not { } files) return EngineUnavailable();

        BlueprintSaveResult r = files.Save(
            id, content, body.Etag, options.BlueprintMaxEditBytes,
            actor: AuditPrincipal.ActorString(User), origin: origin);

        switch (r.Status)
        {
            case BlueprintFileOp.Ok:
                // The blueprint cache is busted by the kgsm event this write emits, not from here — that
                // way an assistant-originated write invalidates it too (Services/Library/BlueprintCache).
                return Ok(new SaveBlueprintResultDto(
                    r.Etag!, r.SizeBytes, r.Mtime, BlueprintTierName.User, r.OverridesSystem, r.CreatedOverride));
            case BlueprintFileOp.Invalid:
                // The engine's own validator messages, verbatim — the API neither rewords nor re-implements
                // them (the engine is the schema authority). Nothing was written. They ride in the frozen
                // envelope's `details` slot rather than a bespoke body, so there stays exactly one error
                // shape on this API.
                return StatusCode(StatusCodes.Status400BadRequest, new ErrorEnvelope(new ErrorBody(
                    "blueprint_invalid", "the engine rejected this blueprint", ErrorDetails(r.Errors))));
            case BlueprintFileOp.EtagMismatch:
                return Error(StatusCodes.Status412PreconditionFailed, "precondition_failed",
                    "this blueprint changed on disk since it was loaded — reload and reapply your changes");
            case BlueprintFileOp.TooLarge:
                return Error(StatusCodes.Status409Conflict, "file_too_large",
                    "the new content exceeds the edit-size limit");
            case BlueprintFileOp.Unavailable:
                return EngineUnavailable();
            default:
                return NotFound(); // OutOfJail (unsafe name) / NotFound / IoError
        }
    }

    /// <summary>
    /// <c>DELETE /library/{id}/file</c> — revert to the shipped blueprint by removing the user-dir
    /// override. <strong>Admin only</strong>. Refused with <c>409 no_original</c> when this blueprint has
    /// no shipped counterpart: deleting then would destroy the only copy rather than restore anything.
    /// The SPA also hides the button, but this API refuses independently.
    /// <list type="bullet">
    /// <item><c>200</c> — reverted (<see cref="RevertBlueprintResultDto"/>).</item>
    /// <item><c>404</c> — no user-dir file to remove (already the shipped one), or the name escapes the jail.</item>
    /// <item><c>409</c> — <c>no_original</c>: nothing to revert TO.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned (or reported no blueprints directory).</item>
    /// </list>
    /// </summary>
    [HttpDelete("{id}/file")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public IActionResult RevertFile(string id, [FromQuery] string? origin)
    {
        if (!TryResolveOrigin(origin, out string resolvedOrigin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (Resolve() is not { } files) return EngineUnavailable();

        BlueprintRevertResult r = files.Revert(id, actor: AuditPrincipal.ActorString(User), origin: resolvedOrigin);
        return r.Status switch
        {
            BlueprintFileOp.Ok => Ok(new RevertBlueprintResultDto(r.RevertedTo, r.Etag, r.SizeBytes, r.Mtime)),
            BlueprintFileOp.NoOriginal => Error(StatusCodes.Status409Conflict, "no_original",
                "this blueprint has no shipped original to revert to — removing it would delete the only copy"),
            BlueprintFileOp.Unavailable => EngineUnavailable(),
            _ => NotFound(), // NotFound (no user override) / OutOfJail / IoError
        };
    }

    // The blueprint file service is registered in lockstep with the kgsm-lib services (both gated on
    // KgsmProvisioned — Startup.cs), so it is resolved lazily rather than constructor-injected: an
    // unprovisioned engine must degrade to a 503, not a DI construction failure. Same pattern as
    // ServerFilesController's IInstanceFileService lookup.
    private IBlueprintFileService? Resolve() =>
        HttpContext.RequestServices.GetService(typeof(IBlueprintFileService)) as IBlueprintFileService;

    private IActionResult EngineUnavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
            "the kgsm engine is not provisioned on this host");

    /// <summary>Whether this caller may save — admin, matching the PUT/DELETE policies. An operator gets
    /// the file with <c>readOnly:true</c>: the editor opens, the buttons don't. Read off the verified
    /// token's tier claim, never a request field.</summary>
    private bool CallerCanWrite() =>
        User.Identity is ClaimsIdentity ci && SessionClaims.ReadTier(ci) >= KgsmTier.Admin;

    /// <summary>The blueprint's runtime as the ENGINE reports it in the cached catalog, or <c>null</c> when
    /// it isn't there (brand-new, or malformed enough that the engine won't enumerate it — precisely a file
    /// worth opening to repair). Never parsed out of the file content here: which runtimes exist and how
    /// they are declared is the engine's knowledge, not this API's.</summary>
    private string? RuntimeOf(string id) =>
        blueprints.GetAll().TryGetValue(id, out Blueprint? bp)
            ? bp.BlueprintType == BlueprintType.Container ? "container" : "native"
            : null;

    private static bool TryResolveOrigin(string? raw, out string origin)
    {
        origin = raw?.Trim().ToLowerInvariant() is { Length: > 0 } o ? o : AuditOrigin.Api;
        return AuditOrigin.IsCallerDeclarable(origin);
    }

    /// <summary>The engine's validation errors as the error envelope's <c>details</c> —
    /// <c>{ "errors": [...] }</c>, a named field rather than a bare array so the shape can grow without a
    /// break. Serialized with the API's own camelCase conventions.</summary>
    private static JsonElement ErrorDetails(IReadOnlyList<string> errors)
    {
        var options = new JsonSerializerOptions();
        ApiJson.Configure(options);
        return JsonSerializer.SerializeToElement(new BlueprintInvalidDetails(errors), options);
    }

    private sealed record BlueprintInvalidDetails(IReadOnlyList<string> Errors);

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
