using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The host's KGSM leaf-service control center — <c>GET /hosts/{id}/services</c> (the host "Services" tab).
/// Returns one row per configured leaf (watchdog, monitor, assistant, firewall, api, bot) joining its live
/// <b>systemd</b> liveness (<see cref="SystemdReader"/>) with the api's deep-health probe where it has one
/// (<see cref="LeafHealthMonitor"/>). Host-OS introspection, sourced directly (like the host logs / file
/// browser), NOT via kgsm-lib.
/// <para>
/// Gated at <b>operator</b> — the same host-internals sensitivity as the host logs (unit names, pids,
/// memory, enablement). The host deep-dive page is already admin-gated on the frontend, so reaching here
/// clears the read gate. <strong>Read-only in this slice</strong> — start/stop/restart controls are a later
/// increment (they need a polkit grant scoped to <c>kgsm-*.service</c>, an admin gate, and audit rows).
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/hosts/{id}/services")]
[Authorize(Policy = AuthPolicy.Operator)]
public sealed class ServicesController(
    ServicesAggregator services,
    LeafCommandStore commands,
    ApiOptions options) : ControllerBase
{
    /// <summary><c>GET /hosts/{id}/services</c> → <c>{ data:[LeafService] }</c> in catalog order. Per-host
    /// api: the only valid <c>{id}</c> is this host (unknown ⇒ 404, mirroring the other host surfaces).</summary>
    [HttpGet]
    public async Task<ActionResult<ServicesSnapshot>> GetServices(string id, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        return await services.SnapshotAsync(ct);
    }

    /// <summary>
    /// <c>GET /hosts/{id}/services/{leaf}/commands</c> → the leaf's own catalog of the commands it answers
    /// to, straight from the manifest it ships. <b>404 when it ships none</b> — most leaves take no
    /// commands at all, and that is the honest answer rather than an empty list, which would read as "this
    /// one takes commands and currently has none".
    /// </summary>
    /// <remarks>
    /// Read-only reference material about a leaf, so it sits with the rest of the read-only Services
    /// surface at operator rather than behind the admin config gate. Nothing here is interpreted: the
    /// manifest's own words for what each command does, and for what the leaf checks before running one,
    /// are passed through — this API cannot verify a gate it does not implement, so it does not restate it.
    /// </remarks>
    [HttpGet("{leaf}/commands")]
    public ActionResult<LeafCommandManifest> GetCommands(string id, string leaf)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        LeafCommandManifest? manifest = commands.For(leaf);
        return manifest is null ? NotFound() : manifest;
    }

    /// <summary>
    /// <c>GET /hosts/{id}/services/scheduler/schedules</c> → the scheduler's whole board, relayed verbatim.
    /// <b>404 when this host runs no scheduler</b> (the socket isn't configured, so the client isn't even
    /// registered) — a different fact from a scheduler that wouldn't answer, which is a <b>503</b>.
    /// </summary>
    /// <remarks>
    /// Nothing is recomputed here. The next-fire times are the leaf's arithmetic over the leaf's clock and
    /// its configured timezone; re-deriving them in this process is how the panel and the scheduler end up
    /// disagreeing about when a restart lands. A null field is the leaf's own honest gap.
    /// </remarks>
    [HttpGet("scheduler/schedules")]
    public async Task<IActionResult> GetSchedules(string id, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (HttpContext.RequestServices.GetService(typeof(SchedulerClient)) is not SchedulerClient scheduler)
            return NotFound();

        SchedulerStatusResponse? status = await scheduler.GetStatusAsync(ct).ConfigureAwait(false);
        if (status is null)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the scheduler didn't answer for its schedule board");

        return Ok(new SchedulerBoard(status.Instances ?? []));
    }

    /// <summary>
    /// <c>GET /hosts/{id}/services/watchdog/supervision</c> → what the watchdog intends for each native
    /// instance, what the kernel measures, and its own readiness to supervise at all.
    /// <b>404 when the watchdog isn't provisioned</b>; <b>503</b> when it is and wouldn't answer.
    /// </summary>
    /// <remarks>
    /// Reached through kgsm-lib's <see cref="IWatchdogClient"/> — the C#↔engine chokepoint — never by
    /// opening the control socket here. Two calls are joined: the supervision table and the persisted
    /// boot-autostart set, because "enabled" lives only in the second and is orthogonal to everything in
    /// the first (an instance can be running and not enabled, or enabled and stopped). If the enabled set
    /// can't be read the rows still stand; nothing about them is invented from the table alone.
    /// </remarks>
    [HttpGet("watchdog/supervision")]
    public async Task<IActionResult> GetSupervision(string id, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (HttpContext.RequestServices.GetService(typeof(IWatchdogClient)) is not IWatchdogClient watchdog)
            return NotFound();

        IReadOnlyList<WatchdogInstanceState> table;
        try
        {
            table = await watchdog.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the watchdog didn't answer for its supervision table");
        }

        // Readiness and the enabled set are both ADDITIVE: the table is the answer, and either of these
        // failing costs one column rather than the response. A null `ready` reads "couldn't ask", which is
        // what the panel renders — never an optimistic true.
        bool? ready = null;
        string? detail = null;
        try
        {
            WatchdogReadyState? state = await watchdog.GetReadyAsync(ct).ConfigureAwait(false);
            if (state is not null) { ready = state.Ready; detail = state.Detail; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* additive — leave unknown */ }

        HashSet<string> enabled = new(StringComparer.Ordinal);
        try
        {
            foreach (string name in await watchdog.GetEnabledNamesAsync(ct).ConfigureAwait(false))
                enabled.Add(name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* additive — leave empty */ }

        List<SupervisedInstance> rows = [.. table.Select(s => new SupervisedInstance(
            s.Name,
            s.Desired,
            s.Phase,
            s.Populated,
            enabled.Contains(s.Name),
            s.Pid,
            s.Restarts,
            string.IsNullOrWhiteSpace(s.Reason) ? null : s.Reason))];

        return Ok(new WatchdogSupervision(ready, string.IsNullOrWhiteSpace(detail) ? null : detail, rows));
    }

    /// <summary>
    /// <c>GET /hosts/{id}/services/monitor/stats</c> → the monitor's own self-report, relayed verbatim:
    /// its sample cadence and coverage, and what its history store measurably holds against what it was
    /// configured to hold. <b>503 when the monitor didn't answer</b>; the leaf being disconnected is the
    /// same relay-level null, so both read as "couldn't be asked" rather than "nothing recorded".
    /// </summary>
    /// <remarks>
    /// Verbatim like <c>metrics/history</c>: the monitor owns this contract, and re-serializing it here
    /// would introduce a second shape to keep in step for no gain. The API holds no opinion about
    /// whether the measured retention span matches the configured one — it carries both across and the
    /// panel compares them.
    /// </remarks>
    [HttpGet("monitor/stats")]
    public async Task<IActionResult> GetMonitorStats(string id, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (HttpContext.RequestServices.GetService(typeof(IMonitorHistoryClient)) is not IMonitorHistoryClient monitor)
            return NotFound();

        string? json = await monitor.GetStatsJsonAsync(ct).ConfigureAwait(false);
        if (json is null)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the monitor didn't answer for its own statistics");

        return Content(json, "application/json");
    }

    /// <summary>
    /// <c>GET /hosts/{id}/services/bot/status</c> → the Discord bot's gateway state, the guild it
    /// actually resolved, its channel map and its announcement switches, relayed verbatim.
    /// <b>404 when this host configures no bot status socket</b>; <b>503</b> when it does and the bot
    /// wouldn't answer.
    /// </summary>
    /// <remarks>
    /// Worth more than it looks: a bot whose guild failed to populate is active in systemd, connected at
    /// the gateway, and unable to post a single message. This is the only surface on which those come
    /// apart, so the resolved guild travels across untouched rather than being reduced to a boolean here.
    /// </remarks>
    [HttpGet("bot/status")]
    public async Task<IActionResult> GetBotStatus(string id, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (HttpContext.RequestServices.GetService(typeof(BotClient)) is not BotClient bot)
            return NotFound();

        string? json = await bot.GetStatusJsonAsync(ct).ConfigureAwait(false);
        if (json is null)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the bot didn't answer for its status");

        return Content(json, "application/json");
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
