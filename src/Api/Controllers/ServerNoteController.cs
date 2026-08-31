using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// A server's operator-authored note — <c>GET /servers/{id}/note</c> (Viewer),
/// <c>PUT</c> (Operator, write) and <c>DELETE</c> (Operator, clear). The note is free text an
/// operator leaves on a game server: mods to expect, rules, a heads-up before joining.
/// </summary>
/// <remarks>
/// <para><b>Engine-owned, not API-local.</b> The note lives in the kgsm instance's own
/// <c>.config.ini</c> (kgsm-lib's <c>InstanceNote</c> owns the encoding and the three keys), so it
/// travels with the instance, is readable by any surface that speaks kgsm-lib, and dies with the
/// instance on uninstall. This controller holds no state.</para>
/// <para><b>Echo-path audit — no double-write.</b> Each key write emits <c>config.changed</c>
/// carrying the actor+origin this stamps onto the call, so the audit row is the engine's echo. The two
/// attribution keys are filtered out of the audit surface (<c>AuditQueries</c>) so one save reads as
/// one line rather than three.</para>
/// <para><b>The note also rides the Server DTO</b> (list, detail and the <c>servers</c> stream), so a
/// successful write triggers an immediate cache refresh: the dashboard tile and every other open panel
/// converge without waiting for the 60-second reconcile.</para>
/// </remarks>
[ApiController]
[Route("api/v1/servers/{id}/note")]
[Authorize(Policy = AuthPolicy.Viewer)] // reads — viewer and up; the writes below require operator
public sealed class ServerNoteController(
    ServerAggregator aggregator,
    InstanceCache cache,
    ILogger<ServerNoteController> logger) : ControllerBase
{
    /// <summary>
    /// The instance's note (<c>{ serverId, note }</c>), with <c>note: null</c> when none is set —
    /// the honest "nothing written", never an empty-string placeholder.
    /// <list type="bullet">
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — the note, or null.</item>
    /// </list>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!TryEngine(out IInstanceService? instances, out IActionResult? unavailable))
            return unavailable!;

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        Instance? instance = instances!.GetInstanceInfo(id);
        if (instance is null)
            return NotFound();

        return Ok(new ServerNoteView(id, ServerAggregator.NoteOf(instance)));
    }

    /// <summary>
    /// Write the note. The body is capped at <see cref="InstanceNote.MaxLength"/> characters measured
    /// after sanitizing (newlines survive; other control characters are dropped) and is
    /// <strong>rejected, never truncated</strong>, when longer. An empty body is refused — clearing is
    /// <c>DELETE</c>, so an accidentally-submitted empty editor can't silently wipe a note.
    /// <list type="bullet">
    /// <item><c>400</c> — missing/empty body, over the cap, or a bad origin.</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>500</c> — the engine refused a key part-way; the response names which keys applied.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — <c>{ serverId, note }</c> re-read from the engine.</item>
    /// </list>
    /// </summary>
    [HttpPut]
    [Authorize(Policy = AuthPolicy.Operator)]
    public Task<IActionResult> Put(string id, [FromBody] ServerNoteWrite? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Body))
            return Task.FromResult<IActionResult>(Error(StatusCodes.Status400BadRequest, "bad_request",
                "a note body is required; use DELETE to clear the note"));

        // Measure what will actually be stored, not what was typed — trailing whitespace and stripped
        // control characters must not cost the operator characters against the cap.
        string sanitized = InstanceNote.Sanitize(body.Body);
        if (sanitized.Length > InstanceNote.MaxLength)
            return Task.FromResult<IActionResult>(Error(StatusCodes.Status400BadRequest, "bad_request",
                $"the note is {sanitized.Length} characters; the maximum is {InstanceNote.MaxLength}"));

        return WriteAsync(id, sanitized, body.Origin, ct);
    }

    /// <summary>
    /// Clear the note. The body is blanked while attribution records <em>who cleared it and when</em>,
    /// so removing a player-facing note is as traceable as writing one.
    /// <list type="bullet">
    /// <item><c>400</c> — bad origin.</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>500</c> — the engine refused a key part-way; the response names which keys applied.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — <c>{ serverId, note: null }</c>.</item>
    /// </list>
    /// </summary>
    /// <remarks>The driving surface rides the query string here (<c>?origin=ui</c>) as well as an
    /// optional body — a DELETE with a body is awkward for browser clients, and the deleteServer path
    /// already established the query-string form.</remarks>
    [HttpDelete]
    [Authorize(Policy = AuthPolicy.Operator)]
    public Task<IActionResult> Delete(
        string id, [FromQuery] string? origin, [FromBody] ServerNoteClear? body, CancellationToken ct)
        => WriteAsync(id, string.Empty, body?.Origin ?? origin, ct);

    // The one write path both PUT and DELETE take (a clear is just an empty body) — so the encoding,
    // the attribution stamp and the cache refresh can never diverge between them.
    private async Task<IActionResult> WriteAsync(string id, string body, string? rawOrigin, CancellationToken ct)
    {
        if (!TryResolveOrigin(rawOrigin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (!TryEngine(out IInstanceService? instances, out IActionResult? unavailable))
            return unavailable!;

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        // actor = the bearer identity (discord:<username>); it is both the audit principal and the
        // value stored as note_updated_by, so the card's byline and the audit row can't disagree.
        string? actor = AuditPrincipal.ActorString(User);

        InstanceNoteResult result = instances!.SetInstanceNote(id, body, actor, origin);
        if (!result.IsSuccess)
        {
            // kgsm writes one key per invocation, so this is genuinely partial — say so instead of
            // implying nothing changed. Body-last ordering means the note itself is the last thing to
            // land, so a failure here never leaves a new note credited to the wrong person.
            logger.LogWarning("note write for '{Id}' failed on key '{Key}' (exit {Exit}); applied: {Applied}",
                id, result.FailedKey, result.ExitCode, string.Join(", ", result.AppliedKeys));

            cache.TryRefresh();
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorEnvelope(new ErrorBody(
                "engine_refused",
                result.Error ?? $"the engine refused '{result.FailedKey}' (exit {result.ExitCode})",
                PartialWriteDetails(result))));
        }

        // The note rides the Server DTO and the `servers` stream; refresh so both converge now rather
        // than on the next 60s reconcile. Best-effort: a refresh already in flight returns false and
        // will pick the write up anyway.
        cache.TryRefresh();

        // Re-read for the authoritative post-write value (what the engine stored, not what we sent).
        Instance? fresh = instances.GetInstanceInfo(id);
        return Ok(new ServerNoteView(id, fresh is null ? null : ServerAggregator.NoteOf(fresh)));
    }

    private bool TryEngine(out IInstanceService? instances, out IActionResult? unavailable)
    {
        instances = HttpContext.RequestServices.GetService(typeof(IInstanceService)) as IInstanceService;
        unavailable = instances is null
            ? Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host")
            : null;
        return instances is not null;
    }

    // Whether the instance is in this host's roster (the honest 404 source, like ServersController).
    private async Task<bool> ExistsAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct).ConfigureAwait(false);
        return servers.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    }

    // Resolve the caller-declared driving surface (ui|assistant|discord|api, default api), the
    // ServersController convention; "system" (autonomous-only) and unknown values are rejected.
    private static bool TryResolveOrigin(string? raw, out string origin)
    {
        origin = raw?.Trim().ToLowerInvariant() is { Length: > 0 } o ? o : AuditOrigin.Api;
        return AuditOrigin.IsCallerDeclarable(origin);
    }

    /// <summary>The partial-write state as the error envelope's <c>details</c> —
    /// <c>{ "applied": [...], "failedKey": "note" }</c>, so a client can tell an untouched config
    /// from one carrying fresh attribution on the old body. Serialized with the API's conventions.</summary>
    private static JsonElement PartialWriteDetails(InstanceNoteResult result)
    {
        var options = new JsonSerializerOptions();
        ApiJson.Configure(options);
        return JsonSerializer.SerializeToElement(
            new NoteWriteFailureDetails(result.AppliedKeys, result.FailedKey), options);
    }

    private sealed record NoteWriteFailureDetails(IReadOnlyList<string> Applied, string? FailedKey);

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
