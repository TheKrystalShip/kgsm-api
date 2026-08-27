using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Availability;

/// <summary>
/// Which supervision phase the watchdog holds each native instance in, cached for the roster join.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists for the one phase a run-state boolean cannot express.</b> A parked instance is stopped —
/// the process is gone, so the engine's status reading is false — and it is stopped on purpose, for a
/// span somebody asked for, with the watchdog's intent still set to running and crash detection
/// suppressed. Reading that as <c>stopped</c> would report a server as down when nothing is wrong and
/// nobody asked for it to be down, which is the same class of fabrication as inventing a metric.
/// </para>
/// <para>
/// <b>Nothing else is derived from a phase here.</b> Running, stopped and crashed all have authorities
/// that already answer them — the engine's run-state façade and the alert feed — and a second opinion
/// about whether a server is up would eventually disagree with the first.
/// </para>
/// <para>
/// <b>Containers are absent and that is correct</b>: the watchdog supervises native instances alone, so
/// there is no container to park and nothing to look up.
/// </para>
/// <para>
/// <b>A failed poll keeps the last map</b> rather than blanking it, the same posture as the run clock: a
/// park that began does not end because the daemon could not be reached, and dropping it would turn a
/// reachability problem into a claim that a server is down.
/// </para>
/// </remarks>
public sealed class SupervisionPhaseIndex(
    IServiceProvider services,
    ILogger<SupervisionPhaseIndex> logger) : BackgroundService
{
    /// <summary>The phase the daemon reports for an instance it is holding out of service.</summary>
    public const string MaintenancePhase = "maintenance";

    /// <summary>How often the daemon is asked. Fast enough that a park is visible within a poll, slow
    /// enough that the socket is not in the read path.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>Bounds one poll, so an unresponsive daemon cannot stall the loop.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private volatile IReadOnlyDictionary<string, string> _phases =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether the watchdog is holding <paramref name="instanceId"/> out of service for maintenance.
    /// False for an instance the daemon does not track and on a host with no daemon — an unasked question
    /// is not a yes.
    /// </summary>
    public bool IsParked(string instanceId) =>
        _phases.TryGetValue(instanceId, out string? phase)
        && string.Equals(phase, MaintenancePhase, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A plain-function accessor for the roster join, matching the <c>runTimes</c> and
    /// <c>updateAvailableSince</c> pattern in <see cref="Aggregation.ServerAggregator"/> — the builder
    /// stays free of this service, and a stream pump can compose the identical rule.
    /// </summary>
    public Func<string, bool> ParkedLookup => IsParked;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "supervision-phase refresh failed; keeping the previous readings");
            }

            try { await Task.Delay(RefreshInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        // Registered only when the watchdog is provisioned (see Startup); resolve optionally so a host
        // without one simply reports nothing rather than failing.
        if (services.GetService(typeof(IWatchdogClient)) is not IWatchdogClient watchdog)
            return;

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ProbeTimeout);

        IReadOnlyList<WatchdogInstanceState> table;
        try
        {
            table = await watchdog.ListAsync(timed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("watchdog supervision read timed out after {Timeout}; keeping the previous readings", ProbeTimeout);
            return;
        }

        var next = new Dictionary<string, string>(table.Count, StringComparer.Ordinal);
        foreach (WatchdogInstanceState state in table)
        {
            if (string.IsNullOrEmpty(state.Name) || string.IsNullOrWhiteSpace(state.Phase))
                continue;
            next[state.Name] = state.Phase;
        }

        _phases = next;
    }
}
