using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Per-server high-level settings — <c>GET /servers/{id}/settings</c> (Viewer) and
/// <c>PATCH /servers/{id}/settings</c> (Operator). Typed façade over kgsm primitives:
/// Phase 0 = <c>autoUpdate</c> (kgsm config), Phase 1 adds <c>autostart</c> (watchdog
/// desired-state). Reads degrade gracefully (null) when a backing authority is absent/down;
/// writes return 503 when the required authority is unavailable. Echo-path audit — no double-write.
/// </summary>
[ApiController]
[Route("api/v1/servers/{id}/settings")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class ServerSettingsController(
    ServerAggregator aggregator,
    LeafRegistry registry,
    ILogger<ServerSettingsController> logger) : ControllerBase
{
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

        return Ok(new ServerSettings(
            id, instance.AutoUpdate, autostart, instance.CpuPriority, instance.MemoryCapMb,
            instance.ScheduledRestart, instance.RestartTime, instance.RestartDay, instance.Timezone,
            schedStatus?.NextFireUtc,
            instance.AutoBackupOnRestart, instance.BackupRetention,
            schedStatus?.LastBackupUtc, schedStatus?.LastBackupOk,
            instance.CrashRestart, instance.CrashMaxRestarts));
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
            && body.ScheduledRestart is null && body.RestartTime is null
            && body.RestartDay is null && body.Timezone is null
            && body.AutoBackupOnRestart is null && body.BackupRetention is null
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

        // Phase 3 — validate the schedule fields up front, so a bad value 400s BEFORE any config write
        // (no partial apply). Non-null non-empty cadence/time/day/timezone are checked; an empty string is
        // an allowed "clear" for day/timezone (falls through to the config write verbatim).
        if (body.ScheduledRestart is { } cadence
            && cadence.Trim().ToLowerInvariant() is not ("off" or "daily" or "weekly" or "6h"))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "scheduledRestart must be one of: off, daily, weekly, 6h");

        if (body.RestartTime is { Length: > 0 } rtime && !IsValidHhMm(rtime))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "restartTime must be HH:MM (24h)");

        if (body.RestartDay is { Length: > 0 } rday
            && rday.Trim().ToLowerInvariant() is not ("sun" or "mon" or "tue" or "wed" or "thu" or "fri" or "sat"))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "restartDay must be one of: sun, mon, tue, wed, thu, fri, sat");

        if (body.Timezone is { Length: > 0 } tz && !IsValidTimezone(tz))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                $"timezone '{tz}' is not a recognized IANA timezone");

        // Phase 4 — auto-backup value validation (pure, no instance needed): retention is a bounded count.
        if (body.BackupRetention is { } retentionCheck && retentionCheck is < 1 or > 100)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "backupRetention must be between 1 and 100");

        // Phase 6 — crash-restart policy value validation (bounded, matches the UI select: 1/2/3/5/10).
        if (body.CrashMaxRestarts is { } cmr && cmr is < 1 or > 10)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "crashMaxRestarts must be between 1 and 10");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        // Phase 4 — auto-backup-on-restart needs a scheduled cadence to hook onto (backup runs in the restart
        // window). Guard here, before any write: effective cadence = the patch value if it's also being set,
        // else the instance's current one. Reject enabling auto-backup with no/off schedule.
        if (body.AutoBackupOnRestart == true)
        {
            string? effectiveCadence = body.ScheduledRestart is { } patchCadence
                ? patchCadence.Trim().ToLowerInvariant()
                : instances.GetInstanceInfo(id)?.ScheduledRestart;
            if (string.IsNullOrEmpty(effectiveCadence) || effectiveCadence == "off")
                return Error(StatusCodes.Status400BadRequest, "bad_request",
                    "autoBackupOnRestart requires scheduledRestart to be set (not 'off')");
        }

        string? actor = AuditPrincipal.ActorString(User);
        var applied = new List<string>(4);

        // Phase 0 — auto_update config key
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

        // Phase 1 — autostart via watchdog boot-enable set
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

        // Phase 2 — cpu_priority config key + best-effort live-apply via the watchdog (cpu.weight)
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

        // Phase 2 — memory_cap_mb config key (no live-apply; memory.max takes effect at next restart)
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

        // Phase 3 — schedule config keys (echo-path audit via the kgsm config event, no double-write). The
        // scheduler leaf re-reads kgsm config as its source of truth, so persisting the key is the whole
        // apply — the API never pushes to the scheduler. Cadence/day are lowercased; time/timezone verbatim.
        if (body.ScheduledRestart is { } sched && !TryApplyConfig(
                instances, id, "scheduled_restart", sched.Trim().ToLowerInvariant(), actor, origin,
                applied, "scheduledRestart", out IActionResult? schedErr))
            return schedErr!;
        if (body.RestartTime is { } rt && !TryApplyConfig(
                instances, id, "restart_time", rt.Trim(), actor, origin,
                applied, "restartTime", out IActionResult? rtErr))
            return rtErr!;
        if (body.RestartDay is { } rd && !TryApplyConfig(
                instances, id, "restart_day", rd.Trim().ToLowerInvariant(), actor, origin,
                applied, "restartDay", out IActionResult? rdErr))
            return rdErr!;
        if (body.Timezone is { } tzPatch && !TryApplyConfig(
                instances, id, "timezone", tzPatch.Trim(), actor, origin,
                applied, "timezone", out IActionResult? tzErr))
            return tzErr!;

        // Phase 4 — auto-backup config keys (echo-path audit, same as the schedule keys — the scheduler leaf
        // re-reads kgsm config as its source of truth, so persisting the key is the whole apply).
        if (body.AutoBackupOnRestart is { } autoBackup && !TryApplyConfig(
                instances, id, "auto_backup_on_restart", autoBackup ? "true" : "false", actor, origin,
                applied, "autoBackupOnRestart", out IActionResult? abErr))
            return abErr!;
        if (body.BackupRetention is { } retention && !TryApplyConfig(
                instances, id, "backup_retention", retention.ToString(), actor, origin,
                applied, "backupRetention", out IActionResult? brErr))
            return brErr!;

        // Phase 6 — crash-restart policy config keys (echo-path audit, same as the schedule keys).
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
        bool freshAutoUpdate = fresh?.AutoUpdate ?? (body.AutoUpdate ?? false);
        bool? freshAutostart = await ReadAutostartAsync(id, ct).ConfigureAwait(false);
        SchedulerInstanceStatus? freshSchedStatus = await ReadSchedulerStatusAsync(id, ct).ConfigureAwait(false);

        var settings = new ServerSettings(
            id, freshAutoUpdate, freshAutostart, fresh?.CpuPriority, fresh?.MemoryCapMb,
            fresh?.ScheduledRestart, fresh?.RestartTime, fresh?.RestartDay, fresh?.Timezone,
            freshSchedStatus?.NextFireUtc,
            fresh?.AutoBackupOnRestart, fresh?.BackupRetention,
            freshSchedStatus?.LastBackupUtc, freshSchedStatus?.LastBackupOk,
            fresh?.CrashRestart, fresh?.CrashMaxRestarts);
        return Ok(new ServerSettingsApplied(applied, settings));
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
    // event, the M5 consumer writes the row; no direct audit here). Adds to `applied` on success; on an
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

    // This instance's scheduler-computed state (nextFireUtc + last-backup outcome) comes ONLY from the
    // scheduler leaf's status socket. Null when the scheduler is not provisioned (client unregistered) or
    // unreachable, or when the leaf reports no row for this instance — honest unknown, never fabricated.
    // GetStatusAsync never throws (returns null on failure).
    private async Task<SchedulerInstanceStatus?> ReadSchedulerStatusAsync(string id, CancellationToken ct)
    {
        if (HttpContext.RequestServices.GetService(typeof(SchedulerClient)) is not SchedulerClient scheduler)
            return null;

        SchedulerStatusResponse? status = await scheduler.GetStatusAsync(ct).ConfigureAwait(false);
        return status?.Instances
            .FirstOrDefault(i => string.Equals(i.Name, id, StringComparison.Ordinal));
    }

    private static bool IsValidHhMm(string value)
    {
        string[] parts = value.Trim().Split(':');
        return parts.Length == 2
            && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m)
            && h is >= 0 and <= 23 && m is >= 0 and <= 59;
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
