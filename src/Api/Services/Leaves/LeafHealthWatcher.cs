using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Integrations;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Tells somebody when a KGSM service on this host stops answering, and when it starts again.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads <see cref="LeafHealthMonitor"/>, which is always on.</b> The Services board shows the same
/// flips, but its pump is subscriber-gated — it goes idle when nobody is looking at the panel, which is
/// exactly the situation this exists for. The capability probe runs regardless, so this only has to watch
/// its cached answer.
/// </para>
/// <para>
/// <b>The dwell is what separates a fault from a deploy.</b> Delivering a leaf restarts it, and a service
/// that is down for four seconds while its new binary lands is not news. <see cref="DownFor"/> is
/// comfortably longer than that, which costs a real outage a minute of notice and is worth it: a channel
/// that pages on every deploy gets switched off, and then it reports nothing at all.
/// </para>
/// <para>
/// <b>Only <c>down</c> counts, never <c>unknown</c>.</b> A capability that has not been probed yet — this
/// process having just started, say — is not a leaf that failed, and treating the two alike would announce
/// an outage every time the API itself is redeployed.
/// </para>
/// <para>
/// <b>Two leaves are deliberately unwatchable, and are not silently reported healthy.</b> The firewall is
/// socket-activated and idle-exits, so <c>inactive</c> is its resting state rather than a fault; the
/// Discord bot serves no health endpoint this API polls. Neither has a signal to report, so neither is
/// reported — inventing one is the alternative, and it would be a lie in whichever direction it was set.
/// </para>
/// </remarks>
public sealed class LeafHealthWatcher : BackgroundService
{
    private readonly Func<HostCapabilities> _read;
    private readonly INotificationBus _bus;
    private readonly ILogger<LeafHealthWatcher> _logger;

    public LeafHealthWatcher(LeafHealthMonitor health, INotificationBus bus, ILogger<LeafHealthWatcher> logger)
        : this(() => health.Current, bus, logger) { }

    /// <summary>
    /// The reading is taken through a function so the dwell can be exercised against a clock and a
    /// scripted sequence of capability blocks. What this class decides — the difference between a deploy
    /// and an outage — is exactly the part that cannot be proved by watching a real host for a minute.
    /// </summary>
    internal LeafHealthWatcher(
        Func<HostCapabilities> read, INotificationBus bus, ILogger<LeafHealthWatcher> logger)
    {
        _read = read;
        _bus = bus;
        _logger = logger;
    }

    /// <summary>How long a leaf has to be reported down before it is worth saying so. Long enough that a
    /// deploy's restart passes unremarked.</summary>
    internal static readonly TimeSpan DownFor = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    // leaf id -> when it was first seen down. Only the timer loop touches these.
    private readonly Dictionary<string, DateTimeOffset> _downSince = new(StringComparer.Ordinal);
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { Tick(DateTimeOffset.UtcNow); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "leaf-health watch tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    internal void Tick(DateTimeOffset now)
    {
        HostCapabilities caps = _read();

        foreach (LeafDescriptor leaf in LeafCatalog.Default)
        {
            Capability? capability = CapabilityFor(leaf, caps);

            // No probe, not provisioned, or not yet probed: nothing measured, so nothing said.
            if (capability is null || !capability.Provisioned || capability.Status == CapabilityStatus.Unknown)
            {
                Forget(leaf.Id);
                continue;
            }

            if (capability.Status != CapabilityStatus.Down)
            {
                // Back up. Only worth announcing to somebody who was told it went down in the first place.
                if (_announced.Remove(leaf.Id))
                    Announce(leaf, up: true, now, TimeSpan.Zero);
                _downSince.Remove(leaf.Id);
                continue;
            }

            if (!_downSince.TryGetValue(leaf.Id, out DateTimeOffset since))
            {
                _downSince[leaf.Id] = now;
                continue;
            }

            TimeSpan been = now - since;
            if (been < DownFor || !_announced.Add(leaf.Id)) continue;

            Announce(leaf, up: false, now, been);
        }
    }

    private void Announce(LeafDescriptor leaf, bool up, DateTimeOffset now, TimeSpan been)
    {
        string summary = up
            ? $"{leaf.DisplayName} is answering again"
            : $"{leaf.DisplayName} has not answered for {Math.Round(been.TotalMinutes):n0} minute(s)";

        _logger.LogWarning("leaf health: {Summary} ({Unit})", summary, leaf.Unit);

        _bus.PublishDerived(new NotificationEvent(
            CatalogId: up ? "leaf_up" : "leaf_down",
            Action: up ? DerivedNotificationAction.LeafUp : DerivedNotificationAction.LeafDown,
            // A leaf is not a game server, and putting its id in the server slot would send a tap to a
            // server page that does not exist.
            ServerId: null,
            Severity: up ? AuditSeverity.Success : AuditSeverity.Danger,
            Summary: summary,
            Ts: now,
            AuditId: "",
            // Each leaf is its own subject: two failing at once are two facts, and a window keyed on
            // anything coarser would report only the first.
            SubjectKey: $"leaf/{leaf.Id}",
            ActionSubject: leaf.Id));
    }

    private void Forget(string leafId)
    {
        _downSince.Remove(leafId);
        _announced.Remove(leafId);
    }

    private static Capability? CapabilityFor(LeafDescriptor leaf, HostCapabilities caps) => leaf.Health switch
    {
        LeafHealthSource.Metrics => caps.Metrics,
        LeafHealthSource.Assistant => caps.Assistant,
        LeafHealthSource.Watchdog => caps.Watchdog,
        LeafHealthSource.Scheduler => caps.Scheduler,
        // SelfApi answers by definition whenever this code runs, and None has no probe at all.
        _ => null,
    };
}
