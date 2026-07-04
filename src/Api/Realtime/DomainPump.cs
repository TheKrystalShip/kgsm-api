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
    MonitorClient monitor,
    ApiOptions options,
    ILogger<DomainPump> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Configurable (KGSM_API_DOMAIN_POLL_MS, default 5s): reads are now free (cache reference),
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
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
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
                    await snapshotTask.ConfigureAwait(false);

                    Dictionary<string, Snap.ServerMetrics> metricsById = IndexMetrics(snapshotTask.Result);

                    // Build the current server list from cache data.
                    var byId = new Dictionary<string, Server>(StringComparer.Ordinal);
                    foreach ((string id, var instance) in roster)
                        byId[id] = ServerAggregator.BuildServer(id, instance, statuses, metricsById, options.HostId, cache.IsStarting);

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

    // Index per-instance metrics by id (the monitor guarantees unique ids per tick).
    private static Dictionary<string, Snap.ServerMetrics> IndexMetrics(Snap.Snapshot? snapshot)
    {
        Dictionary<string, Snap.ServerMetrics> metricsById = new(StringComparer.Ordinal);
        if (snapshot is not null)
            foreach (Snap.ServerMetrics sm in snapshot.Servers)
                metricsById[sm.Id] = sm;
        return metricsById;
    }

    // Ignores the metrics block on purpose — see the class remarks (status/roster, not the metric firehose).
    private static bool CoreChanged(Server a, Server b) =>
        a.Status != b.Status || a.Version != b.Version || a.Name != b.Name
        || a.Blueprint != b.Blueprint || a.Runtime != b.Runtime;
}
