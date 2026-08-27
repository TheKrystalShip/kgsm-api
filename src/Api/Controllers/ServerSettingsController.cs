using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Scheduling;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Per-server high-level settings — <c>GET /servers/{id}/settings</c> (Viewer),
/// <c>PATCH /servers/{id}/settings</c> (Operator) and the maintenance-window preview (Operator). A typed
/// façade over kgsm config, watchdog desired-state and the scheduler leaf's reading of the windows.
/// </summary>
/// <remarks>
/// Reads degrade gracefully (null) when a backing authority is absent or down; writes 503 when the
/// authority a field needs is unavailable. Every config write stamps actor+origin so the engine's own
/// event carries the provenance and the audit row is written from that echo — never here.
/// </remarks>
[ApiController]
[Route("api/v1/servers/{id}/settings")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class ServerSettingsController(
    ServerAggregator aggregator,
    LeafRegistry registry,
    ILogger<ServerSettingsController> logger) : ControllerBase
{
    /// <summary>The kgsm config key the whole window list is packed into.</summary>
    private const string MaintenanceWindowsKey = "maintenance_windows";

    /// <summary>How many fires the preview returns when the caller names no count.</summary>
    private const int DefaultPreviewCount = 5;

    /// <summary>The most fires one preview will compute. An editor shows the next few.</summary>
    private const int MaxPreviewCount = 20;

    [HttpGet]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        Instance? instance = instances.GetInstanceInfo(id);
        if (instance is null)
            return NotFound();

        bool? autostart = await ReadAutostartAsync(id, ct).ConfigureAwait(false);
        SchedulerInstanceStatus? schedStatus = await ReadSchedulerStatusAsync(id, ct).ConfigureAwait(false);

        return Ok(Compose(id, instance, autostart, schedStatus));
    }

    [HttpPatch]
    [Authorize(Policy = AuthPolicy.Operator)]
    public async Task<IActionResult> Patch(string id, [FromBody] ServerSettingsPatch? body, CancellationToken ct)
    {
        if (body is null)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "a settings body is required");

        if (!TryResolveOrigin(body.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (body.AutoUpdate is null && body.Autostart is null
            && body.CpuPriority is null && body.MemoryCapMb is null
            && body.MaintenanceWindows is null && body.Timezone is null
            && body.BackupRetention is null
            && body.CrashRestart is null && body.CrashMaxRestarts is null)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "no recognized settings fields in body");

        string? normalizedPriority = null;
        if (body.CpuPriority is { } rawPriority)
        {
            normalizedPriority = rawPriority.Trim().ToLowerInvariant();
            if (normalizedPriority is not ("low" or "normal" or "high"))
                return Error(StatusCodes.Status400BadRequest, "bad_request",
                    "cpuPriority must be one of: low, normal, high");
        }

        if (body.MemoryCapMb is { } memCap && memCap < 0)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "memoryCapMb must be >= 0 (0 = uncapped)");

        // The timezone is checked here rather than left to the clock: ScheduleClock falls back to this
        // host's local zone for a name it does not recognize, so an unchecked typo would silently move
        // every appointment on the instance to a zone nobody chose.
        if (body.Timezone is { Length: > 0 } tz && !IsValidTimezone(tz))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                $"timezone '{tz}' is not a recognized IANA timezone");

        if (body.BackupRetention is { } retentionCheck && retentionCheck is < 1 or > 100)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "backupRetention must be between 1 and 100");

        if (body.CrashMaxRestarts is { } cmr && cmr is < 1 or > 10)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "crashMaxRestarts must be between 1 and 10");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        Instance? current = instances.GetInstanceInfo(id);
        if (current is null)
            return NotFound();

        // The windows are read, refused or packed BEFORE any key is written, so a list carrying one bad
        // expression leaves the instance exactly as it was rather than half-applied.
        string? packedWindows = null;
        if (body.MaintenanceWindows is { } candidates)
        {
            if (!TryPackWindows(candidates, current.Runtime == InstanceRuntime.Container,
                    out packedWindows, out string? windowError))
                return Error(StatusCodes.Status400BadRequest, "bad_request", windowError!);
        }

        string? actor = AuditPrincipal.ActorString(User);
        var applied = new List<string>(4);

        if (body.AutoUpdate is { } autoUpdate)
        {
            KgsmResult result = instances.SetInstanceConfigValue(
                id, "auto_update", autoUpdate ? "true" : "false", actor, origin);
            if (!result.IsSuccess)
                return Error(StatusCodes.Status400BadRequest, "bad_request",
                    string.IsNullOrWhiteSpace(result.Stderr)
                        ? $"the engine refused 'auto_update' (exit {result.ExitCode})"
                        : result.Stderr.Trim());
            applied.Add("autoUpdate");
        }

        if (body.Autostart is { } autostart)
        {
            // The watchdog client is always registered (lazy, configured-or-default socket); provisioning is
            // the registry's flag, not the client's presence — gate on it (the NetworkAggregator/CommandRunner
            // pattern) so an unprovisioned host honestly 503s instead of dialing a dead socket.
            if (!registry.IsProvisioned(ProvisionableLeaf.Watchdog)
                || HttpContext.RequestServices.GetService(typeof(IWatchdogClient)) is not IWatchdogClient watchdog)
                return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                    "watchdog is not provisioned on this host — cannot change autostart");

            try
            {
                WatchdogActionResult result = autostart
                    ? await watchdog.EnableAsync(id, ct).ConfigureAwait(false)
                    : await watchdog.DisableAsync(id, ct).ConfigureAwait(false);

                if (!result.Ok)
                    return Error(StatusCodes.Status400BadRequest, "bad_request",
                        result.Message ?? $"watchdog refused autostart change for '{id}'");

                applied.Add("autostart");
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                logger.LogDebug(ex, "watchdog unreachable setting autostart for '{Id}'", id);
                return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                    "watchdog is unreachable — cannot change autostart");
            }
        }

        if (normalizedPriority is { } priority)
        {
            KgsmResult result = instances.SetInstanceConfigValue(
                id, "cpu_priority", priority, actor, origin);
            if (!result.IsSuccess)
                return Error(StatusCodes.Status400BadRequest, "bad_request",
                    string.IsNullOrWhiteSpace(result.Stderr)
                        ? $"the engine refused 'cpu_priority' (exit {result.ExitCode})"
                        : result.Stderr.Trim());

            // Live-apply is best-effort: the config is already persisted (takes effect next spawn regardless),
            // so an unreachable/absent watchdog must NOT fail the whole request — log at Debug and move on.
            if (HttpContext.RequestServices.GetService(typeof(IWatchdogClient)) is IWatchdogClient watchdog)
            {
                try
                {
                    await watchdog.SetCpuPriorityAsync(id, priority, ct).ConfigureAwait(false);
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    logger.LogDebug(ex, "watchdog unreachable live-applying cpu priority for '{Id}' — persisted only", id);
                }
            }

            applied.Add("cpuPriority");
        }

        // memory.max takes effect at the next spawn, so there is nothing to live-apply.
        if (body.MemoryCapMb is { } memoryCapMb)
        {
            KgsmResult result = instances.SetInstanceConfigValue(
                id, "memory_cap_mb", memoryCapMb.ToString(), actor, origin);
            if (!result.IsSuccess)
                return Error(StatusCodes.Status400BadRequest, "bad_request",
                    string.IsNullOrWhiteSpace(result.Stderr)
                        ? $"the engine refused 'memory_cap_mb' (exit {result.ExitCode})"
                        : result.Stderr.Trim());

            applied.Add("memoryCapMb");
        }

        // The whole window list is one key, replaced wholesale. The scheduler leaf re-reads kgsm config as
        // its source of truth, so persisting the key is the whole apply — nothing is pushed at the daemon.
        if (packedWindows is not null && !TryApplyConfig(
                instances, id, MaintenanceWindowsKey, packedWindows, actor, origin,
                applied, "maintenanceWindows", out IActionResult? mwErr))
            return mwErr!;

        if (body.Timezone is { } tzPatch && !TryApplyConfig(
                instances, id, "timezone", tzPatch.Trim(), actor, origin,
                applied, "timezone", out IActionResult? tzErr))
            return tzErr!;

        if (body.BackupRetention is { } retention && !TryApplyConfig(
                instances, id, "backup_retention", retention.ToString(), actor, origin,
                applied, "backupRetention", out IActionResult? brErr))
            return brErr!;

        if (body.CrashRestart is { } crashRestart && !TryApplyConfig(
                instances, id, "crash_restart", crashRestart ? "true" : "false", actor, origin,
                applied, "crashRestart", out IActionResult? crErr))
            return crErr!;
        if (body.CrashMaxRestarts is { } crashMax && !TryApplyConfig(
                instances, id, "crash_max_restarts", crashMax.ToString(), actor, origin,
                applied, "crashMaxRestarts", out IActionResult? cmrErr))
            return cmrErr!;

        // Re-read all fields for the authoritative post-write settings.
        Instance? fresh = instances.GetInstanceInfo(id);
        bool? freshAutostart = await ReadAutostartAsync(id, ct).ConfigureAwait(false);
        SchedulerInstanceStatus? freshSchedStatus = await ReadSchedulerStatusAsync(id, ct).ConfigureAwait(false);

        ServerSettings settings = fresh is not null
            ? Compose(id, fresh, freshAutostart, freshSchedStatus)
            : new ServerSettings(id, body.AutoUpdate ?? false, freshAutostart, null, null, [], null, null, null, null);

        return Ok(new ServerSettingsApplied(applied, settings));
    }

    /// <summary>
    /// <c>POST /servers/{id}/settings/maintenance/preview</c> → when a candidate window would fire, before
    /// anybody saves it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pure.</b> Nothing is written, nothing is pushed at the scheduler, and the instance is read only
    /// for the timezone the caller did not supply. Operator all the same: it is the editor's companion, and
    /// the editor is the writer.
    /// </para>
    /// <para>
    /// <b>An expression that cannot be read is an answer, not a failure.</b> The result carries
    /// <c>valid:false</c> with the parse error naming the offending text and no fires, which is exactly
    /// what an editor renders where the next fire would have gone. A missing expression is a 400 — that is
    /// a malformed request rather than a badly written window.
    /// </para>
    /// </remarks>
    [HttpPost("maintenance/preview")]
    [Authorize(Policy = AuthPolicy.Operator)]
    public async Task<IActionResult> PreviewMaintenance(
        string id, [FromBody] MaintenancePreviewRequest? body, CancellationToken ct)
    {
        if (body?.Expression is not { Length: > 0 } expression || string.IsNullOrWhiteSpace(expression))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "an expression is required, e.g. 'weekly.sun@04:00/backup,restart'");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        Instance? instance = instances.GetInstanceInfo(id);
        if (instance is null)
            return NotFound();

        // A zone the clock does not recognize resolves to this host's local one, which would silently
        // preview a schedule nobody asked for — so an unrecognized name is refused instead.
        string? requested = string.IsNullOrWhiteSpace(body.Timezone) ? instance.Timezone : body.Timezone.Trim();
        if (requested is { Length: > 0 } && !IsValidTimezone(requested))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                $"timezone '{requested}' is not a recognized IANA timezone");

        TimeZoneInfo zone = ScheduleClock.ResolveTimezone(requested);
        int count = Math.Clamp(body.Count ?? DefaultPreviewCount, 1, MaxPreviewCount);

        MaintenanceWindow window = MaintenanceWindowParser.ParseWindow(expression);
        IReadOnlyList<DateTimeOffset> fires =
        [
            .. ScheduleClock.NextFires(window, zone, DateTime.UtcNow, count)
                .Select(f => new DateTimeOffset(DateTime.SpecifyKind(f, DateTimeKind.Utc)))
        ];

        return Ok(new MaintenancePreviewResult(
            Id: window.Id,
            Expression: window.ToExpression(),
            Kind: MaintenanceWindows.KindToken(window.Kind),
            Tasks: MaintenanceWindows.TaskTokens(window),
            Valid: window.IsValid,
            Error: window.Error,
            Timezone: zone.Id,
            Fires: fires));
    }

    // The settings body, composed from the three authorities. Kept in one place so the GET and the
    // post-write read of the PATCH cannot drift into describing the same instance differently.
    private static ServerSettings Compose(
        string id, Instance instance, bool? autostart, SchedulerInstanceStatus? leaf) =>
        new(id,
            instance.AutoUpdate,
            autostart,
            instance.CpuPriority,
            instance.MemoryCapMb,
            MaintenanceWindows.Project(instance.MaintenanceWindows, leaf),
            instance.Timezone,
            instance.BackupRetention,
            instance.CrashRestart,
            instance.CrashMaxRestarts);

    /// <summary>
    /// Reads a submitted window list into the one packed value kgsm stores, or says why it will not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The parser is the validator.</b> Its error names the offending text, which is the whole reason
    /// it is worth carrying through to the caller verbatim rather than restating it as a vocabulary list.
    /// </para>
    /// <para>
    /// Two windows sharing a schedule are one appointment written twice: the id is the schedule, so the
    /// second would be indistinguishable from the first for postpone, skip and every announcement — the
    /// answer is to merge their task sets, and saying so is more use than picking one.
    /// </para>
    /// </remarks>
    private static bool TryPackWindows(
        IReadOnlyList<string> candidates, bool isContainer, out string? packed, out string? error)
    {
        packed = null;
        error = null;

        var windows = new List<MaintenanceWindow>(candidates.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                error = "a maintenance window cannot be blank; send an empty list to clear them all";
                return false;
            }

            MaintenanceWindow window = MaintenanceWindowParser.ParseWindow(candidate);
            if (MaintenanceWindows.Refusal(window, isContainer) is { } refusal)
            {
                error = refusal;
                return false;
            }

            if (!seen.Add(window.Id))
            {
                error = $"two windows share the schedule '{window.Id}'; merge their task sets into one window";
                return false;
            }

            windows.Add(window);
        }

        packed = MaintenanceWindowParser.Format(windows);
        return true;
    }

    // Query the watchdog's boot-autostart set. Returns null when the watchdog is absent or unreachable —
    // honest unknown, never a fabricated false (a missing entry and a down daemon look the same).
    private async Task<bool?> ReadAutostartAsync(string id, CancellationToken ct)
    {
        // Unprovisioned watchdog → honest null (unknown), never a fabricated false. Gate on the registry
        // (the client is always registered) before touching it, so we don't dial a dead default socket.
        if (!registry.IsProvisioned(ProvisionableLeaf.Watchdog)
            || HttpContext.RequestServices.GetService(typeof(IWatchdogClient)) is not IWatchdogClient watchdog)
            return null;

        try
        {
            IReadOnlyList<string> enabled = await watchdog.GetEnabledNamesAsync(ct).ConfigureAwait(false);
            return enabled.Contains(id, StringComparer.Ordinal);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            logger.LogDebug(ex, "watchdog unreachable reading autostart for '{Id}' — returning null", id);
            return null;
        }
    }

    // Persist one kgsm config key (echo-path audit — the write stamps actor+origin, kgsm emits the config
    // event, the consumer writes the row; no direct audit here). Adds to `applied` on success; on an
    // engine refusal sets `error` to a 400 and returns false so the caller short-circuits (no partial apply
    // past the failing key).
    private bool TryApplyConfig(
        IInstanceService instances, string id, string key, string value, string? actor, string origin,
        List<string> applied, string appliedName, out IActionResult? error)
    {
        KgsmResult result = instances.SetInstanceConfigValue(id, key, value, actor, origin);
        if (!result.IsSuccess)
        {
            error = Error(StatusCodes.Status400BadRequest, "bad_request",
                string.IsNullOrWhiteSpace(result.Stderr)
                    ? $"the engine refused '{key}' (exit {result.ExitCode})"
                    : result.Stderr.Trim());
            return false;
        }
        applied.Add(appliedName);
        error = null;
        return true;
    }

    // This instance's scheduler-computed state (each window's next fire and last run) comes ONLY from the
    // scheduler leaf's status socket. Null when the scheduler is not provisioned (client unregistered) or
    // unreachable, or when the leaf reports no row for this instance — honest unknown, never fabricated.
    // GetStatusAsync never throws (returns null on failure).
    private async Task<SchedulerInstanceStatus?> ReadSchedulerStatusAsync(string id, CancellationToken ct)
    {
        if (HttpContext.RequestServices.GetService(typeof(SchedulerClient)) is not SchedulerClient scheduler)
            return null;

        SchedulerStatusResponse? status = await scheduler.GetStatusAsync(ct).ConfigureAwait(false);
        return status?.Instances?
            .FirstOrDefault(i => string.Equals(i.Name, id, StringComparison.Ordinal));
    }

    private static bool IsValidTimezone(string value)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(value.Trim()); return true; }
        catch { return false; }
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
}
