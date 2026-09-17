using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Lifecycle;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Tells somebody when a KGSM service on this host reports that part of its job stopped working, and when
/// it reports that part working again.
/// </summary>
/// <remarks>
/// <para>
/// <b>The notifying half of what <see cref="LeafDegradationTracker"/> reads.</b> A leaf that answers its
/// health check while unable to do part of its job is shown degraded on the Services board, and the board
/// is only read by somebody already looking. The case that needs this is the watchdog's router component:
/// a router whose UPnP service goes silent leaves every server running and unreachable from the internet,
/// and the only place that was visible was the router's own table.
/// </para>
/// <para>
/// <b>What the leaf wrote is the message.</b> A degradation carries the leaf's own sentence about what is
/// wrong, so the notification says what the leaf knows rather than a component id somebody has to look up.
/// </para>
/// <para>
/// <b>A fault leaving the journal is announced as recovered only when the leaf said so.</b> A resident
/// leaf starting again wipes its slate while the fault may still be true, and a segment rolling over at
/// midnight stops mentioning it; neither is a recovery. A fault a restart wiped is forgotten quietly, so
/// the next report of it is news again. One the segment simply stopped mentioning is held, because the
/// recovery that does arrive is written into the new segment and is still worth hearing — and a report of
/// it dated after the one announced is a new fault, announced as one.
/// </para>
/// <para>
/// <b>The dwell keeps a flap quiet</b>, the same way <see cref="LeafHealthWatcher"/>'s does for a deploy: a
/// fault reported and recovered inside <see cref="DegradedFor"/> is never announced, and so neither is its
/// recovery.
/// </para>
/// <para>
/// <b>A fault that predates this process is adopted, not announced.</b> Redeploying this API during an
/// outage would otherwise announce it again every time. Its recovery is still announced — the previous
/// process told somebody it broke. The cost is a fault reported in the seconds this API was down, which
/// the Services board still shows.
/// </para>
/// </remarks>
public sealed class LeafDegradationWatcher : BackgroundService
{
    /// <summary>How long a fault must stay reported before it is announced.</summary>
    internal static readonly TimeSpan DegradedFor = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly Func<IReadOnlyDictionary<string, LeafStateReport>?> _read;
    private readonly INotificationBus _bus;
    private readonly ILogger<LeafDegradationWatcher> _logger;
    private readonly DateTimeOffset _startedAt;

    // Keyed by (producer, component). Only the timer loop touches these. An announced fault keeps the
    // timestamp of the report it announced, so a later report of the same component — after a restart the
    // journal no longer shows, say — is recognised as news rather than as the one already told.
    private readonly Dictionary<(string Producer, string Component), DateTimeOffset> _seenSince = [];
    private readonly Dictionary<(string Producer, string Component), DateTimeOffset?> _announced = [];
    private bool _adopted;

    public LeafDegradationWatcher(
        LeafDegradationTracker tracker, INotificationBus bus, ILogger<LeafDegradationWatcher> logger)
        : this(() => tracker.Reports, bus, logger, DateTimeOffset.UtcNow) { }

    /// <summary>
    /// The reading is taken through a function so the rules can be exercised against scripted journal
    /// reports and a clock — the difference between a recovery and a restart is exactly what cannot be
    /// proved by watching a real host.
    /// </summary>
    internal LeafDegradationWatcher(
        Func<IReadOnlyDictionary<string, LeafStateReport>?> read,
        INotificationBus bus,
        ILogger<LeafDegradationWatcher> logger,
        DateTimeOffset startedAt)
    {
        _read = read;
        _bus = bus;
        _logger = logger;
        _startedAt = startedAt;
    }

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
                    _logger.LogDebug(ex, "leaf-degradation watch tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    internal void Tick(DateTimeOffset now)
    {
        // No reading taken yet. Acting on "nothing" would adopt nothing and then announce every standing
        // fault on the host the moment the first reading lands.
        if (_read() is not { } reports)
            return;

        var current = new Dictionary<(string, string), LeafDegradation>();
        foreach ((string producer, LeafStateReport report) in reports)
        {
            foreach (LeafDegradation d in report.Degraded)
                current[(producer, d.Component)] = d;
        }

        if (!_adopted)
        {
            _adopted = true;
            foreach (((string, string) key, LeafDegradation d) in current)
            {
                if (d.Since is { } since && since < _startedAt)
                    _announced[key] = d.Since;
            }
        }

        foreach (((string Producer, string Component) key, LeafDegradation d) in current)
        {
            if (_announced.TryGetValue(key, out DateTimeOffset? told))
            {
                if (told is null || d.Since is null || d.Since <= told)
                    continue;

                _announced.Remove(key);
            }

            if (!_seenSince.TryGetValue(key, out DateTimeOffset since))
            {
                _seenSince[key] = now;
                continue;
            }

            if (now - since < DegradedFor)
                continue;

            _announced[key] = d.Since;
            _seenSince.Remove(key);
            Announce(key.Producer, key.Component, d.Detail, degraded: true, now);
        }

        foreach ((string, string) key in _seenSince.Keys.Where(k => !current.ContainsKey(k)).ToList())
            _seenSince.Remove(key);

        foreach ((string Producer, string Component) key in _announced.Keys.Where(k => !current.ContainsKey(k)).ToList())
        {
            // A producer whose journal was not read this time says nothing either way.
            if (!reports.TryGetValue(key.Producer, out LeafStateReport? report))
                continue;

            if (report.Recovered.Contains(key.Component))
            {
                _announced.Remove(key);
                Announce(key.Producer, key.Component, detail: null, degraded: false, now);
            }
            else if (report.Cleared.Contains(key.Component))
            {
                _announced.Remove(key);
            }
        }
    }

    private void Announce(string producer, string component, string? detail, bool degraded, DateTimeOffset now)
    {
        string leafId = LeafIdOf(producer);
        string name = LeafCatalog.Find(leafId)?.DisplayName ?? producer;

        string summary = degraded
            ? $"{name}: {(string.IsNullOrWhiteSpace(detail) ? $"{component} stopped working." : detail)}"
            : $"{name}: {component} is working again.";

        _logger.LogWarning("leaf degradation: {Summary} ({Producer}/{Component})", summary, producer, component);

        _bus.PublishDerived(new NotificationEvent(
            CatalogId: degraded ? "leaf_degraded" : "leaf_recovered",
            Action: degraded ? DerivedNotificationAction.LeafDegraded : DerivedNotificationAction.LeafRecovered,
            // A leaf is not a game server, and putting its id in the server slot would send a tap to a
            // server page that does not exist.
            ServerId: null,
            Severity: degraded ? AuditSeverity.Warn : AuditSeverity.Success,
            Summary: summary,
            Ts: now,
            AuditId: "",
            // Each component is its own subject: a leaf broken two ways is two facts, and a window keyed on
            // the leaf alone would report only the first.
            SubjectKey: $"leaf/{leafId}/{component}",
            ActionSubject: leafId));
    }

    /// <summary>The Control Panel's unprefixed leaf id for a producer, or the producer id when it has none.</summary>
    private static string LeafIdOf(string producer) =>
        producer.Length > JournalProducer.EcosystemPrefix.Length
        && producer.StartsWith(JournalProducer.EcosystemPrefix, StringComparison.Ordinal)
            ? producer[JournalProducer.EcosystemPrefix.Length..]
            : producer;
}
