using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The <c>servers</c>-topic pump (M2): polls the kgsm-lib domain ⋈ monitor join and pushes a full honest
/// <c>Server</c> element (<c>server.patch</c>) when an instance's status/roster/version changes, plus a
/// <c>server.removed</c> tombstone when an id leaves the roster. The client merges by id.
/// </summary>
/// <remarks>
/// <para><b>Status/roster, NOT the metric firehose.</b> Change-detection deliberately ignores the metrics
/// block (<see cref="CoreChanged"/>) — a per-second resource delta must not trigger a <c>server.patch</c>,
/// or this topic would double-stream the metrics that already flow on <c>servers/{id}/metrics</c> (the
/// frozen §6 topic split). The pushed element still carries its current metrics for a complete merge.</para>
/// <para><b>Prime, don't storm.</b> On the first active cycle (or after subscribers return) the baseline is
/// primed to current with no emit — the client already hydrated via REST (§3·j), so subscribing must not
/// replay the whole roster as patches. The slow kgsm-lib poll (process spawns) runs only while subscribed.</para>
/// </remarks>
public sealed class DomainPump(StreamHub hub, ServerAggregator servers, ILogger<DomainPump> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3); // status changes are rare; the poll spawns kgsm.sh

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Dictionary<string, Server> last = new(StringComparer.Ordinal);
        bool primed = false;

        using var timer = new PeriodicTimer(Interval);
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

                    IReadOnlyList<Server> current = await servers.GetServersAsync(stoppingToken).ConfigureAwait(false);
                    var byId = new Dictionary<string, Server>(StringComparer.Ordinal);
                    foreach (Server s in current) byId[s.Id] = s;

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
    private static bool CoreChanged(Server a, Server b) =>
        a.Status != b.Status || a.Version != b.Version || a.Name != b.Name
        || a.Blueprint != b.Blueprint || a.Runtime != b.Runtime;
}
