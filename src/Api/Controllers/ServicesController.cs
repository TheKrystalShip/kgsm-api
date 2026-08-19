using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Speech;

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
    /// <c>GET /hosts/{id}/services/speech/status</c> → what the host's speech engine is doing: whether the
    /// models are loaded, which runtime each half actually opened on, the voice it is speaking in, when it
    /// unloads, what is attached to it, and what it has heard and said since it started.
    /// <b>404 when this host has no speech leaf</b> (no socket bound); <b>503</b> when it has one and the
    /// daemon would not answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>An inactive unit is answered without connecting.</b> The leaf is socket-activated and idle-exits
    /// to give back the ~1.6GB its models cost, and connecting to its socket is precisely what starts it —
    /// so a resting daemon is reported as resting rather than woken up to be asked how it is. What can still
    /// be known without asking is: the unit's state, the model files on disk, and the configured voice.
    /// </para>
    /// <para>
    /// Nothing is recomputed. The runtime each half loaded on, the tallies and the unload time are the
    /// leaf's own measurements; the model files are this API's own <c>stat</c> of the paths the leaf's
    /// config descriptor resolves to. Neither is derived from the other, and a lane that fell back to the
    /// processor says so rather than repeating the setting that asked for a card.
    /// </para>
    /// </remarks>
    [HttpGet("speech/status")]
    public async Task<IActionResult> GetSpeechStatus(
        string id,
        [FromServices] SystemdReader systemd,
        [FromServices] LeafConfigService config,
        CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (HttpContext.RequestServices.GetService(typeof(SpeechLeafClient)) is not SpeechLeafClient speech
            || !speech.IsProvisioned)
            return NotFound();

        // The models the leaf's configuration points at, measured here rather than asked for: readable
        // whether or not the daemon is running, and the question somebody has when nothing will speak.
        (IReadOnlyList<SpeechModelFile> models, string configuredVoice, int? idleMinutes) =
            await ConfiguredAsync(config, ct).ConfigureAwait(false);

        IReadOnlyDictionary<string, UnitState> units =
            await systemd.ReadAsync([SpeechUnit], ct).ConfigureAwait(false);
        string state = units.TryGetValue(SpeechUnit, out UnitState? unit) ? unit.State : "unknown";

        // Anything but a running daemon is left alone. `unknown` counts as resting here on purpose: if we
        // could not read the unit we do not know whether asking would start one, and starting it is the
        // outcome that cannot be undone.
        if (!string.Equals(state, "active", StringComparison.Ordinal))
            return Ok(new SpeechEngine(
                Resting: true, State: state, StartedAt: null, Loaded: false, LoadedAt: null, LoadMs: null,
                IdleMinutes: idleMinutes, LastAskedAt: null, UnloadsAt: null, Surfaces: [],
                Voice: new SpeechVoice(string.Empty, configuredVoice, false, null),
                Hearing: null, Speaking: null, Models: models));

        SpeechStatus? status = await speech.GetStatusAsync(ct).ConfigureAwait(false);
        if (status is null)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the speech engine didn't answer for its own status");

        return Ok(new SpeechEngine(
            Resting: false,
            State: state,
            StartedAt: status.StartedAt,
            Loaded: status.Loaded,
            LoadedAt: status.LoadedAt,
            LoadMs: status.LoadMilliseconds,
            IdleMinutes: status.IdleMinutes,
            LastAskedAt: status.LastAskedAt,
            UnloadsAt: status.UnloadsAt,
            Surfaces: status.Surfaces,
            Voice: new SpeechVoice(
                status.SpeakingVoice,
                // The daemon reads the same configuration this API does, so its word for the configured
                // voice is the one to carry — falling back to ours only if it reported none.
                status.ConfiguredVoice.Length > 0 ? status.ConfiguredVoice : configuredVoice,
                status.VoiceOverridden,
                status.InstalledVoices),
            Hearing: Lane(status.Hearing),
            Speaking: Lane(status.Speaking),
            Models: models));
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

    /// <summary>
    /// <c>GET /hosts/{id}/services/reactor/status</c> → what the reactor is doing right now, relayed
    /// verbatim: the gate's tuning, the rules that are live and the authority each runs under, what it has
    /// ingested and judged since it started, and the evaluations waiting out their settle windows.
    /// <b>404 when this host runs no reactor</b>; <b>503</b> when it runs one that would not answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verbatim like <c>monitor/stats</c> and <c>bot/status</c>: the reactor owns this contract and already
    /// resolves the parts a reader would otherwise get wrong — a rule named in two mode lists is reported
    /// at the safest of them, and a rule with no window of its own is reported at the host-wide one. Both
    /// are arithmetic this API would have to duplicate to reshape the payload, and duplicating it is how
    /// the panel and the leaf come to disagree about what a rule is permitted to do.
    /// </para>
    /// <para>
    /// ⚠ The counters are since the reactor's own process started, not since the beginning. They are named
    /// that way on the wire and the panel must keep saying so — a zero after a deploy is a restart, not a
    /// quiet host.
    /// </para>
    /// </remarks>
    [HttpGet("reactor/status")]
    public async Task<IActionResult> GetReactorStatus(string id, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (HttpContext.RequestServices.GetService(typeof(ReactorClient)) is not ReactorClient reactor
            || !reactor.IsProvisioned)
            return NotFound();

        string? json = await reactor.GetStatusJsonAsync(ct).ConfigureAwait(false);
        if (json is null)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the reactor didn't answer for its own status");

        return Content(json, "application/json");
    }

    /// <summary>
    /// <c>GET /hosts/{id}/services/reactor/decisions?days=N</c> → what the reactor made of what it saw,
    /// relayed verbatim: what each rule concluded and how often, the busiest rolling hour of fired
    /// decisions, how far apart a rule's repeats on one subject were, the rules that decided nothing,
    /// and the decisions themselves with the journal position each was derived from.
    /// <b>404 when this host runs no reactor</b>; <b>503</b> when it runs one that would not answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the review the reactor's plan gates propose and act mode behind — nothing moves until a
    /// week of decisions has been read against what a person would actually have done. It existed only
    /// as text on the host until now, which made the gate something declared rather than performed.
    /// </para>
    /// <para>
    /// ⚠ <b>The window is bounded by the leaf, not here.</b> It clamps <c>days</c> to its own ledger
    /// retention, which is the only place that figure is known. This API passes the request through and
    /// reads the window back off the answer.
    /// </para>
    /// </remarks>
    [HttpGet("reactor/decisions")]
    public async Task<IActionResult> GetReactorDecisions(
        string id, [FromQuery] int days, CancellationToken ct)
    {
        if (!string.Equals(id, options.HostId, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (HttpContext.RequestServices.GetService(typeof(ReactorClient)) is not ReactorClient reactor
            || !reactor.IsProvisioned)
            return NotFound();

        // Zero or absent leaves the window to the leaf, which defaults to the week the review gate is
        // stated over. A negative is a caller error rather than a window: forwarded, it would read back
        // as the leaf's minimum with nobody told the request was nonsense.
        if (days < 0)
            return Error(StatusCodes.Status400BadRequest, "invalid_range",
                "days must not be negative");

        string? json = await reactor.GetDecisionsJsonAsync(days, ct).ConfigureAwait(false);
        if (json is null)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the reactor didn't answer for its decision review");

        return Content(json, "application/json");
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));

    /// <summary>The speech leaf's unit, as the catalog names it.</summary>
    private static string SpeechUnit => LeafCatalog.Find("speech")?.Unit ?? "kgsm-speech.service";

    /// <summary>One of the leaf's halves, relayed field for field.</summary>
    private static SpeechLaneReport Lane(SpeechLane lane) => new(
        lane.Available, lane.Detail, lane.Model, lane.Runtime, lane.Busy, lane.Waiting,
        lane.Done, lane.Rejected, lane.Failed, lane.AudioSeconds, lane.Characters,
        lane.LastMilliseconds, lane.MeanMilliseconds, lane.P95Milliseconds, lane.RealtimeFactor,
        lane.LastAt, lane.LastOutcome);

    /// <summary>
    /// What the speech leaf is configured with, and what is on disk where it points.
    /// </summary>
    /// <remarks>
    /// Read through the leaf's own config surface rather than from a path written down here: the
    /// effective value already accounts for the override this API may itself have written, and holding a
    /// second copy of the default is how the two come to disagree. A leaf that cannot be read yields no
    /// models rather than invented ones.
    /// </remarks>
    private static async Task<(IReadOnlyList<SpeechModelFile> Models, string Voice, int? IdleMinutes)>
        ConfiguredAsync(LeafConfigService config, CancellationToken ct)
    {
        LeafConfig? surface = null;
        try
        {
            surface = await config.GetConfigAsync("speech", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The engine's own state is the point of this endpoint; its configuration is context. A
            // config surface that cannot be read costs the model card, not the page.
        }

        string? Effective(string key) => surface?.Fields
            .FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal))?.Effective;

        var models = new List<SpeechModelFile>(2);
        foreach ((string kind, string key) in new[]
                 { ("recognition", "recognitionModel"), ("synthesis", "synthesisModel") })
        {
            string? path = Effective(key);
            if (string.IsNullOrWhiteSpace(path)) continue;

            long? bytes = null;
            bool present = false;
            try
            {
                var file = new FileInfo(path);
                present = file.Exists;
                if (present) bytes = file.Length;
            }
            catch (Exception)
            {
                // An unreadable path is a file of unknown size, not an absent one — reported as such
                // rather than as "no model", which would send somebody to re-download 813MB.
            }

            models.Add(new SpeechModelFile(kind, Path.GetFileName(path), path, bytes, present));
        }

        return (models, Effective("voice") ?? string.Empty, ParseMinutes(Effective("idleMinutes")));
    }

    private static int? ParseMinutes(string? value) =>
        int.TryParse(value, out int minutes) ? minutes : null;
}
