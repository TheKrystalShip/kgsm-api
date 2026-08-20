using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Availability;

/// <summary>
/// When each supervised instance's current run began and its last run ended — the two timestamps behind
/// <see cref="Contracts.Server.StartedAt"/> and <see cref="Contracts.Server.StoppedAt"/>.
/// </summary>
/// <param name="SpawnedAt">
/// When the running process was spawned, or null when nothing is running and when the daemon adopted a
/// cgroup it did not spawn.
/// </param>
/// <param name="LastExitedAt">When the last recorded run ended, or null when no run is on record.</param>
public readonly record struct RunTimes(DateTimeOffset? SpawnedAt, DateTimeOffset? LastExitedAt);

/// <summary>
/// The watchdog's run clock, cached for the roster join.
/// </summary>
/// <remarks>
/// <para><b>Why the watchdog and not the engine.</b> kgsm dates a run from a local pid file, and a native
/// instance the watchdog spawned has none — the process lives in a cgroup the daemon owns, so the engine's
/// <c>start_time</c> is null for exactly the instances this host runs. The watchdog is already the run-state
/// authority for a native instance (<c>system-architecture.md §4</c>), and it holds both timestamps durably:
/// the spawn time beside the persisted phase, and the last exit in its run ledger. Asking the authority for
/// the clock keeps run-state and run-duration answered by one source that cannot disagree with itself.</para>
/// <para><b>Containers are not covered here</b> and do not need to be: Docker reports a start time that
/// kgsm passes through as ISO-UTC, so <see cref="Aggregation.ServerAggregator"/> keeps using the engine's
/// reading for those and consults this index only where the engine has nothing.</para>
/// <para><b>Why it is cached.</b> The roster is rebuilt on every read and on a 60s pump; a socket round trip
/// per read would put the daemon in the path of <c>GET /servers</c>. The values are per-run constants — a
/// timestamp does not age, only the duration a surface derives from it does — so a poll measured in seconds
/// is exact between runs and at worst names a new run one cadence late, while the instance is reporting
/// "starting" anyway.</para>
/// <para><b>A failed poll keeps the last map</b> rather than blanking it. A run that began at T began at T
/// whether or not the daemon can be reached right now, and dropping the figure would turn a reachability
/// problem into a claim that the run has no start.</para>
/// </remarks>
public sealed class RunTimesIndex(
    IServiceProvider services,
    ILogger<RunTimesIndex> logger) : BackgroundService
{
    /// <summary>How often the daemon is asked. Fast enough that a fresh run is dated promptly, slow enough
    /// that the socket is not in the read path.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>Bounds one poll, so an unresponsive daemon cannot stall the loop.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private volatile IReadOnlyDictionary<string, RunTimes> _times =
        new Dictionary<string, RunTimes>(StringComparer.Ordinal);

    /// <summary>
    /// The run clock for <paramref name="instanceId"/>. Both halves are null for an instance the daemon
    /// does not track, which is the honest unknown — never a fabricated start.
    /// </summary>
    public RunTimes For(string instanceId) =>
        _times.TryGetValue(instanceId, out RunTimes t) ? t : default;

    /// <summary>
    /// A plain-function accessor for the roster join, matching the <c>onlinePlayers</c> and
    /// <c>updateAvailableSince</c> pattern in <see cref="Aggregation.ServerAggregator"/> — the builder stays
    /// free of this service, and a stream pump can compose the identical rule.
    /// </summary>
    public Func<string, RunTimes> Lookup => For;

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
                logger.LogDebug(ex, "run-time refresh failed; keeping the previous readings");
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

        IReadOnlyList<WatchdogRunTimes> rows;
        try
        {
            // Deliberately NOT ListAsync: an instance leaves the daemon's supervised table when it stops,
            // so a list walk reports nothing for exactly the instances a stop time is wanted for. This call
            // unions that table with the durable run ledger.
            rows = await watchdog.GetRunTimesAsync(timed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("watchdog run-time read timed out after {Timeout}; keeping the previous readings", ProbeTimeout);
            return;
        }

        var next = new Dictionary<string, RunTimes>(rows.Count, StringComparer.Ordinal);
        foreach (WatchdogRunTimes r in rows)
        {
            if (string.IsNullOrEmpty(r.Name))
                continue;
            next[r.Name] = new RunTimes(AsUtc(r.SpawnedAt), AsUtc(r.LastExitedAt));
        }

        _times = next;
    }

    /// <summary>
    /// A daemon timestamp as an offset. The watchdog stamps UTC and serializes it with its kind, so a value
    /// that arrives Unspecified carries an unknown offset — read as UTC rather than as this machine's local
    /// zone, which would silently shift a run by the host's offset.
    /// </summary>
    private static DateTimeOffset? AsUtc(DateTime? value)
    {
        if (value is not { } v)
            return null;

        return v.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(v),
            DateTimeKind.Local => new DateTimeOffset(v).ToUniversalTime(),
            _ => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)),
        };
    }
}
