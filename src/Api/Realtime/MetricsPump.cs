using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Leaves;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The metric-tick pump (M2): one monitor scrape per tick fans out to both the per-server
/// (<c>servers/{id}/metrics</c>) and host (<c>hosts/{id}/metrics</c>) topics. Continuous, not diffed —
/// metrics are a tick feed and the client applies the latest (the connection coalesces an unsent tick).
/// </summary>
/// <remarks>
/// <para><b>Gated:</b> scrapes only when some connection is subscribed to a <c>*/metrics</c> topic, so an
/// idle host never hits the monitor socket.</para>
/// <para><b>Honesty (invariant #1):</b> a null snapshot (monitor down/absent/not-ready) produces
/// <em>silence</em> — never a replayed stale frame. The "metrics went down" signal is the
/// <c>LeafHealthMonitor</c>'s <c>down</c> flip on <c>hosts/{id}/capabilities</c>; the metric topics simply go quiet.</para>
/// </remarks>
public sealed class MetricsPump(StreamHub hub, MonitorClient monitor, ApiOptions options, ILogger<MetricsPump> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1); // ~the monitor's own self-tick cadence

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string hostTopic = StreamProtocol.HostMetricsTopic(options.HostId);
        using var timer = new PeriodicTimer(Interval);
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
}
