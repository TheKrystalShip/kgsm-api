using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Per-server backups (Tier-1 ops) — <c>GET /servers/{id}/backups</c> (list), <c>POST /servers/{id}/backups</c>
/// (create), and <c>POST /servers/{id}/backups/restore</c> (restore from a named snapshot). The list is a
/// viewer-gated synchronous read (kgsm <c>instances backups</c> is quick); create and restore are operator-gated
/// and async — they reuse the shared <see cref="JobRegistry"/>/<see cref="CommandRunner"/> (one job model, one
/// in-flight slot per server) exactly like install/uninstall, returning <c>202</c> + a job. Restore lives on its
/// own sub-route (not a <c>/commands</c> verb) because it carries a <c>backup</c> name and the command verbs are
/// param-less; create is symmetric with it. Both are audited via the kgsm event echo
/// (<c>instance_backup_created</c> → <c>backup.create</c>, <c>instance_backup_restored</c> → <c>backup.restore</c>) —
/// no direct audit write (the no-double-write contract).
/// </summary>
[ApiController]
[Route("api/v1/servers/{id}/backups")]
[Authorize(Policy = AuthPolicy.Viewer)] // list — viewer and up; create/restore below require operator
public sealed class ServerBackupsController(
    ServerAggregator aggregator,
    JobRegistry jobs,
    CommandRunner runner) : ControllerBase
{
    /// <summary>
    /// List this instance's backups (<c>{ serverId, backups: [{ name }] }</c>). kgsm reports names only, so
    /// name is the sole field (size/when/type are omitted, not fabricated). Newest-first as the engine lists.
    /// <list type="bullet">
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — the backup list (possibly empty — no snapshots yet).</item>
    /// </list>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(string id, CancellationToken ct)
    {
        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        KgsmResult result = instances.GetBackups(id);
        if (!result.IsSuccess)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                string.IsNullOrWhiteSpace(result.Stderr)
                    ? $"could not list backups (exit {result.ExitCode})"
                    : result.Stderr.Trim());

        // kgsm prints one backup name per line; blank lines dropped. An empty stdout = no snapshots (a
        // legitimate empty list, never an error).
        IReadOnlyList<ServerBackup> backups = (result.Stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => new ServerBackup(name))
            .ToArray();

        return Ok(new ServerBackupList(id, backups));
    }

    /// <summary>
    /// Create a backup of the instance (async). Returns <c>202</c> + a <c>backup_create</c> job; the snapshot
    /// is taken off-request and appears on a subsequent list with a <c>backup.create</c> audit row (kgsm echo).
    /// <list type="bullet">
    /// <item><c>400</c> — a bad origin.</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>409</c> — a command is already in flight for this server.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>202</c> — accepted: <c>{ job }</c>.</item>
    /// </list>
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicy.Operator)] // mutation — operator and up
    public async Task<IActionResult> Create(string id, [FromBody] CreateBackupRequest? body, CancellationToken ct)
    {
        if (!TryResolveOrigin(body?.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        if (TryStart(id, CommandVerb.BackupCreate, out Job job, out IActionResult conflict) is false)
            return conflict;

        string? actor = AuditPrincipal.ActorString(User);
        runner.StartBackupCreate(job, actor, origin);
        return StatusCode(StatusCodes.Status202Accepted, new CommandAccepted(job));
    }

    /// <summary>
    /// Restore the instance from a named backup (async). Returns <c>202</c> + a <c>backup_restore</c> job; the
    /// restore runs off-request and lands a <c>backup.restore</c> audit row (kgsm echo). An unknown backup name
    /// is surfaced honestly as a failed job + the engine's real error (the engine owns the name set).
    /// <list type="bullet">
    /// <item><c>400</c> — missing <c>backup</c> name or a bad origin.</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>409</c> — a command is already in flight for this server.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>202</c> — accepted: <c>{ job }</c>.</item>
    /// </list>
    /// </summary>
    [HttpPost("restore")]
    [Authorize(Policy = AuthPolicy.Operator)] // mutation — operator and up
    public async Task<IActionResult> Restore(string id, [FromBody] RestoreBackupRequest? body, CancellationToken ct)
    {
        string? backup = body?.Backup?.Trim();
        if (string.IsNullOrEmpty(backup))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "backup name is required");

        if (!TryResolveOrigin(body?.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        if (TryStart(id, CommandVerb.BackupRestore, out Job job, out IActionResult conflict) is false)
            return conflict;

        string? actor = AuditPrincipal.ActorString(User);
        runner.StartBackupRestore(job, backup, actor, origin);
        return StatusCode(StatusCodes.Status202Accepted, new CommandAccepted(job));
    }

    // Claim the single in-flight slot for this server (atomic), mirroring ServersController. Returns false +
    // a 409 result when a command is already running for the server.
    private bool TryStart(string id, string verb, out Job job, out IActionResult conflict)
    {
        string jobId = "job_" + Guid.NewGuid().ToString("N")[..8];
        Job? started = jobs.TryStart(jobId, id, verb, DateTimeOffset.UtcNow);
        if (started is null)
        {
            Job? existing = jobs.InFlightFor(id);
            conflict = Error(StatusCodes.Status409Conflict, "conflict",
                existing is not null
                    ? $"a command is already in flight for this server (job {existing.Id})"
                    : "a command is already in flight for this server");
            job = null!;
            return false;
        }
        job = started;
        conflict = null!;
        return true;
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
