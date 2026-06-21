using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Console scrollback — <c>GET /api/v1/servers/{id}/console?tail=N</c> (#8), the REST half of the split
/// console feature: a finite, oldest-first tail of a native instance's stdout for the SPA's console panel to
/// <strong>hydrate</strong> on open/reconnect, after which it follows live lines on the WS
/// <c>servers/{id}/console</c> topic (the patch-only, no-snapshot-on-subscribe rule — REST hydrates, WS
/// follows). Viewer-gated, a read surface consistent with <c>/audit</c> and <c>/alerts</c>.
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

    /// <summary>The frozen scrollback shape: <c>{ "lines": [ "...", "..." ] }</c>.</summary>
    private sealed record ConsoleScrollback(IReadOnlyList<string> Lines);
}
