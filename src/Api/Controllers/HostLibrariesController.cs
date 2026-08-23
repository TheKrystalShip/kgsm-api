using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// This host's <strong>placement roots</strong> — the named libraries instances live in.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Not the game catalog. <see cref="LibraryController"/> serves <c>/library</c>, the blueprints a server
/// can be installed <em>from</em>; this serves the disks a server is installed <em>onto</em>. They share a
/// word and nothing else.
/// </para>
/// <para>
/// Everything here goes through kgsm-lib's <see cref="ILibraryService"/> — the single C#↔engine chokepoint.
/// Nothing shells <c>kgsm libraries</c>, and no state about a library is kept here: the registry lives on
/// disk beside the engine and is read per request, because whether a root is mounted is a fact that can
/// change between two page loads.
/// </para>
/// <para>
/// Reads sit at viewer, alongside <c>GET /hosts</c> which already carries the same list. Writes are
/// admin: registering a disk shapes the host, like the other host-shaping writes.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{id}/libraries")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class HostLibrariesController(
    HostAggregator aggregator, ApiJournal journal, ApiOptions options,
    ILogger<HostLibrariesController> logger) : ControllerBase
{
    /// <summary>
    /// <c>GET .../libraries</c> — every registered root with its state, capacity and instance count.
    /// </summary>
    /// <remarks>
    /// The same list <c>GET /hosts</c> carries, joined to the monitor's mounts the same way, served on its
    /// own route so the management surface can refresh placement without re-reading the whole host
    /// aggregate. An engine that cannot answer is a <c>503</c>, never an empty list: a host with nothing
    /// registered is a different fact, and one this endpoint says by answering <c>[]</c>.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> List(string id, CancellationToken ct)
    {
        if (!IsThisHost(id)) return NotFound();

        Contracts.Host host = await aggregator.GetHostAsync(ct).ConfigureAwait(false);
        return host.Libraries is null
            ? Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine could not report this host's libraries")
            : Ok(host.Libraries);
    }

    /// <summary>
    /// <c>POST .../libraries</c> — register a root.
    /// </summary>
    /// <remarks>
    /// The engine creates the directory when it does not exist, writes the <c>.kgsm-library</c> marker, and
    /// <em>adopts</em> a root that already carries one — which is what makes a disk portable between hosts.
    /// Its refusal is passed through verbatim as a <c>400</c>; it names the reason, and rewording it would
    /// throw that away.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> Add(string id, [FromBody] AddLibraryRequest? body, CancellationToken ct)
    {
        if (!IsThisHost(id)) return NotFound();

        string? path = body?.Path?.Trim();
        if (string.IsNullOrEmpty(path))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "path is required");

        if (!TryResolveOrigin(body?.Origin, out string origin))
            return BadOrigin();

        string? name = string.IsNullOrWhiteSpace(body?.Name) ? null : body!.Name!.Trim();

        if (Engine() is not ILibraryService libraries) return NoEngine();

        string? actor = AuditPrincipal.ActorString(User);
        KgsmResult result = await Task.Run(() => libraries.Add(path, name, actor, origin), ct)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
            return await FailedAsync(
                "add", name ?? path, path, newName: null, result, actor, origin, ct).ConfigureAwait(false);

        // No audit row here — kgsm emits library_added, carrying the actor and origin stamped onto the
        // call above, and the journal tail turns that into the library.add row. A second write for the
        // same fact could not be deduplicated against the echo.
        logger.LogInformation("registered library at {Path} (name={Name}, actor={Actor})",
            path, name ?? "(from the directory)", actor ?? "(none)");

        return await CurrentAsync(name, path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>PATCH .../libraries/{name}</c> — rename a library.
    /// </summary>
    /// <remarks>
    /// Registry and marker only. Every instance records its library's <em>path</em>, so nothing moves and
    /// nothing has to be restarted. kgsm emits no event for a rename, which is why this is the one library
    /// mutation whose successful row this API writes itself.
    /// </remarks>
    [HttpPatch("{name}")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> Rename(
        string id, string name, [FromBody] RenameLibraryRequest? body, CancellationToken ct)
    {
        if (!IsThisHost(id)) return NotFound();

        string? newName = body?.Name?.Trim();
        if (string.IsNullOrEmpty(newName))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "name is required");

        if (!TryResolveOrigin(body?.Origin, out string origin))
            return BadOrigin();

        if (Engine() is not ILibraryService libraries) return NoEngine();

        string? actor = AuditPrincipal.ActorString(User);
        KgsmResult result = await Task.Run(() => libraries.Rename(name, newName, actor, origin), ct)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
            return await FailedAsync("rename", name, path: null, newName, result, actor, origin, ct)
                .ConfigureAwait(false);

        // The engine records nothing for a rename, so without this row the name in every earlier library
        // row would point at something a reader could no longer find.
        await journal.LibraryOutcomeAsync(
            ApiJournal.LibraryRenamedEvent, name, "rename", path: null, newName: newName,
            error: null, exitCode: null, actor, origin, ct).ConfigureAwait(false);

        return await CurrentAsync(newName, path: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>DELETE .../libraries/{name}</c> — deregister a root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No file inside it is touched, including the instances placed there — they stay on the disk and
    /// resolve to no library until it is registered again.
    /// </para>
    /// <para>
    /// <c>?drain=&lt;target&gt;</c> is how a disk is emptied before it is taken out: every instance in
    /// this library moves into the target, one at a time, and the library is deregistered once the last
    /// has landed. Every resident has to be stopped first — the engine lists the running ones and moves
    /// nothing rather than stopping servers on the caller's behalf.
    /// </para>
    /// <para>
    /// ⚠ <b>There is no force.</b> The engine refuses while instances still resolve to the library, naming
    /// them, and that refusal is served through as a <c>409</c> with its own words. Adding a
    /// pass-through would let the panel produce, in one click, the state the engine exists to prevent;
    /// draining is the sanctioned path, and it is the one offered here.
    /// </para>
    /// <para>
    /// ⚠ A drain is <b>minutes of copying per resident instance</b> and this request blocks for all of
    /// it — unlike a server move, which runs as a job. Nothing in the engine brackets a drain, so there
    /// is no per-instance progress to stream; a caller needs a client timeout that matches.
    /// </para>
    /// </remarks>
    [HttpDelete("{name}")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public async Task<IActionResult> Remove(
        string id, string name, [FromQuery] string? origin, [FromQuery] string? drain, CancellationToken ct)
    {
        if (!IsThisHost(id)) return NotFound();

        if (!TryResolveOrigin(origin, out string resolvedOrigin))
            return BadOrigin();

        string? drainTo = string.IsNullOrWhiteSpace(drain) ? null : drain.Trim();
        if (drainTo is not null && string.Equals(drainTo, name, StringComparison.Ordinal))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "a library cannot be drained into itself");

        if (Engine() is not ILibraryService libraries) return NoEngine();

        string? actor = AuditPrincipal.ActorString(User);
        KgsmResult result = await Task
            .Run(() => libraries.Remove(name, force: false, drainTo, actor, resolvedOrigin), ct)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
            return await FailedAsync(
                "remove", name, path: null, newName: null, result, actor, resolvedOrigin, ct,
                // A refusal is not a malformed request: the library exists and the caller may remove it,
                // but something still lives there. 409 is what lets the panel tell "you asked for the
                // wrong thing" from "you cannot do that yet".
                StatusCodes.Status409Conflict, "conflict").ConfigureAwait(false);

        // No audit row here — kgsm emits library_removed. See Add.
        logger.LogInformation("deregistered library {Name} (actor={Actor})", name, actor ?? "(none)");

        return await CurrentAsync(name: null, path: null, ct).ConfigureAwait(false);
    }

    // The registry as it now stands, so a client re-renders from a measurement rather than from what it
    // assumes the mutation did. When the request named one library, that entry rides alongside — a
    // freshly-registered root reports its own capacity, which is the number the install form needs next.
    private async Task<IActionResult> CurrentAsync(string? name, string? path, CancellationToken ct)
    {
        Contracts.Host host = await aggregator.GetHostAsync(ct).ConfigureAwait(false);
        IReadOnlyList<LibraryDto> current = host.Libraries ?? [];

        LibraryDto? subject = name is null
            ? null
            : current.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
        // A root registered under a name the engine derived (from the directory, or from an adopted
        // marker) is not findable by the name the caller sent, so fall back to the path it named.
        subject ??= path is null
            ? null
            : current.FirstOrDefault(l => string.Equals(l.Path, path, StringComparison.Ordinal));

        return Ok(new { libraries = current, library = subject });
    }

    // A mutation the engine did not perform. The row is written here because kgsm emits an event when a
    // library verb WORKS and nothing at all when it does not — the command.failed case exactly — so
    // there is no echo to ride and no double-write risk.
    private async Task<IActionResult> FailedAsync(
        string verb, string name, string? path, string? newName, KgsmResult result,
        string? actor, string origin, CancellationToken ct,
        int status = StatusCodes.Status400BadRequest, string code = "bad_request")
    {
        string detail = Detail(result, verb, name);

        await journal.LibraryOutcomeAsync(
            ApiJournal.LibraryFailedEvent, name, verb, path, newName,
            detail, result.ExitCode, actor, origin, ct).ConfigureAwait(false);

        return Error(status, code, detail);
    }

    // The engine's own sentence, verbatim — it names the instances blocking a removal, the running ones
    // blocking a drain, or why a path could not be registered, and no wording composed here could carry
    // that. A run that said nothing gets a bare statement of what did not happen rather than an invented
    // reason.
    private static string Detail(KgsmResult result, string verb, string name)
    {
        string stderr = Clean(result.Stderr);
        if (stderr.Length > 0) return stderr;
        string stdout = Clean(result.Stdout);
        return stdout.Length > 0 ? stdout : $"the engine refused to {verb} library '{name}'";
    }

    private static string Clean(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? ""
            : string.Join('\n', raw.Split('\n')
                .Select(line => line.TrimEnd())
                .Where(line => line.Length > 0));

    // Transient and registered only when the engine is provisioned, so it is resolved per request rather
    // than injected — a constructor parameter would turn an engine-less host into a 500 here.
    private ILibraryService? Engine() =>
        HttpContext.RequestServices.GetService(typeof(ILibraryService)) as ILibraryService;

    private ObjectResult NoEngine() =>
        Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
            "the kgsm engine is not provisioned on this host");

    private ObjectResult BadOrigin() =>
        Error(StatusCodes.Status400BadRequest, "bad_request",
            "unknown origin; expected one of: ui, assistant, discord, api");

    private static bool TryResolveOrigin(string? raw, out string origin)
    {
        origin = raw?.Trim().ToLowerInvariant() is { Length: > 0 } o ? o : AuditOrigin.Api;
        return AuditOrigin.IsCallerDeclarable(origin);
    }

    private bool IsThisHost(string id) =>
        string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase);

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
