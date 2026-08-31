using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Backups;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Per-server backups (Tier-1 ops) — <c>GET /servers/{id}/backups</c> (list), <c>POST /servers/{id}/backups</c>
/// (create), <c>POST /servers/{id}/backups/restore</c> (restore from a named snapshot),
/// <c>DELETE /servers/{id}/backups/{backupId}</c> (remove one), and the two-step archive download
/// (<c>POST …/download-ticket</c> then <c>GET …/archive</c>). The list is a viewer-gated synchronous read
/// (kgsm <c>instances backups</c> is quick); everything that mutates or hands over bytes is operator-gated.
/// <para>
/// Create and restore are async — they reuse the shared <see cref="JobRegistry"/>/<see cref="CommandRunner"/>
/// (one job model, one in-flight slot per server) exactly like install/uninstall, returning <c>202</c> + a job.
/// Delete answers inside the request instead, because removing a backup is an unlink: it still takes the same
/// in-flight slot, so it can never run alongside the restore that reads what it is removing. Restore and delete
/// live on their own routes rather than as <c>/commands</c> verbs because they name a backup and the command
/// verbs are param-less; create is symmetric with them.
/// </para>
/// Every mutation is audited via the kgsm event echo (<c>backup.created</c> → <c>backup.create</c>,
/// <c>backup.restored</c> → <c>backup.restore</c>, <c>backup.deleted</c> →
/// <c>backup.delete</c>) — no direct audit write (the no-double-write contract). The one exception is
/// <c>backup.download</c>, which no engine command produces: nothing happens on the host when an archive is
/// served, so the API is the only witness and writes that row itself.
/// </summary>
[ApiController]
[Route("api/v1/servers/{id}/backups")]
[Authorize(Policy = AuthPolicy.Viewer)] // list — viewer and up; create/restore below require operator
public sealed class ServerBackupsController(
    ServerAggregator aggregator,
    JobRegistry jobs,
    CommandRunner runner,
    BackupDownloadTickets tickets,
    ApiJournal journal,
    ILogger<ServerBackupsController> logger) : ControllerBase
{
    /// <summary>
    /// List this instance's backups (<c>{ serverId, backups: [...] }</c>), newest first as the engine lists.
    /// Each entry carries what that backup's manifest records — creation time, captured version, size, file
    /// count, sources and digest — with anything the manifest lacks left null rather than defaulted.
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

        // The id-only listing runs first because it distinguishes an engine failure from an empty store;
        // the detailed read collapses both to an empty list, so it cannot carry that signal on its own.
        KgsmResult result = instances.GetBackups(id);
        if (!result.IsSuccess)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                string.IsNullOrWhiteSpace(result.Stderr)
                    ? $"could not list backups (exit {result.ExitCode})"
                    : result.Stderr.Trim());

        // kgsm prints one backup id per line; blank lines dropped. An empty stdout = no backups (a
        // legitimate empty list, never an error).
        string[] ids = (result.Stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Join each id to its manifest. An id with no manifest still appears, carrying its id alone —
        // the backup exists, we just have no detail for it, which is not the same as it not existing.
        Dictionary<string, InstanceBackup> detail = instances.GetBackupsDetailed(id)
            .Where(b => !string.IsNullOrWhiteSpace(b.Id))
            .GroupBy(b => b.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        IReadOnlyList<ServerBackup> backups = ids
            .Select(backupId => detail.TryGetValue(backupId, out InstanceBackup? m)
                ? ServerBackupMapping.FromManifest(m)
                : ServerBackupMapping.IdOnly(backupId))
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

    /// <summary>
    /// Delete one backup (operator). Synchronous — removing a backup is an unlink, not a transfer, so it
    /// answers within the request and the caller can re-list immediately; there is no job to await and
    /// nothing to show progress for. Audited via the kgsm event echo (<c>backup.deleted</c> →
    /// <c>backup.delete</c>, at warn) — no direct write here.
    /// <list type="bullet">
    /// <item><c>400</c> — a bad origin.</item>
    /// <item><c>404</c> — unknown server id, or no such backup (the engine owns the name set and refuses
    /// an id it does not itself list, which is what stops an arbitrary directory being named and removed).</item>
    /// <item><c>409</c> — a command is already in flight for this server: a restore reads the very bytes a
    /// delete would remove, so the two are never allowed to overlap.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>204</c> — deleted.</item>
    /// </list>
    /// </summary>
    [HttpDelete("{backupId}")]
    [Authorize(Policy = AuthPolicy.Operator)] // mutation, and an irreversible one — operator and up
    public async Task<IActionResult> Delete(
        string id, string backupId, [FromQuery] string? origin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(backupId))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "backup id is required");

        // A DELETE carries no body, so the driving surface rides the query string. Same vocabulary and
        // same refusal as the POST bodies — the audit row must not be able to claim an origin nobody can
        // declare, whichever verb produced it.
        if (!TryResolveOrigin(origin, out string resolvedOrigin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        // Claim the server's single in-flight slot for the duration, exactly as create and restore do.
        // The slot is the only thing that makes "no delete during a restore" true rather than merely
        // likely: a check that only reads the registry leaves a window between the read and the unlink,
        // and the loser of that race is a restore reading a directory that is disappearing underneath it.
        if (TryStart(id, CommandVerb.BackupDelete, out Job job, out IActionResult conflict) is false)
            return conflict;

        KgsmResult result;
        try
        {
            result = instances.DeleteBackup(id, backupId, AuditPrincipal.ActorString(User), resolvedOrigin);
        }
        catch (Exception ex)
        {
            jobs.Update(job with { State = JobState.Failed, SettledAt = DateTimeOffset.UtcNow, Error = ex.Message });
            logger.LogWarning(ex, "Failed to delete backup {Backup} of {Server}", backupId, id);
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable", "could not delete the backup");
        }

        jobs.Update(job with
        {
            State = result.IsSuccess ? JobState.Succeeded : JobState.Failed,
            SettledAt = DateTimeOffset.UtcNow,
            Error = result.IsSuccess ? null : result.Stderr?.Trim(),
        });

        if (result.IsSuccess)
            return NoContent();

        // The engine refuses an id it does not list, which is the same answer as "no such backup" — and
        // the SPA's row is stale either way, so 404 is the honest code. Its own message carries through
        // rather than a guess at what went wrong.
        string message = string.IsNullOrWhiteSpace(result.Stderr)
            ? $"could not delete the backup (exit {result.ExitCode})"
            : result.Stderr.Trim();
        return Error(StatusCodes.Status404NotFound, "not_found", message);
    }

    /// <summary>
    /// Delete this instance's surplus backups, keeping the newest <c>keep</c>
    /// (<c>POST …/backups/prune</c>).
    /// <list type="bullet">
    /// <item><c>400</c> — <c>keep</c> missing or below 1, or a bad origin.</item>
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>409</c> — a command is already in flight for this server.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned, or it refused the prune.</item>
    /// <item><c>204</c> — the surplus is gone.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// One engine call, holding the server's in-flight slot for the same reason delete does: it removes
    /// the bytes a restore would be reading. The engine owns what surplus means — a pinned archive is
    /// not surplus — so nothing here decides which files go.
    /// <para>
    /// <c>keep: 0</c> is refused rather than obeyed. Keeping none is a delete-all, which is a different
    /// act with a different blast radius, and it must be asked for as itself rather than reached by
    /// passing a zero to a prune.
    /// </para>
    /// </remarks>
    [HttpPost("prune")]
    [Authorize(Policy = AuthPolicy.Operator)] // mutation, and an irreversible one — operator and up
    public async Task<IActionResult> Prune(string id, [FromBody] PruneBackupsRequest? body, CancellationToken ct)
    {
        if (body?.Keep is not { } keep || keep < 1)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "keep is required and must be at least 1; a prune that keeps nothing is a delete-all");

        if (!TryResolveOrigin(body.Origin, out string resolvedOrigin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        if (TryStart(id, CommandVerb.BackupPrune, out Job job, out IActionResult conflict) is false)
            return conflict;

        KgsmResult result;
        try
        {
            result = instances.PruneBackups(id, keep, AuditPrincipal.ActorString(User), resolvedOrigin);
        }
        catch (Exception ex)
        {
            jobs.Update(job with { State = JobState.Failed, SettledAt = DateTimeOffset.UtcNow, Error = ex.Message });
            logger.LogWarning(ex, "Failed to prune backups of {Server}", id);
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable", "could not prune the backups");
        }

        jobs.Update(job with
        {
            State = result.IsSuccess ? JobState.Succeeded : JobState.Failed,
            SettledAt = DateTimeOffset.UtcNow,
            Error = result.IsSuccess ? null : result.Stderr?.Trim(),
        });

        if (result.IsSuccess)
            return NoContent();

        // The engine's own sentence, because it is the only part naming what it would not do.
        string message = string.IsNullOrWhiteSpace(result.Stderr)
            ? $"could not prune the backups (exit {result.ExitCode})"
            : result.Stderr.Trim();
        return Error(StatusCodes.Status503ServiceUnavailable, "unavailable", message);
    }

    /// <summary>
    /// Pin one backup (<c>POST …/{backupId}/pin</c>) or hand it back to retention
    /// (<c>POST …/{backupId}/unpin</c>). Answers inside the request — the engine rewrites one manifest,
    /// which is a metadata edit and not the archive's bytes.
    /// <list type="bullet">
    /// <item><c>400</c> — a missing backup id or a bad origin.</item>
    /// <item><c>404</c> — unknown server id, or no such backup (the engine owns the name set).</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>204</c> — the retention was changed.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> claim the server's in-flight slot, unlike delete. A pin touches no
    /// archive bytes and the engine publishes the rewritten manifest by rename, so a concurrent read sees
    /// the old document or the new one and never a partial one. Holding the slot would refuse a metadata
    /// edit for the whole of a backup run that has nothing to do with it.
    /// <para>
    /// Audited through the kgsm echo (<c>backup.pinned</c> → <c>backup.pin</c>,
    /// <c>backup.unpinned</c> → <c>backup.unpin</c>) — no direct write here.
    /// </para>
    /// </remarks>
    [HttpPost("{backupId}/pin")]
    [Authorize(Policy = AuthPolicy.Operator)] // mutation — operator and up
    public Task<IActionResult> Pin(string id, string backupId, [FromBody] BackupRetentionRequest? body, CancellationToken ct)
        => SetRetentionAsync(id, backupId, body?.Origin, pin: true, ct);

    [HttpPost("{backupId}/unpin")]
    [Authorize(Policy = AuthPolicy.Operator)] // mutation — operator and up
    public Task<IActionResult> Unpin(string id, string backupId, [FromBody] BackupRetentionRequest? body, CancellationToken ct)
        => SetRetentionAsync(id, backupId, body?.Origin, pin: false, ct);

    // Both directions share one path so the gate, the origin vocabulary and the failure shape cannot
    // drift between pinning and unpinning.
    private async Task<IActionResult> SetRetentionAsync(
        string id, string backupId, string? origin, bool pin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(backupId))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "backup id is required");

        if (!TryResolveOrigin(origin, out string resolvedOrigin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        string? actor = AuditPrincipal.ActorString(User);
        KgsmResult result;
        try
        {
            result = pin
                ? instances.PinBackup(id, backupId, actor, resolvedOrigin)
                : instances.UnpinBackup(id, backupId, actor, resolvedOrigin);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to change the retention of backup {Backup} of {Server}", backupId, id);
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "could not change the backup's retention");
        }

        if (result.IsSuccess)
            return NoContent();

        // The engine refuses an id it does not list, which is the same answer as "no such backup"; its
        // own message carries through rather than a guess at what went wrong.
        string message = string.IsNullOrWhiteSpace(result.Stderr)
            ? $"could not change the backup's retention (exit {result.ExitCode})"
            : result.Stderr.Trim();
        return Error(StatusCodes.Status404NotFound, "not_found", message);
    }

    /// <summary>
    /// Mint a short-lived ticket for downloading one backup's archive (operator — a backup carries the
    /// instance's whole install and saves, so it holds every secret the file browser is operator-gated
    /// for, in bulk). Returns the handle plus the relative URL to navigate to.
    /// <list type="bullet">
    /// <item><c>400</c> — a bad origin.</item>
    /// <item><c>404</c> — unknown server id, or no such backup.</item>
    /// <item><c>409</c> — the backup is uncompressed (<c>backup_uncompressed</c>): a directory tree is
    /// not a single artifact and has no digest, so there is nothing to hand over honestly.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — the ticket.</item>
    /// </list>
    /// </summary>
    [HttpPost("{backupId}/download-ticket")]
    [Authorize(Policy = AuthPolicy.Operator)] // a whole-instance archive — bulk secrets, same tier as file read
    public async Task<IActionResult> MintDownloadTicket(
        string id, string backupId, [FromBody] CreateBackupRequest? body, CancellationToken ct)
    {
        if (!TryResolveOrigin(body?.Origin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceBackups)) is not IInstanceBackups backups)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        // Open it now, purely to answer honestly: a ticket for a backup that cannot be served would
        // turn a 404 into a broken download two clicks later, with nothing to explain it. The stream is
        // disposed immediately — this is a probe, and the archive is reopened at redemption because the
        // ticket outlives this request.
        FileOpResult<BackupArchive> probe = backups.OpenArchive(id, backupId);
        if (probe.Outcome != FileOpOutcome.Ok)
            return DescribeArchiveFailure(probe.Outcome);

        long sizeBytes;
        string? sha256;
        using (BackupArchive archive = probe.Value!)
        {
            sizeBytes = archive.SizeBytes;
            sha256 = archive.Sha256;
        }

        (string handle, BackupDownloadTicket ticket) = tickets.Mint(
            id, backupId,
            AuditPrincipal.ActorString(User),
            origin,
            User.FindFirst(KgsmAuthClaims.SessionId)?.Value);

        return Ok(new BackupDownloadTicketResponse(
            handle,
            $"/api/v1/servers/{Uri.EscapeDataString(id)}/backups/{Uri.EscapeDataString(backupId)}/archive?ticket={handle}",
            ticket.ExpiresAt,
            sizeBytes,
            sha256));
    }

    /// <summary>
    /// Stream a backup's archive. Authenticated by the <c>?ticket=</c> alone — a browser navigation
    /// cannot set an Authorization header, and that is the entire reason the ticket exists.
    /// <list type="bullet">
    /// <item><c>401</c> — missing, expired, or wrong-backup ticket (<c>invalid_ticket</c>).</item>
    /// <item><c>404</c> — no such backup.</item>
    /// <item><c>409</c> — the backup is uncompressed.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c>/<c>206</c> — the archive, range-capable so a broken transfer resumes.</item>
    /// </list>
    /// </summary>
    [HttpGet("{backupId}/archive")]
    [AllowAnonymous] // the ticket IS the credential; no bearer reaches a navigation
    public IActionResult DownloadArchive(string id, string backupId, [FromQuery] string? ticket)
    {
        if (!tickets.TryRedeem(ticket, id, backupId, out BackupDownloadTicket? redeemed, out bool firstRedemption))
            return Error(StatusCodes.Status401Unauthorized, "invalid_ticket",
                "this download link is invalid or has expired; request the download again");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceBackups)) is not IInstanceBackups backups)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        FileOpResult<BackupArchive> result = backups.OpenArchive(id, backupId);
        if (result.Outcome != FileOpOutcome.Ok)
            return DescribeArchiveFailure(result.Outcome);

        BackupArchive archive = result.Value!;

        // Audited HERE, not at mint: minting is an intent, this is the archive actually leaving the
        // host. Fire-and-forget so a slow audit write never stalls the transfer, and only on the first
        // redemption so a resumed download stays one row. Warn, because bulk data leaving the host is
        // worth seeing in a feed even when it is entirely routine.
        if (firstRedemption)
            _ = WriteDownloadAudit(id, backupId, archive, redeemed!);

        if (archive.Sha256 is { Length: > 0 } digest)
            Response.Headers["X-Backup-Sha256"] = digest;

        // The engine names every archive data.tar.gz; the backup id is what distinguishes them, so it
        // becomes the download name — otherwise every backup a user keeps lands as data(3).tar.gz.
        return File(archive.Content, "application/gzip", $"{backupId}.tar.gz", enableRangeProcessing: true);
    }

    // Recorded when the archive was authorised to leave, not when somebody clicked: the fact worth
    // keeping is that a copy of a world left this host. The actor and origin come off the ticket the
    // download was issued against, so a streamed file is still attributable to who asked for it.
    private Task WriteDownloadAudit(
        string id, string backupId, BackupArchive archive, BackupDownloadTicket ticket) =>
        journal.BackupDownloadedAsync(
            id, backupId, archive.SizeBytes, archive.Sha256,
            ticket.Actor ?? "", AuditMapping.NormalizeOrigin(ticket.Origin));

    // One mapping for both archive endpoints, so the ticket probe and the download cannot disagree about
    // what a given failure means.
    private ObjectResult DescribeArchiveFailure(FileOpOutcome outcome) => outcome switch
    {
        FileOpOutcome.NotAFile => Error(StatusCodes.Status409Conflict, "backup_uncompressed",
            "this backup is an uncompressed directory tree, so it cannot be downloaded as one file"),
        FileOpOutcome.InstanceUnavailable => Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
            "the instance's backups directory is unavailable"),
        // OutOfJail folds into 404 with NotFound — never reveal that a path resolved outside the store.
        _ => Error(StatusCodes.Status404NotFound, "not_found", "no such backup"),
    };

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
