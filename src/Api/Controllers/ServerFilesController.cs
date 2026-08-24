using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Files;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The file browser &amp; editor (Tier 3 #12) — <c>GET /servers/{id}/files</c> (list one directory, lazy),
/// <c>GET /servers/{id}/files/content</c> (read a text file) and <c>PUT /servers/{id}/files/content</c>
/// (save an existing text file). Everything is scoped to the instance's working directory; paths ride a
/// <c>?path=</c> query (relative, server-canonicalized — never a route segment, an encoding/injection
/// minefield). <strong>BOTH read and write are operator-gated, not viewer</strong> (file contents routinely
/// hold secrets — rcon passwords, tokens, webhook URLs — so even listing/reading is operator+).
/// <para>
/// The controller does its own engine-provisioned/unknown-id pre-check (<c>IInstanceService.GetInstanceInfo</c>
/// via the chokepoint); the actual jailed list/read/write is kgsm-lib's <c>IInstanceFiles</c> — the single
/// filesystem authority shared with the assistant — reached
/// through the kgsm-api-owned <see cref="IInstanceFileService"/> wrapper (status mapping + presentation
/// hints only, no I/O of its own). A save is a mutation with no kgsm event, so the API writes the
/// <c>file.write</c> audit row itself (the <c>auth.*</c> direct-write pattern, no double-write) — recording
/// <c>path</c>/<c>sizeBytes</c>/<c>sha256</c> but NEVER the content.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/servers/{id}/files")]
[Authorize(Policy = AuthPolicy.Operator)] // read AND write are operator+ (contents hold secrets)
public sealed class ServerFilesController(
    ServerAggregator aggregator,
    ApiJournal journal,
    ApiOptions options) : ControllerBase
{
    /// <summary>
    /// List one directory (lazy — one directory per request, no recursion). <c>?path=</c> is relative to
    /// the working dir; empty/absent = the root.
    /// <list type="bullet">
    /// <item><c>200</c> — the listing (<c>{ path, truncated, entries[] }</c>).</item>
    /// <item><c>400</c> — the path is not a directory.</item>
    /// <item><c>404</c> — unknown server id, or the path is missing / escapes the jail.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned (or the working dir is unavailable).</item>
    /// </list>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(string id, [FromQuery] string? path, CancellationToken ct)
    {
        Jail jail = await TryResolveJail(id, ct).ConfigureAwait(false);
        if (jail.Error is not null) return jail.Error;

        ListResult result = jail.Files!.ListDirectory(id, path, options.FilesMaxEntries);
        return result.Status switch
        {
            FileOp.Ok => Ok(new DirListingDto(result.Path, result.Truncated, result.Entries.Select(ToDto).ToArray())),
            FileOp.NotADirectory => Error(StatusCodes.Status400BadRequest, "bad_request", "not a directory"),
            FileOp.Unavailable => Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the instance working directory is unavailable"),
            // OutOfJail folds into 404 — never reveal that a path resolved outside the host jail.
            _ => NotFound(),
        };
    }

    /// <summary>
    /// Read a text file's raw content + an sha256 etag (for optimistic concurrency on save).
    /// <list type="bullet">
    /// <item><c>200</c> — the content (<c>{ path, encoding, content, sizeBytes, mtime, etag }</c>).</item>
    /// <item><c>404</c> — unknown server id, or the path is missing / not a regular file / escapes the jail.</item>
    /// <item><c>409</c> — the file is binary (<c>file_binary</c>) or too large (<c>file_too_large</c>) — can't open honestly.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned (or the working dir is unavailable).</item>
    /// </list>
    /// </summary>
    [HttpGet("content")]
    public async Task<IActionResult> Read(string id, [FromQuery] string? path, CancellationToken ct)
    {
        Jail jail = await TryResolveJail(id, ct).ConfigureAwait(false);
        if (jail.Error is not null) return jail.Error;

        ReadResult result = jail.Files!.ReadFile(id, path, options.FilesMaxEditBytes);
        return result.Status switch
        {
            FileOp.Ok => Ok(new FileContentDto(result.Path, "utf-8", result.Content!, result.SizeBytes, result.Mtime, result.Etag!)),
            FileOp.Binary => Error(StatusCodes.Status409Conflict, "file_binary", "this file is binary and can't be opened in the editor"),
            FileOp.TooLarge => Error(StatusCodes.Status409Conflict, "file_too_large", "this file is too large to open in the editor"),
            FileOp.Unavailable => Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the instance working directory is unavailable"),
            _ => NotFound(), // NotFound / NotAFile / OutOfJail
        };
    }

    /// <summary>
    /// Save (overwrite) an EXISTING text file — atomic write, preserving the file mode. The body is
    /// <c>{ content, etag?, origin? }</c>; an <c>etag</c> mismatch (the file changed on disk since read)
    /// returns <c>412</c>. On success, writes a <c>file.write</c> audit row (path/size/sha256 only — never
    /// the content).
    /// <list type="bullet">
    /// <item><c>200</c> — saved (<c>{ path, sizeBytes, mtime, etag }</c>).</item>
    /// <item><c>400</c> — missing <c>content</c>, or a bad origin.</item>
    /// <item><c>404</c> — unknown server id, or the file does not exist (v1 = save-existing only) / escapes the jail.</item>
    /// <item><c>409</c> — the target is binary/too-large/non-regular (refuse to clobber).</item>
    /// <item><c>412</c> — the <c>etag</c> no longer matches (the file changed on disk).</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned (or the working dir is unavailable).</item>
    /// </list>
    /// </summary>
    [HttpPut("content")]
    public async Task<IActionResult> Save(string id, [FromQuery] string? path, [FromBody] SaveFileRequest? body, CancellationToken ct)
    {
        if (body?.Content is not string content)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "content is required");

        if (!TryResolveOrigin(body.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        Jail jail = await TryResolveJail(id, ct).ConfigureAwait(false);
        if (jail.Error is not null) return jail.Error;

        WriteResult result = jail.Files!.SaveFile(id, path, content, body.Etag, options.FilesMaxEditBytes);

        switch (result.Status)
        {
            case FileOp.Ok:
                await WriteAuditAsync(id, result, origin, ct).ConfigureAwait(false);
                return Ok(new SaveFileResultDto(result.Path, result.SizeBytes, result.Mtime, result.Etag!));
            case FileOp.EtagMismatch:
                return Error(StatusCodes.Status412PreconditionFailed, "precondition_failed",
                    "the file changed on disk since it was loaded — reload and reapply your changes");
            case FileOp.Binary:
                return Error(StatusCodes.Status409Conflict, "file_binary", "this file is binary and can't be edited");
            case FileOp.TooLarge:
                return Error(StatusCodes.Status409Conflict, "file_too_large", "the new content exceeds the edit-size limit");
            case FileOp.NotAFile:
                return Error(StatusCodes.Status409Conflict, "conflict", "the target is not a regular file");
            case FileOp.Unavailable:
                return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                    "the instance working directory is unavailable");
            default:
                return NotFound(); // NotFound (no create in v1) / OutOfJail
        }
    }

    // ---- audit (direct write, no double-write — kgsm runs nothing here) --------------------------

    // The path, the size and the hash — never the content. An instance's configuration files hold
    // rcon passwords, tokens and webhook URLs, so the record identifies the write rather than
    // reproducing it.
    private Task WriteAuditAsync(string id, WriteResult result, string origin, CancellationToken ct) =>
        journal.FileWrittenAsync(
            id, result.Path, result.SizeBytes, result.Etag,
            AuditPrincipal.ActorString(User) ?? "", AuditMapping.NormalizeOrigin(origin), ct);

    // ---- shared instance/jail resolution (mirrors ServerConfigController) ------------------------

    private readonly record struct Jail(IInstanceFileService? Files, IActionResult? Error);

    /// <summary>Resolve the instance (and the file service, kgsm-lib-backed and gated the same way) or
    /// the matching error result: 503 if the engine is unprovisioned, 404 if the id is unknown, 503 if
    /// the engine reports no working dir. The actual jail root lookup + I/O now happens inside kgsm-lib's
    /// <c>IInstanceFiles</c> (by instance name, not a pre-resolved path) — this pre-check exists purely to
    /// preserve the existing degrade/404 behaviour before making that call.</summary>
    private async Task<Jail> TryResolveJail(string id, CancellationToken ct)
    {
        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return new Jail(null, Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host"));

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return new Jail(null, NotFound());

        Instance? instance = instances.GetInstanceInfo(id);
        if (instance is null)
            return new Jail(null, NotFound());

        if (string.IsNullOrWhiteSpace(instance.WorkingDir))
            return new Jail(null, Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the instance working directory is unavailable"));

        // Registered in lockstep with IInstanceService (both gated on KgsmProvisioned — Startup.cs), so
        // this is guaranteed non-null once the check above passed; resolved lazily (not constructor-
        // injected) for the same reason IInstanceService is: an unprovisioned engine must degrade to the
        // 503 above, not a DI construction failure.
        var files = (IInstanceFileService?)HttpContext.RequestServices.GetService(typeof(IInstanceFileService));
        return new Jail(files, null);
    }

    private async Task<bool> ExistsAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct).ConfigureAwait(false);
        return servers.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    }

    private static bool TryResolveOrigin(string? raw, out string origin)
    {
        origin = raw?.Trim().ToLowerInvariant() is { Length: > 0 } o ? o : AuditOrigin.Api;
        return AuditOrigin.IsCallerDeclarable(origin);
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));

    // ---- entry → wire DTO (adds the presentation-only `lang` hint for editable files) -----------

    private static FileEntryDto ToDto(FsEntry e) => new(
        e.Name, e.Kind, e.SizeBytes, e.Mtime, e.Editable,
        e.Editable == true && e.Kind == EntryKind.File ? LangHint(e.Name) : null,
        e.Reason);

    /// <summary>A small extension → highlight-language hint (presentation only; the FE may ignore it).
    /// Honest <c>null</c> for an unrecognized extension (the FE falls back to plain text).</summary>
    private static string? LangHint(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".cfg" => "cfg",
            ".conf" or ".ini" or ".properties" => "ini",
            ".json" => "json",
            ".yaml" or ".yml" => "yaml",
            ".xml" => "xml",
            ".toml" => "toml",
            ".sh" or ".bash" => "bash",
            ".lua" => "lua",
            ".txt" or ".log" or ".md" => "text",
            _ => null,
        };
    }
}
