using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Leaves;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The metric-tick pump (M2): one monitor scrape per tick fans out to the per-server
/// (<c>servers/{id}/metrics</c>), roster (<c>servers/metrics</c>) and host (<c>hosts/{id}/metrics</c>)
/// topics. Continuous, not diffed — metrics are a tick feed and the client applies the latest (the
/// connection coalesces an unsent tick).
/// </summary>
/// <remarks>
/// <para><b>Gated:</b> scrapes only when some connection is subscribed to a <c>*/metrics</c> topic, so an
/// idle host never hits the monitor socket.</para>
/// <para><b>Two cadences off one scrape.</b> The per-server and host topics feed live charts and tick at
/// the monitor's own rate. The roster topic feeds card grids, where a figure that changes faster than
/// someone can read it buys nothing and costs every card a re-render, so it is published no more often
/// than <see cref="RosterIntervalMs"/> — never on a scrape of its own.</para>
/// <para><b>Honesty (invariant #1):</b> a null snapshot (monitor down/absent/not-ready) produces
/// <em>silence</em> — never a replayed stale frame. The "metrics went down" signal is the
/// <c>LeafHealthMonitor</c>'s <c>down</c> flip on <c>hosts/{id}/capabilities</c>; the metric topics simply go quiet.</para>
/// </remarks>
public sealed class MetricsPump(StreamHub hub, MonitorClient monitor, ApiOptions options, ILogger<MetricsPump> logger)
    : BackgroundService
{
    /// <summary>How often the roster frame goes out, at most. A card's readout, not a chart's sample
    /// feed: two seconds is faster than an operator can take a grid in, and it halves both the wire
    /// traffic and the client's render work against the chart cadence. Not a knob — the scrape cadence
    /// is the configurable one, and this rides it.</summary>
    private const int RosterIntervalMs = 2000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Configurable (Api__MetricsPollMs) but defaults to ~the monitor's own 1s self-tick: this is
        // the live resource feed, not the instance poll — keep it tight or the SPA's charts get choppy.
        string hostTopic = StreamProtocol.HostMetricsTopic(options.HostId);
        logger.LogInformation("metrics pump: started (interval={IntervalMs}ms — live monitor scrape, roster every {RosterMs}ms)",
            options.MetricsPollMs, RosterIntervalMs);
        long lastRoster = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.MetricsPollMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    if (!hub.AnySubscription(IsMetricsTopic)) continue;

                    Snap.Snapshot? snap = await monitor.GetLatestAsync(stoppingToken).ConfigureAwait(false);
                    if (snap is null) continue; // monitor down -> silence; the capability flip is the LeafHealthMonitor's job

                    if (hub.HasSubscribers(hostTopic))
                        hub.Publish(hostTopic, hostTopic,
                            new StreamMessage(hostTopic, StreamProtocol.HostMetrics, MetricsMapping.ToHostMetrics(snap)));

                    foreach (Snap.ServerMetrics sm in snap.Servers)
                    {
                        string topic = StreamProtocol.ServerMetricsTopic(sm.Id);
                        if (!hub.HasSubscribers(topic)) continue;
                        hub.Publish(topic, topic,
                            new StreamMessage(topic, StreamProtocol.MetricsTick, MetricsMapping.ToServerMetrics(sm)));
                    }

                    if (hub.HasSubscribers(StreamProtocol.ServersMetricsTopic)
                        && Environment.TickCount64 - lastRoster >= RosterIntervalMs)
                    {
                        lastRoster = Environment.TickCount64;
                        hub.Publish(StreamProtocol.ServersMetricsTopic, StreamProtocol.ServersMetricsEntityKey,
                            new StreamMessage(StreamProtocol.ServersMetricsTopic, StreamProtocol.MetricsRoster,
                                BuildRoster(snap)));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "metrics pump tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private static bool IsMetricsTopic(string topic) => topic.EndsWith("/metrics", StringComparison.Ordinal);

    /// <summary>
    /// One frame covering every instance the monitor knows of — the union of the instances it sampled and
    /// the instances it measured a footprint for. The union is the point: a stopped server appears in the
    /// second set only, with a null sample and a real disk figure, which is exactly what a card needs to
    /// show what it occupies without claiming a CPU reading nobody took.
    /// </summary>
    internal static ServerMetricsRoster BuildRoster(Snap.Snapshot snap)
    {
        Dictionary<string, Snap.ServerMetrics> byId = ServerAggregator.IndexMetrics(snap);
        Dictionary<string, long> diskById = ServerAggregator.IndexDiskBytes(snap);

        var rows = new List<ServerMetricsRow>(byId.Count + diskById.Count);
        foreach ((string id, Snap.ServerMetrics sm) in byId)
            rows.Add(new ServerMetricsRow(id, MetricsMapping.ToServerMetrics(sm),
                diskById.TryGetValue(id, out long bytes) ? bytes : sm.DiskBytes));

        foreach ((string id, long bytes) in diskById)
            if (!byId.ContainsKey(id))
                rows.Add(new ServerMetricsRow(id, null, bytes));

        return new ServerMetricsRoster(rows);
    }
}
