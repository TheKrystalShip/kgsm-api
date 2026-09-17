using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Lifecycle;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// When a leaf's report about itself is worth telling somebody, and what does and does not count as the
/// fault being over.
/// </summary>
public sealed class LeafDegradationWatcherTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 17, 4, 0, 0, TimeSpan.Zero);

    private const string Detail =
        "No router has answered UPnP for 15 minutes (discovery got no answer). Running servers' port " +
        "forwards cannot be opened or restored, so they are unreachable from the internet until it does.";

    private sealed class Recorder : INotificationBus
    {
        public List<NotificationEvent> Events { get; } = [];
        public void Publish(AuditRecord record) => throw new NotSupportedException();
        public void PublishDerived(NotificationEvent ev) => Events.Add(ev);
        public IAsyncEnumerable<NotificationEvent> ReadAllAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private IReadOnlyDictionary<string, LeafStateReport>? _reports =
        new Dictionary<string, LeafStateReport>(StringComparer.Ordinal);

    private readonly Recorder _bus = new();

    private LeafDegradationWatcher Watcher() =>
        new(() => _reports, _bus, NullLogger<LeafDegradationWatcher>.Instance, Started);

    private void Watchdog(LeafStateReport report) =>
        _reports = new Dictionary<string, LeafStateReport>(StringComparer.Ordinal) { ["kgsm-watchdog"] = report };

    private static LeafStateReport Degraded(DateTimeOffset since, string? detail = Detail) =>
        new([new LeafDegradation("upnp-router", detail, since)], [], []);

    private static LeafStateReport Recovered() => new([], ["upnp-router"], []);

    private static LeafStateReport Cleared() => new([], [], ["upnp-router"]);

    private static LeafStateReport Silent() => LeafStateReport.Empty;

    [Fact]
    public void A_fault_that_stands_past_the_dwell_is_announced_once_with_the_leafs_own_words()
    {
        LeafDegradationWatcher w = Watcher();
        DateTimeOffset t = Started.AddMinutes(45);
        Watchdog(Degraded(t));

        w.Tick(t);
        w.Tick(t.AddSeconds(30));
        w.Tick(t.AddSeconds(75));
        w.Tick(t.AddMinutes(30));

        NotificationEvent only = Assert.Single(_bus.Events);
        Assert.Equal("leaf_degraded", only.CatalogId);
        Assert.Equal(DerivedNotificationAction.LeafDegraded, only.Action);
        Assert.Equal(AuditSeverity.Warn, only.Severity);
        Assert.Equal($"Watchdog: {Detail}", only.Summary);
        Assert.Equal("watchdog", only.ActionSubject);
        Assert.Equal("leaf/watchdog/upnp-router", only.SubjectKey);
        Assert.Null(only.ServerId);
    }

    [Fact]
    public void A_fault_that_clears_inside_the_dwell_is_never_announced_and_neither_is_its_recovery()
    {
        LeafDegradationWatcher w = Watcher();
        DateTimeOffset t = Started.AddMinutes(45);

        Watchdog(Degraded(t));
        w.Tick(t);
        w.Tick(t.AddSeconds(30));
        Watchdog(Recovered());
        w.Tick(t.AddSeconds(45));
        w.Tick(t.AddMinutes(5));

        Assert.Empty(_bus.Events);
    }

    [Fact]
    public void The_recovery_the_leaf_reports_is_announced()
    {
        LeafDegradationWatcher w = Watcher();
        DateTimeOffset t = Started.AddMinutes(45);

        Watchdog(Degraded(t));
        w.Tick(t);
        w.Tick(t.AddMinutes(2));
        Watchdog(Recovered());
        w.Tick(t.AddHours(8));
        w.Tick(t.AddHours(9));

        Assert.Equal(["leaf_degraded", "leaf_recovered"], _bus.Events.Select(e => e.CatalogId));
        NotificationEvent recovered = _bus.Events[1];
        Assert.Equal(AuditSeverity.Success, recovered.Severity);
        Assert.Equal("Watchdog: upnp-router is working again.", recovered.Summary);
    }

    [Fact]
    public void A_restart_wiping_the_fault_is_not_a_recovery_and_the_fault_reported_again_is_news()
    {
        // The watchdog restarted mid-outage: its ready line wipes the slate while the router is still
        // silent, and fifteen minutes later the fresh process reports it again.
        LeafDegradationWatcher w = Watcher();
        DateTimeOffset t = Started.AddMinutes(45);

        Watchdog(Degraded(t));
        w.Tick(t);
        w.Tick(t.AddMinutes(2));
        Watchdog(Cleared());
        w.Tick(t.AddMinutes(10));
        Watchdog(Degraded(t.AddMinutes(25)));
        w.Tick(t.AddMinutes(25));
        w.Tick(t.AddMinutes(27));

        Assert.Equal(["leaf_degraded", "leaf_degraded"], _bus.Events.Select(e => e.CatalogId));
    }

    [Fact]
    public void A_fault_the_segment_stopped_mentioning_is_held_and_its_recovery_still_announced()
    {
        // Midnight: the new segment carries no trace of the fault, which is neither broken nor fixed as far
        // as the read can tell. The recovery written into the new segment is still the one worth hearing.
        LeafDegradationWatcher w = Watcher();
        DateTimeOffset t = Started.AddMinutes(45);

        Watchdog(Degraded(t));
        w.Tick(t);
        w.Tick(t.AddMinutes(2));
        Watchdog(Silent());
        w.Tick(t.AddHours(20));
        Watchdog(Recovered());
        w.Tick(t.AddHours(21));

        Assert.Equal(["leaf_degraded", "leaf_recovered"], _bus.Events.Select(e => e.CatalogId));
    }

    [Fact]
    public void A_held_fault_reported_afresh_later_is_announced_again()
    {
        LeafDegradationWatcher w = Watcher();
        DateTimeOffset t = Started.AddMinutes(45);

        Watchdog(Degraded(t));
        w.Tick(t);
        w.Tick(t.AddMinutes(2));
        Watchdog(Silent());
        w.Tick(t.AddHours(20));
        Watchdog(Degraded(t.AddHours(30)));
        w.Tick(t.AddHours(30));
        w.Tick(t.AddHours(30).AddMinutes(2));

        Assert.Equal(["leaf_degraded", "leaf_degraded"], _bus.Events.Select(e => e.CatalogId));
    }

    [Fact]
    public void A_fault_that_predates_this_process_is_adopted_but_its_recovery_is_announced()
    {
        // Redeploying the API during an outage must not announce the outage again; the process before it did.
        LeafDegradationWatcher w = Watcher();

        Watchdog(Degraded(Started.AddHours(-3)));
        w.Tick(Started.AddSeconds(15));
        w.Tick(Started.AddMinutes(10));
        Watchdog(Recovered());
        w.Tick(Started.AddMinutes(20));

        Assert.Equal(["leaf_recovered"], _bus.Events.Select(e => e.CatalogId));
    }

    [Fact]
    public void Nothing_is_decided_before_the_first_reading()
    {
        // A reading not yet taken is not a host with no faults: adopting from it would adopt nothing, and
        // every standing fault would then be announced the moment the real reading landed.
        LeafDegradationWatcher w = Watcher();
        _reports = null;

        w.Tick(Started.AddSeconds(15));
        Watchdog(Degraded(Started.AddHours(-3)));
        w.Tick(Started.AddSeconds(30));
        w.Tick(Started.AddMinutes(10));

        Assert.Empty(_bus.Events);
    }

    [Fact]
    public void A_fault_with_no_detail_names_the_component()
    {
        LeafDegradationWatcher w = Watcher();
        DateTimeOffset t = Started.AddMinutes(45);
        Watchdog(Degraded(t, detail: null));

        w.Tick(t);
        w.Tick(t.AddMinutes(2));

        Assert.Equal("Watchdog: upnp-router stopped working.", Assert.Single(_bus.Events).Summary);
    }

    [Fact]
    public void Both_events_are_in_the_catalog_a_person_can_switch_off()
    {
        Assert.True(NotificationCatalog.IsKnown("leaf_degraded"));
        Assert.True(NotificationCatalog.IsKnown("leaf_recovered"));
        Assert.True(NotificationCatalog.DefaultRule("leaf_degraded").Enabled);
    }
}
