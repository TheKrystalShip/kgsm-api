using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Dns.Member;

namespace TheKrystalShip.Api.Services.Transfers;

/// <summary>Why a transfer call was refused: the status, and the code and words of the error envelope.</summary>
public sealed record TransferProblem(int Status, string Code, string Message);

/// <summary>
/// The source's half of a transfer: the ticket, the lock, the parts, the commit and the way out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each node checks its own half.</b> The person's session never leaves the node it was presented to.
/// The source mints a ticket bound to the instance, the target member and the person's account, and every
/// time the target redeems it the source evaluates <c>kgsm:server.export</c> for that account again — the
/// pattern of the backup-download ticket. The ticket expires ten minutes after minting unless the lock was
/// taken; from the lock on it stays good until the transfer ends, because the lock is what holds the
/// server and only its end may release it.
/// </para>
/// <para>
/// <b>One transition at a time per transfer.</b> The lock, the abort, the commit and a person's cancel all
/// read the record, act on the engine and write the record; a gate per transfer keeps a cancel from
/// releasing a lock the target is in the middle of taking.
/// </para>
/// <para>
/// <b>The lock is released only on the target's word, or the person's.</b> The target may already hold the
/// imported server, and an unlocked source would let the same server run in two places. Recovery and the
/// cancel ask the target first; a target that cannot be asked keeps the lock, unless a person forces it.
/// </para>
/// </remarks>
[PerformedFor(ActionIds.ServerExport)]
public sealed class TransferSource(
    IServiceScopeFactory scopeFactory,
    TransferStore store,
    TransferPeerClient peers,
    MembersStore members,
    ClusterOptions cluster,
    ApiOptions options,
    InstanceCache cache,
    JobRegistry jobs,
    DnsNameBook names,
    NodeAccess access,
    ApiJournal journal,
    DomainPump pump,
    TransferClock time,
    ILogger<TransferSource> logger)
{
    /// <summary>How long a ticket stays redeemable before the lock is taken.</summary>
    public static readonly TimeSpan TicketTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string TransferId, string PartId), string> _partHashes = new();

    // ---- the person's routes ----------------------------------------------------------------------

    /// <summary>Mints a transfer of <paramref name="instanceId"/> to <paramref name="targetMemberId"/>.</summary>
    public async Task<(TransferTicket? Ticket, TransferProblem? Problem)> CreateAsync(
        string instanceId, string? targetMemberId, ClaimsPrincipal user, CancellationToken ct)
    {
        string target = targetMemberId?.Trim() ?? "";
        if (target.Length == 0)
            return (null, new TransferProblem(400, "bad_request", "targetMemberId is required"));

        if (!cluster.Enabled)
            return (null, new TransferProblem(422, TransferRefusalCodes.TargetNotNode,
                "this node is not a member of a cluster"));

        MemberRow? peer = await members.GetByMemberIdAsync(target, ct).ConfigureAwait(false);
        if (peer is null || !peer.Enabled || peer.Kind != MemberKind.Node
            || string.Equals(target, options.NodeId, StringComparison.Ordinal))
        {
            return (null, new TransferProblem(422, TransferRefusalCodes.TargetNotNode,
                $"'{target}' is not another enabled node of this cluster"));
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        if (Engine(scope) is not { } engine)
            return (null, new TransferProblem(503, "unavailable", "the kgsm engine is not provisioned on this host"));

        if (!cache.Roster.ContainsKey(instanceId))
            return (null, new TransferProblem(404, "not_found", $"no server '{instanceId}' on this node"));

        if (IsMeasuredRunning(instanceId))
            return (null, new TransferProblem(409, TransferRefusalCodes.InstanceRunning,
                $"'{instanceId}' is running; stop it before transferring it"));

        if (jobs.InFlightFor(instanceId) is { } job)
            return (null, new TransferProblem(409, "conflict",
                $"a command is already in flight for this server (job {job.Id})"));

        DateTimeOffset now = time.GetUtcNow();
        foreach (TransferEntity existing in await store.ListAsync(TransferRole.Source, ct).ConfigureAwait(false))
        {
            if (!string.Equals(existing.InstanceId, instanceId, StringComparison.Ordinal)) continue;

            if (existing.State == TransferState.Locked)
                return (null, new TransferProblem(409, TransferRefusalCodes.TransferInProgress,
                    $"'{instanceId}' is already being transferred ({existing.TransferId})"));

            // A ticket that holds no lock is superseded by the next one: the person closed the dialog and
            // opened it again, and nothing on either node depends on the old one.
            if (existing.State == TransferState.Ticketed)
            {
                await WithGateAsync(existing.TransferId, async () =>
                {
                    await store.UpdateAsync(existing.TransferId, TransferRole.Source, r =>
                    {
                        if (r.State == TransferState.Ticketed)
                        {
                            r.State = TransferState.Aborted;
                            r.Reason = TransferRefusalCodes.TransferCancelled;
                        }
                    }, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
        }

        string? actor = AuditPrincipal.ActorString(user);
        TransferOutcome<TransferManifest> read = await Task.Run(
            () => engine.ExportManifest(instanceId, lockToken: null, actor, AuditOrigin.Api), ct).ConfigureAwait(false);
        if (read.Value is not { } manifest)
            return (null, EngineProblem(read.Result, instanceId));

        string transferId = "xfer_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        string ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string? gameName = names.GameName(instanceId);
        DateTimeOffset expires = now + TicketTtl;

        await store.TryAddAsync(new TransferEntity
        {
            TransferId = transferId,
            Role = TransferRole.Source,
            InstanceId = instanceId,
            PeerMemberId = target,
            GameName = gameName,
            State = TransferState.Ticketed,
            TicketHash = Hash(ticket),
            TicketExpiresAt = expires,
            AccountId = NodeAccess.AccountOf(user),
            Actor = actor,
            Origin = AuditOrigin.Api,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct).ConfigureAwait(false);
        pump.Nudge();

        logger.LogInformation("transfer {TransferId}: ticket minted for {Instance} to {Target}",
            transferId, instanceId, target);
        return (new TransferTicket(transferId, ticket, options.NodeId, expires, Summary(manifest, gameName)), null);
    }

    /// <summary>
    /// The person's way out: ends the transfer holding <paramref name="instanceId"/>, after asking the
    /// target. <paramref name="force"/> releases the lock when the target cannot answer or is still running.
    /// </summary>
    public async Task<TransferProblem?> CancelAsync(string instanceId, bool force, string? actor, CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        TransferEntity? current = (await store.ListAsync(TransferRole.Source, ct).ConfigureAwait(false))
            .FirstOrDefault(r => string.Equals(r.InstanceId, instanceId, StringComparison.Ordinal)
                                 && TransferStore.IsLive(r, now));
        if (current is null)
            return new TransferProblem(404, "not_found", $"'{instanceId}' has no transfer under way");

        TransferProblem? problem = null;
        await WithGateAsync(current.TransferId, async () =>
        {
            TransferEntity? r = await store.GetAsync(current.TransferId, TransferRole.Source, ct).ConfigureAwait(false);
            if (r is null || TransferState.IsTerminal(r.State)) return;

            if (r.State == TransferState.Ticketed)
            {
                await EndAsync(r, TransferRefusalCodes.TransferCancelled, TransferPhase.Locking, actor, ct)
                    .ConfigureAwait(false);
                return;
            }

            (PeerAnswer answer, TransferOutcome? outcome) =
                await peers.OutcomeAsync(r.PeerMemberId, r.TransferId, ct).ConfigureAwait(false);

            if (outcome?.State == TransferState.Committed)
            {
                problem = new TransferProblem(409, TransferRefusalCodes.TransferCommitted,
                    $"{r.PeerMemberId} already holds this server; it is removed here when the commit is applied");
                return;
            }

            if (outcome?.State == TransferState.Running && !force)
            {
                problem = new TransferProblem(409, TransferRefusalCodes.TransferInProgress,
                    $"{r.PeerMemberId} is still running this transfer; force releases the lock regardless");
                return;
            }

            bool unknownThere = answer.Status == 404;
            if (outcome is null && !unknownThere && !force)
            {
                problem = new TransferProblem(502, TransferRefusalCodes.MemberUnreachable,
                    $"{r.PeerMemberId} could not be asked how the transfer stands; force releases the lock regardless");
                return;
            }

            if (!await UnlockAsync(r, force, actor, ct).ConfigureAwait(false))
            {
                problem = new TransferProblem(500, "internal", "the engine could not release the transfer lock");
                return;
            }

            string reason = outcome?.State == TransferState.Aborted && outcome.Reason is { Length: > 0 } theirs
                ? theirs
                : TransferRefusalCodes.TransferCancelled;
            await EndAsync(r, reason, TransferPhase.Transferring, actor, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return problem;
    }

    // ---- the target's member routes -----------------------------------------------------------------

    /// <summary>
    /// Checks a member route's caller against the transfer: its service token's issuer is the transfer's
    /// target and the ticket is the one minted. With <paramref name="redeem"/>, the ticket must also be
    /// redeemable now and the minting account still allowed <c>kgsm:server.export</c> at the instance.
    /// </summary>
    public async Task<(TransferEntity? Record, TransferProblem? Problem)> AuthorizeAsync(
        string callerMemberId, string transferId, string? ticket, bool redeem, CancellationToken ct)
    {
        TransferEntity? r = await store.GetAsync(transferId, TransferRole.Source, ct).ConfigureAwait(false);
        if (r is null)
            return (null, new TransferProblem(404, "not_found", $"no transfer '{transferId}' on this node"));

        if (!string.Equals(callerMemberId, r.PeerMemberId, StringComparison.Ordinal))
            return (null, new TransferProblem(403, TransferRefusalCodes.TicketInvalid,
                "this transfer was not issued to the calling member"));

        if (string.IsNullOrEmpty(ticket) || r.TicketHash is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(ticket)), Encoding.ASCII.GetBytes(r.TicketHash)))
        {
            return (null, new TransferProblem(403, TransferRefusalCodes.TicketInvalid, "the ticket is not this transfer's"));
        }

        if (!redeem) return (r, null);

        if (r.State == TransferState.Committed)
            return (null, new TransferProblem(409, TransferRefusalCodes.TransferCommitted, "the transfer is committed"));
        if (r.State == TransferState.Aborted)
            return (null, new TransferProblem(403, TransferRefusalCodes.TicketInvalid, "the transfer has ended"));
        if (r.State == TransferState.Ticketed && !TransferStore.IsLive(r, time.GetUtcNow()))
            return (null, new TransferProblem(403, TransferRefusalCodes.TicketInvalid, "the ticket has expired"));

        if (!await access.AllowsAccountAsync(r.AccountId, ActionIds.ServerExport, access.ServerTarget(r.InstanceId), ct)
                .ConfigureAwait(false))
        {
            return (null, new TransferProblem(403, TransferRefusalCodes.TicketInvalid,
                "the account that minted the ticket may no longer transfer this server"));
        }

        return (r, null);
    }

    /// <summary>The instance's manifest without a lock, for the target's preflight.</summary>
    public async Task<(TransferManifest? Manifest, TransferProblem? Problem)> ManifestAsync(
        TransferEntity r, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        if (Engine(scope) is not { } engine)
            return (null, new TransferProblem(503, "unavailable", "the kgsm engine is not provisioned on this host"));

        TransferOutcome<TransferManifest> read = await Task.Run(
            () => engine.ExportManifest(r.InstanceId, lockToken: null, r.Actor, r.Origin), ct).ConfigureAwait(false);
        return read.Value is { } manifest ? (manifest, null) : (null, EngineProblem(read.Result, r.InstanceId));
    }

    /// <summary>Takes the lock, or confirms it is already held for this transfer; the manifest as of the lock.</summary>
    public async Task<(TransferManifest? Manifest, TransferProblem? Problem)> LockAsync(
        TransferEntity record, CancellationToken ct)
    {
        TransferManifest? manifest = null;
        TransferProblem? problem = null;

        await WithGateAsync(record.TransferId, async () =>
        {
            TransferEntity? r = await store.GetAsync(record.TransferId, TransferRole.Source, ct).ConfigureAwait(false);
            if (r is null || TransferState.IsTerminal(r.State))
            {
                problem = new TransferProblem(403, TransferRefusalCodes.TicketInvalid, "the transfer has ended");
                return;
            }

            if (r.State == TransferState.Ticketed && IsMeasuredRunning(r.InstanceId))
            {
                problem = new TransferProblem(409, TransferRefusalCodes.InstanceRunning,
                    $"'{r.InstanceId}' is running; it must stay stopped to be transferred");
                return;
            }

            using IServiceScope scope = scopeFactory.CreateScope();
            if (Engine(scope) is not { } engine)
            {
                problem = new TransferProblem(503, "unavailable", "the kgsm engine is not provisioned on this host");
                return;
            }

            TransferOutcome<TransferManifest> read = await Task.Run(
                () => engine.ExportManifest(r.InstanceId, r.TransferId, r.Actor, r.Origin), ct).ConfigureAwait(false);
            if (read.Value is null)
            {
                problem = EngineProblem(read.Result, r.InstanceId);
                return;
            }

            manifest = read.Value;
            if (r.State == TransferState.Ticketed)
            {
                await store.UpdateAsync(r.TransferId, TransferRole.Source, x => x.State = TransferState.Locked, ct)
                    .ConfigureAwait(false);
                pump.Nudge();
                logger.LogInformation("transfer {TransferId}: {Instance} locked for {Target}",
                    r.TransferId, r.InstanceId, r.PeerMemberId);
            }
        }, ct).ConfigureAwait(false);

        return (manifest, problem);
    }

    /// <summary>
    /// Streams one part into <paramref name="body"/>, hashing what is written. The hash is kept only when
    /// the engine finished the part, so a send cut short leaves none for the target to compare against.
    /// </summary>
    /// <returns>The engine's result; on a failure the caller decides what the response can still say.</returns>
    public async Task<StreamResult?> StreamPartAsync(TransferEntity r, string partId, Stream body, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        if (Engine(scope) is not { } engine) return null;

        _partHashes.TryRemove((r.TransferId, partId), out _);
        await using var hashing = new HashingStream(body, write: true);
        StreamResult result = await engine.ExportPartAsync(r.InstanceId, partId, r.TransferId, hashing, r.Actor, r.Origin, ct)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            await hashing.FlushAsync(ct).ConfigureAwait(false);
            _partHashes[(r.TransferId, partId)] = hashing.Hex();
        }
        else
        {
            logger.LogWarning("transfer {TransferId}: part {PartId} of {Instance} failed with exit {ExitCode}: {Stderr}",
                r.TransferId, partId, r.InstanceId, result.ExitCode, result.Stderr);
        }

        return result;
    }

    /// <summary>The SHA-256 of the last complete send of a part, or null.</summary>
    public string? PartHash(string transferId, string partId) =>
        _partHashes.TryGetValue((transferId, partId), out string? hex) ? hex : null;

    /// <summary>The target gave up: releases the lock and records why. Idempotent.</summary>
    public async Task<TransferProblem?> AbortAsync(
        TransferEntity record, string? reason, string? phase, CancellationToken ct)
    {
        TransferProblem? problem = null;
        await WithGateAsync(record.TransferId, async () =>
        {
            TransferEntity? r = await store.GetAsync(record.TransferId, TransferRole.Source, ct).ConfigureAwait(false);
            if (r is null || r.State == TransferState.Aborted) return;

            if (r.State == TransferState.Committed)
            {
                problem = new TransferProblem(409, TransferRefusalCodes.TransferCommitted, "the transfer is committed");
                return;
            }

            if (r.State == TransferState.Locked && !await UnlockAsync(r, force: false, r.Actor, ct).ConfigureAwait(false))
            {
                problem = new TransferProblem(500, "internal", "the engine could not release the transfer lock");
                return;
            }

            await EndAsync(r, reason is { Length: > 0 } ? reason : TransferRefusalCodes.TransferInterrupted,
                phase is { Length: > 0 } ? phase : r.State == TransferState.Locked ? TransferPhase.Transferring : TransferPhase.Locking,
                r.Actor, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return problem;
    }

    // ---- the commit -----------------------------------------------------------------------------------

    /// <summary>
    /// Applies a commit the target sent: removes the instance through the engine's uninstall path and writes
    /// the source's last line for it. Idempotent; an engine failure that may pass throws, so the bus retries.
    /// </summary>
    /// <remarks>
    /// A commit for a transfer this node aborted or never knew means a person forced the lock off while the
    /// target finished: two copies exist. It is recorded and journaled as such and acknowledged, because no
    /// retry changes it.
    /// </remarks>
    public async Task ApplyCommitAsync(TransferCommit commit, string fromMember, CancellationToken ct)
    {
        await WithGateAsync(commit.TransferId, async () =>
        {
            TransferEntity? r = await store.GetAsync(commit.TransferId, TransferRole.Source, ct).ConfigureAwait(false);
            if (r is null)
            {
                DateTimeOffset now = time.GetUtcNow();
                await store.TryAddAsync(new TransferEntity
                {
                    TransferId = commit.TransferId,
                    Role = TransferRole.Source,
                    InstanceId = commit.SourceInstanceId,
                    PeerMemberId = fromMember,
                    State = TransferState.Aborted,
                    Reason = TransferRefusalCodes.TransferCommitted,
                    TargetInstanceId = commit.TargetInstanceId,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, ct).ConfigureAwait(false);
                await ConflictAsync(commit.TransferId, commit.SourceInstanceId, null, ct).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(r.PeerMemberId, fromMember, StringComparison.Ordinal))
            {
                logger.LogWarning("transfer {TransferId}: a commit from {From} ignored; the transfer's target is {Target}",
                    r.TransferId, fromMember, r.PeerMemberId);
                return;
            }

            if (r.State == TransferState.Committed) return;

            if (r.State != TransferState.Locked)
            {
                if (r.Reason == TransferRefusalCodes.TransferCommitted) return;
                await store.UpdateAsync(r.TransferId, TransferRole.Source, x =>
                {
                    x.State = TransferState.Aborted;
                    x.Reason = TransferRefusalCodes.TransferCommitted;
                    x.TargetInstanceId = commit.TargetInstanceId;
                }, ct).ConfigureAwait(false);
                await ConflictAsync(r.TransferId, r.InstanceId, r.Actor, ct).ConfigureAwait(false);
                return;
            }

            using IServiceScope scope = scopeFactory.CreateScope();
            IInstanceService engine = Engine(scope)
                ?? throw new InvalidOperationException("the kgsm engine is not provisioned on this host");

            KgsmResult result = await Task.Run(() => engine.CommitExport(r.InstanceId, r.TransferId, r.Actor, r.Origin), ct)
                .ConfigureAwait(false);

            // The instance already gone is a commit applied before this process could record it.
            bool applied = result.ExitCode == 0 || result.ExitCode == TransferExitCodes.NotFound;
            if (!applied && result.ExitCode == TransferExitCodes.InstanceLocked)
            {
                await store.UpdateAsync(r.TransferId, TransferRole.Source, x =>
                {
                    x.State = TransferState.Aborted;
                    x.Reason = TransferRefusalCodes.TransferCommitted;
                    x.TargetInstanceId = commit.TargetInstanceId;
                }, ct).ConfigureAwait(false);
                await ConflictAsync(r.TransferId, r.InstanceId, r.Actor, ct).ConfigureAwait(false);
                return;
            }

            if (!applied)
            {
                throw new InvalidOperationException(
                    $"the engine could not remove {r.InstanceId} for transfer {r.TransferId} (exit {result.ExitCode}): {result.Stderr}");
            }

            await store.UpdateAsync(r.TransferId, TransferRole.Source, x =>
            {
                x.State = TransferState.Committed;
                x.TargetInstanceId = commit.TargetInstanceId;
            }, ct).ConfigureAwait(false);
            await journal.TransferCompletedAsync(r.TransferId, r.InstanceId, r.PeerMemberId, commit.TargetInstanceId,
                r.Actor, r.Origin, ct).ConfigureAwait(false);
            cache.TryRefresh();
            pump.Nudge();
            logger.LogInformation("transfer {TransferId}: {Instance} committed to {Target} as {TargetInstance}",
                r.TransferId, r.InstanceId, r.PeerMemberId, commit.TargetInstanceId);
        }, ct).ConfigureAwait(false);
    }

    // ---- recovery -------------------------------------------------------------------------------------

    /// <summary>
    /// Settles every source transfer this node holds a lock for by asking its target, and drops tickets that
    /// expired without a lock. Run at startup and on a timer.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        foreach (TransferEntity r in await store.ListAsync(TransferRole.Source, ct).ConfigureAwait(false))
        {
            if (r.State == TransferState.Ticketed && !TransferStore.IsLive(r, now))
            {
                await WithGateAsync(r.TransferId, () => store.UpdateAsync(r.TransferId, TransferRole.Source, x =>
                {
                    if (x.State == TransferState.Ticketed) x.State = TransferState.Aborted;
                }, ct), ct).ConfigureAwait(false);
                continue;
            }

            if (r.State != TransferState.Locked) continue;

            (PeerAnswer answer, TransferOutcome? outcome) =
                await peers.OutcomeAsync(r.PeerMemberId, r.TransferId, ct).ConfigureAwait(false);

            if (outcome is { State: TransferState.Committed, TargetInstanceId: { Length: > 0 } targetInstance })
            {
                await ApplyCommitAsync(new TransferCommit(r.TransferId, r.InstanceId, targetInstance), r.PeerMemberId, ct)
                    .ConfigureAwait(false);
            }
            else if (outcome?.State == TransferState.Aborted || answer.Status == 404)
            {
                await WithGateAsync(r.TransferId, async () =>
                {
                    TransferEntity? fresh = await store.GetAsync(r.TransferId, TransferRole.Source, ct).ConfigureAwait(false);
                    if (fresh?.State != TransferState.Locked) return;
                    if (await UnlockAsync(fresh, force: false, fresh.Actor, ct).ConfigureAwait(false))
                    {
                        await EndAsync(fresh, outcome?.Reason is { Length: > 0 } reason ? reason : TransferRefusalCodes.TransferInterrupted,
                            TransferPhase.Transferring, fresh.Actor, ct).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);
            }
            else if (outcome is null)
            {
                logger.LogWarning("transfer {TransferId}: {Target} could not be asked how it ended; {Instance} stays locked",
                    r.TransferId, r.PeerMemberId, r.InstanceId);
            }
        }
    }

    // ---- shared ---------------------------------------------------------------------------------------

    private async Task<bool> UnlockAsync(TransferEntity r, bool force, string? actor, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        if (Engine(scope) is not { } engine) return false;

        KgsmResult result = await Task.Run(
            () => force
                ? engine.Unlock(r.InstanceId, lockToken: null, force: true, actor, r.Origin)
                : engine.Unlock(r.InstanceId, r.TransferId, force: false, actor, r.Origin), ct).ConfigureAwait(false);

        // An instance that is gone holds no lock.
        if (result.ExitCode is 0 or TransferExitCodes.NotFound) return true;

        logger.LogWarning("transfer {TransferId}: the lock on {Instance} could not be released (exit {ExitCode}): {Stderr}",
            r.TransferId, r.InstanceId, result.ExitCode, result.Stderr);
        return false;
    }

    // Records the transfer aborted and writes the source's failed line.
    private async Task EndAsync(TransferEntity r, string reason, string phase, string? actor, CancellationToken ct)
    {
        await store.UpdateAsync(r.TransferId, TransferRole.Source, x =>
        {
            x.State = TransferState.Aborted;
            x.Reason = reason;
        }, ct).ConfigureAwait(false);
        await journal.TransferFailedAsync(r.TransferId, r.InstanceId, null, reason, phase, actor, r.Origin, ct)
            .ConfigureAwait(false);
        pump.Nudge();
        logger.LogInformation("transfer {TransferId}: {Instance} stays here ({Reason})", r.TransferId, r.InstanceId, reason);
    }

    private async Task ConflictAsync(string transferId, string instanceId, string? actor, CancellationToken ct)
    {
        logger.LogError("transfer {TransferId}: the target committed after this node released {Instance}; two copies exist",
            transferId, instanceId);
        await journal.TransferFailedAsync(transferId, instanceId, null, TransferRefusalCodes.TransferCommitted,
            TransferPhase.Committing, actor, null, ct).ConfigureAwait(false);
        pump.Nudge();
    }

    private async Task WithGateAsync(string transferId, Func<Task> body, CancellationToken ct)
    {
        SemaphoreSlim gate = _gates.GetOrAdd(transferId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await body().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private bool IsMeasuredRunning(string instanceId) =>
        cache.Statuses.TryGetValue(instanceId, out Reading<InstanceRuntimeStatus>? reading)
        && reading is { IsMeasured: true, Value.Status: true };

    private static IInstanceService? Engine(IServiceScope scope) =>
        scope.ServiceProvider.GetService<IInstanceService>();

    /// <summary>What an engine refusal on the source means to the target and the person.</summary>
    internal static TransferProblem EngineProblem(KgsmResult result, string instanceId)
    {
        string detail = string.IsNullOrWhiteSpace(result.Stderr) ? $"the engine refused (exit {result.ExitCode})" : result.Stderr.Trim();
        return result.ExitCode switch
        {
            EngineExit.InstanceRunning => new TransferProblem(409, TransferRefusalCodes.InstanceRunning, detail),
            TransferExitCodes.InstanceLocked => new TransferProblem(409, TransferRefusalCodes.InstanceLocked, detail),
            TransferExitCodes.NotFound => new TransferProblem(404, "not_found", $"no server '{instanceId}' on this node"),
            _ => new TransferProblem(409, "conflict", detail),
        };
    }

    /// <summary>The summary a person sees of a manifest.</summary>
    public static TransferSummary Summary(TransferManifest m, string? gameName) =>
        new(m.Instance.Id, m.Instance.DisplayName, m.Instance.Blueprint, m.Instance.Runtime,
            m.Parts.Sum(p => p.Bytes), gameName);

    private static string Hash(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket))).ToLowerInvariant();
}
