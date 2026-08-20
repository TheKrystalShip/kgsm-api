using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Leaves;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The <c>servers</c>-topic pump (M2): reads the cached instance roster + run-state from
/// <see cref="InstanceCache"/> and pushes a full honest <see cref="Server"/> element
/// (<c>server.patch</c>) when an instance's status/roster/version changes, plus a
/// <c>server.removed</c> tombstone when an id leaves the roster. The client merges by id.
/// </summary>
/// <remarks>
/// <para><b>Cache-backed, no process spawns.</b> Unlike the original pump that spawned kgsm.sh
/// processes every tick, this version reads from the in-memory <see cref="InstanceCache"/> (a
/// lock-free reference read). The cache is updated by kgsm lifecycle events (real-time) and a
/// 60-second background refresh (authoritative reconciliation). The monitor scrape (a socket read,
/// not a process spawn) remains on-demand for metrics.</para>
/// <para><b>Status/roster, NOT the metric firehose.</b> Change-detection deliberately ignores the metrics
/// block (<see cref="CoreChanged"/>) — a per-second resource delta must not trigger a <c>server.patch</c>,
/// or this topic would double-stream the metrics that already flow on <c>servers/{id}/metrics</c> (the
/// frozen §6 topic split). The pushed element still carries its current metrics for a complete merge.</para>
/// <para><b>Prime, don't storm.</b> On the first active cycle (or after subscribers return) the baseline is
/// primed to current with no emit — the client already hydrated via REST (§3·j), so subscribing must not
/// replay the whole roster as patches.</para>
/// </remarks>
public sealed class DomainPump(
    StreamHub hub,
    InstanceCache cache,
    BackupCache backups,
    MonitorClient monitor,
    Services.Commands.JobRegistry jobs,
    Services.Players.PlayerHistoryService players,
    Services.Players.PlayerObservability observability,
    Services.Availability.UpdateLagIndex updateLag,
    Services.Availability.RunTimesIndex runTimes,
    ApiOptions options,
    ILogger<DomainPump> logger)
    : BackgroundService
{
    // Raised when something has just changed a server's state and the next tick is too late to say so.
    // Capacity one: several changes arriving together need one pass, not one each — the pass diffs the
    // whole roster anyway.
    private readonly SemaphoreSlim _nudge = new(0, 1);

    /// <summary>
    /// Run a diff pass now rather than at the next tick. Called when the engine has just announced a
    /// run-state change (<see cref="Services.Audit.KgsmAuditConsumer"/>), because a poll interval of
    /// silence right then reads as the PREVIOUS state — a server stopped from the CLI keeps saying
    /// "online" for a moment, which is the one moment someone is watching it. Everything else still
    /// rides the interval; this only moves the announcement earlier, and never publishes anything the
    /// tick wouldn't have.
    /// </summary>
    public void Nudge()
    {
        try { _nudge.Release(); }
        catch (SemaphoreFullException) { /* a pass is already pending — that pass covers this too */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Configurable (Api__DomainPollMs, default 5s): reads are now free (cache reference),
        // but the interval still bounds how quickly out-of-band changes (caught by the background
        // refresh or events) reach subscribed clients.
        TimeSpan interval = TimeSpan.FromMilliseconds(options.DomainPollMs);
        logger.LogInformation("domain pump: started (interval={IntervalMs}ms — cache-backed, no process spawns)",
            options.DomainPollMs);

        Dictionary<string, Server> last = new(StringComparer.Ordinal);
        bool primed = false;

        using var timer = new PeriodicTimer(interval);
        try
        {
            // The tick task is held across iterations because a PeriodicTimer allows only one waiter:
            // a nudge-driven pass leaves it pending and the next iteration waits on the same one, so
            // the interval keeps its own cadence instead of restarting on every nudge.
            Task<bool> tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            while (!stoppingToken.IsCancellationRequested)
            {
                using (var pending = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    Task nudged = _nudge.WaitAsync(pending.Token);
                    if (await Task.WhenAny(tick, nudged).ConfigureAwait(false) == tick)
                    {
                        if (!await tick.ConfigureAwait(false))
                            break; // the timer was disposed — shutting down
                        tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                        // Cancel the waiter we are abandoning: left queued, it would consume the next
                        // Nudge and nobody would act on it.
                        await pending.CancelAsync().ConfigureAwait(false);
                    }
                }

                try
                {
                    if (!hub.HasSubscribers(StreamProtocol.ServersTopic))
                    {
                        primed = false;
                        last.Clear();
                        continue;
                    }

                    // Distinguish a FAILED engine read from a genuinely empty roster: on a failure the
                    // cache has EngineRead=false, and diffing [] against `last` would tombstone EVERY
                    // instance (a mass server.removed). Skip the tick entirely: keep `last` + `primed`,
                    // retry next interval.
                    if (!cache.EngineRead)
                    {
                        logger.LogDebug("domain pump: engine read failed; skipping tick (keeping last-known roster)");
                        continue;
                    }

                    // Read from the cache (lock-free reference swap) + monitor socket concurrently.
                    IReadOnlyDictionary<string, TheKrystalShip.KGSM.Core.Models.Instance> roster = cache.Roster;
                    IReadOnlyDictionary<string, TheKrystalShip.KGSM.Core.Models.Reading<TheKrystalShip.KGSM.Core.Models.InstanceRuntimeStatus>> statuses = cache.Statuses;
                    Task<Snap.Snapshot?> snapshotTask = monitor.GetLatestAsync(stoppingToken);
                    Task observabilityTask = observability.RefreshIfStaleAsync(stoppingToken);
                    await Task.WhenAll(snapshotTask, observabilityTask).ConfigureAwait(false);

                    Dictionary<string, Snap.ServerMetrics> metricsById = ServerAggregator.IndexMetrics(snapshotTask.Result);
                    Dictionary<string, long> diskById = ServerAggregator.IndexDiskBytes(snapshotTask.Result);
                    Func<string, int?> onlinePlayers = ServerAggregator.OnlinePlayersOf(players, observability);
                    Func<string, DateTimeOffset?> updateSince = updateLag.Lookup;
                    // The same run clock the REST join uses. Omitting it here would blank startedAt on every
                    // streamed patch, so a card would show an uptime until the first patch arrived and then
                    // lose it.
                    Func<string, Services.Availability.RunTimes> runClock = runTimes.Lookup;

                    // Build the current server list from cache data.
                    var byId = new Dictionary<string, Server>(StringComparer.Ordinal);
                    foreach ((string id, var instance) in roster)
                        byId[id] = ServerAggregator.BuildServer(id, instance, statuses,
                            backups.Readings, metricsById, options.HostId, cache.IsStarting, jobs.InFlightFor,
                            diskById, onlinePlayers, updateSince, runClock);

                    if (!primed)
                    {
                        last = byId;
                        primed = true;
                        continue; // hydrate-via-REST on subscribe; no patch storm
                    }

                    // Additions + status/roster changes -> a full-element patch (merge by id).
                    foreach ((string id, Server s) in byId)
                        if (!last.TryGetValue(id, out Server? prev) || CoreChanged(prev, s))
                            hub.Publish(StreamProtocol.ServersTopic, StreamProtocol.ServerEntityKey(id),
                                new StreamMessage(StreamProtocol.ServersTopic, StreamProtocol.ServerPatch, s));

                    // Removals -> a tombstone.
                    foreach (string id in last.Keys)
                        if (!byId.ContainsKey(id))
                            hub.Publish(StreamProtocol.ServersTopic, StreamProtocol.ServerEntityKey(id),
                                new StreamMessage(StreamProtocol.ServersTopic, StreamProtocol.ServerRemoved, new ServerRemoved(id)));

                    last = byId;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "domain pump tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    // Ignores the metrics block on purpose — see the class remarks (status/roster, not the metric firehose).
    // The update fields (UpdateAvailable/LatestVersion/UpdateCheckedAt) ride along: a flip happens when the
    // scheduler's sweep finds a new build, which is hourly at most and usually far rarer, so carrying it
    // here never turns the `servers` topic into a firehose — it is the status/roster cadence, not the
    // metric one.
    // The note rides here for the same reason as the update fields: an operator edits it by hand, so
    // a flip is rare, and carrying it means an edit made in one browser reaches every other open panel
    // without a refresh. `record` equality compares the note's body + attribution structurally.
    // The backup fields ride here on the same reasoning: a backup is taken or restored rarely, so a flip
    // costs nothing on this topic, and carrying it is what lets a backup taken from the CLI (or by another
    // operator) reach every open panel — the SPA's backup KPIs update without a refresh. `record` equality
    // compares the whole manifest record structurally, so a re-scan that changes nothing emits nothing.
    // The active job rides here too, and it is the reason a surface can show an instance as busy for the
    // whole of a long operation: an update that starts or finishes flips this field, so the transition
    // reaches every open panel on the servers topic, not just the client that happened to be listening to
    // `jobs` when the command was issued. Its cadence is the operation's, not the metric firehose's.
    // The online player count rides here even though it is a number that moves, because what moves it is a
    // person joining or leaving — a cadence of a handful a day on a busy server, nothing like the metric
    // firehose. Carrying it is what lets a fleet total on a dashboard be live without every card fetching
    // its own roster, and what keeps that total agreeing with the cards beside it.
    // DiskBytes is absent for the same reason as the metrics block: it is a measurement, and a footprint
    // that ticks up as a world saves would publish the whole roster on this topic. The element pushed on a
    // real change still carries it, and the roster metric frame is what keeps it live.
    private static bool CoreChanged(Server a, Server b) =>
        a.Status != b.Status || a.Version != b.Version || a.Name != b.Name
        || a.Blueprint != b.Blueprint || a.Runtime != b.Runtime
        || a.UpdateAvailable != b.UpdateAvailable
        || a.LatestVersion != b.LatestVersion
        || a.UpdateCheckedAt != b.UpdateCheckedAt
        // Rides here so the age a card shows moves with the flag it qualifies: the index resolves the
        // notice a few minutes after the sweep that raised UpdateAvailable, and without this the patch
        // carrying the flag would land with the age still null and stay that way until the next reload.
        || a.UpdateAvailableSince != b.UpdateAvailableSince
        || a.Note != b.Note
        || a.LastBackup != b.LastBackup
        || a.BackupCount != b.BackupCount
        || a.ActiveJob != b.ActiveJob
        || a.OnlinePlayers != b.OnlinePlayers;
}
