using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Console scrollback + input — the split console feature (#8). <b>Read</b> (viewer): <c>GET
/// /api/v1/servers/{id}/console?tail=N</c>, a finite, oldest-first tail of a native instance's stdout for
/// the SPA's console panel to <strong>hydrate</strong> on open/reconnect, after which it follows live
/// lines on the WS <c>servers/{id}/console</c> topic (the patch-only, no-snapshot-on-subscribe rule — REST
/// hydrates, WS follows). <b>Write</b> (operator): <c>POST /api/v1/servers/{id}/console</c> delivers an
/// arbitrary console command to the running native server's input; the effect, if any, streams back on the
/// same WS topic. The read is viewer-gated (consistent with <c>/audit</c> and <c>/alerts</c>); the write is
/// operator-gated (at least as privileged as a lifecycle command).
/// </summary>
/// <remarks>
/// <para><b>Degrade gracefully — never a 500.</b> The durable record is the watchdog's LogFile, read via
/// kgsm-lib's <see cref="IWatchdogClient.GetConsoleTailAsync"/>. That call already degrades a 404 (unknown /
/// non-native / no-console instance) to an EMPTY list rather than throwing. This controller handles the two
/// remaining degrade cases the same honest way — <c>{ lines: [] }</c>, not an error:
/// the watchdog being <b>absent</b> (not provisioned → no <see cref="IWatchdogClient"/> resolved) and the
/// watchdog being <b>down</b> (a transport throw). An unknown id is therefore indistinguishable from an
/// empty/absent console here, by design — the contract is "best-effort scrollback or nothing", not a 404
/// gate. Console output is NEVER fabricated.</para>
/// </remarks>
[ApiController]
[Route("api/v1/servers/{id}/console")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class ServerConsoleController(ILogger<ServerConsoleController> logger) : ControllerBase
{
    private const int DefaultTail = 200;
    private const int MaxTail = 5000; // the watchdog clamps 0..5000; clamp here too so a wild ?tail= is honest

    /// <summary>
    /// The trailing <paramref name="tail"/> console lines, oldest-first: <c>200 { "lines": ["...", ...] }</c>.
    /// Defaults to 200, clamped to <c>0..5000</c>. Watchdog absent or down → <c>{ "lines": [] }</c> (degrade
    /// gracefully, never a 500); an unknown / non-native / no-console instance also reads as <c>{ "lines": [] }</c>.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(string id, [FromQuery] int? tail, CancellationToken ct)
    {
        int lines = Math.Clamp(tail ?? DefaultTail, 0, MaxTail);

        // Resolved optionally — the watchdog client is registered only when provisioned (Startup). Absent =>
        // no console source on this host => honest empty, exactly like a leaf-down degrade (never a 500).
        if (HttpContext.RequestServices.GetService(typeof(IWatchdogClient)) is not IWatchdogClient watchdog)
            return Ok(new ConsoleScrollback(Array.Empty<string>()));

        try
        {
            IReadOnlyList<string> tailLines = await watchdog.GetConsoleTailAsync(id, lines, ct).ConfigureAwait(false);
            return Ok(new ConsoleScrollback(tailLines));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the client went away — let the framework abort, don't mask it as an empty read
        }
        catch (Exception ex)
        {
            // The watchdog is provisioned but unreachable (transport throw). Degrade to an empty scrollback —
            // the live WS follow + the LogFile remain the source of truth; the SPA just opens with no history.
            logger.LogDebug(ex, "console tail for '{Instance}' failed (watchdog down?) — returning empty scrollback", id);
            return Ok(new ConsoleScrollback(Array.Empty<string>()));
        }
    }

    private const int MaxInput = 1000; // kgsm's __sanitize_input_command caps at 1000 — reject early + honestly

    /// <summary>
    /// Send an arbitrary console command to a running <b>native</b> instance — the write half of the split
    /// console feature. Body: <c>{ "input": "...", "origin"?: "ui|assistant|discord|api" }</c>. The command
    /// is delivered to the server's console input (kgsm <c>instances input</c> → the FIFO the watchdog wired
    /// to the process's stdin); its effect, if any, streams back on the live <c>servers/{id}/console</c> WS
    /// topic. <b>Fire-and-forget:</b> a <c>202</c> means the command was DELIVERED to the input channel, not
    /// that the game accepted it. The path stamps actor+origin onto <c>SendInput</c>, so the resulting kgsm
    /// <c>instance_input_sent</c> event — and the <c>console.input</c> audit row written from it — records
    /// who/through-what (echo-path audit, no direct write, no double-write).
    /// <list type="bullet">
    /// <item><c>202</c> — accepted: the command was delivered to the console input.</item>
    /// <item><c>400</c> — missing/blank/over-long <c>input</c>, or a bad <c>origin</c>.</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>409</c> — the instance is a container (console input is native-only), or the engine could
    /// not deliver the command (the server is not running / the input channel is unavailable).</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// </list>
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicy.Operator)] // a write — operator and up (architecture.html §3·e control set)
    public IActionResult Post(string id, [FromBody] ConsoleInputRequest? body)
    {
        string? input = body?.Input;
        if (string.IsNullOrWhiteSpace(input))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "input is required");
        if (input.Length > MaxInput)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                $"command too long (max {MaxInput} characters)");

        // Caller-declared driving surface, validated against the closed client set; absent => "api".
        if (!TryResolveOrigin(body?.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        // Resolved per-request (transient, registered only when the engine is provisioned); degrade to a
        // 503 rather than throw a missing dependency when kgsm isn't configured on this host (Install pattern).
        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        // Honest 404 on an unknown id; native-only gate — the console FOLLOW path is native-only too (the
        // watchdog owns a native process's stdout/stdin; Docker owns a container's), so send and see stay
        // symmetric. A container console is out of scope this round.
        Instance? instance = instances.GetInstanceInfo(id);
        if (instance is null)
            return NotFound();
        if (instance.Runtime != InstanceRuntime.Native)
            return Error(StatusCodes.Status409Conflict, "conflict",
                "console input is only supported on native instances (containers are managed by Docker)");

        // actor = the bearer identity (discord:<username>) or null → kgsm's own OS-user fallback. STAMP
        // actor+origin so the kgsm instance_input_sent event carries provenance; the audit row is written
        // from that echo (KgsmAuditConsumer.FromInputSentEvent), never here — no double-write.
        string? actor = AuditPrincipal.ActorString(User);
        KgsmResult result = instances.SendInput(id, input, actor, origin);
        if (!result.IsSuccess)
        {
            // The dominant failure is "server not running" (no FIFO → kgsm's _send_input reports "No active
            // server found"); a sanitizer rejection lands here too. Surface kgsm's own message honestly
            // rather than guess — never a fabricated success.
            string detail = string.IsNullOrWhiteSpace(result.Stderr)
                ? "the command could not be delivered (is the server running?)"
                : result.Stderr.Trim();
            return Error(StatusCodes.Status409Conflict, "conflict", detail);
        }

        logger.LogInformation("console input delivered to {ServerId} (actor={Actor}, origin={Origin})",
            id, actor ?? "(none)", origin);
        return StatusCode(StatusCodes.Status202Accepted, new ConsoleInputAccepted(true));
    }

    // Resolve the caller-declared origin (ui|assistant|discord|api, default api; "system" reserved/rejected)
    // — identical to the command path's helper, kept independent of the actor.
    private static bool TryResolveOrigin(string? raw, out string origin)
    {
        origin = raw?.Trim().ToLowerInvariant() is { Length: > 0 } o ? o : AuditOrigin.Api;
        return AuditOrigin.IsCallerDeclarable(origin);
    }

    // The frozen { error: { code, message } } envelope (architecture.html §6), via the MVC formatters.
    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));

    /// <summary>The frozen scrollback shape: <c>{ "lines": [ "...", "..." ] }</c>.</summary>
    private sealed record ConsoleScrollback(IReadOnlyList<string> Lines);

    /// <summary>The 202 body: <c>{ "accepted": true }</c> — delivered to the console input (effect is async, on the WS).</summary>
    private sealed record ConsoleInputAccepted(bool Accepted);
}

/// <summary>The console-input request body: <c>{ "input": "...", "origin"?: "ui|assistant|discord|api" }</c>.</summary>
public sealed record ConsoleInputRequest(string? Input, string? Origin = null);
