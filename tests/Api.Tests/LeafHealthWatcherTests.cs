using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Which leaves this API is willing to restart, and which capability answers for which leaf.
/// <para>
/// The set is derived rather than listed, so the thing worth pinning is the derivation: a leaf added to
/// the catalog has to become restartable without anybody remembering a second list, and this API has to
/// stay out of its own.
/// </para>
/// </summary>
public sealed class LeafRestartabilityTests
{
    [Theory]
    [InlineData("watchdog")]
    [InlineData("monitor")]
    [InlineData("assistant")]
    [InlineData("firewall")]
    [InlineData("speech")]
    [InlineData("scheduler")]
    [InlineData("bot")]
    public void Every_leaf_on_this_host_can_be_restarted(string leafId) =>
        Assert.True(LeafCatalog.IsRestartable(leafId));

    [Fact]
    public void Except_this_one()
    {
        // Restarting this service kills the request doing the restarting, so the reply never arrives and
        // the caller cannot tell it from a failure. Redeploying is how this one is restarted.
        Assert.False(LeafCatalog.IsRestartable(LeafCatalog.SelfId));
        Assert.False(LeafCatalog.IsRestartable("api"));
    }

    [Fact]
    public void And_nothing_this_host_does_not_run()
    {
        Assert.False(LeafCatalog.IsRestartable("kgsm-web"));
        Assert.False(LeafCatalog.IsRestartable(""));
        Assert.False(LeafCatalog.IsRestartable(null));
    }

    [Fact]
    public void The_restartable_set_is_derived_from_the_catalog_not_listed_again()
    {
        // A leaf joining the catalog becomes restartable with no second edit — which is the point, and
        // the thing that would silently stop being true if somebody replaced this with a literal list.
        IEnumerable<string> expected = LeafCatalog.Default
            .Select(l => l.Id)
            .Where(id => id != LeafCatalog.SelfId);

        Assert.All(expected, id => Assert.True(LeafCatalog.IsRestartable(id)));
        Assert.Equal(LeafCatalog.Default.Count - 1, expected.Count());
    }

    [Fact]
    public void A_leaf_can_be_found_by_id_with_its_unit()
    {
        LeafDescriptor? leaf = LeafCatalog.Find("assistant");
        Assert.NotNull(leaf);
        // The assistant's unit carries the '-service' segment, which is the one place in this map a
        // reader would guess wrong.
        Assert.Equal("kgsm-assistant-service.service", leaf.Unit);
        Assert.Null(LeafCatalog.Find("nothing-here"));
    }
}

/// <summary>
/// The rule behind "a service went down": which capabilities answer for a leaf, and what the watcher
/// refuses to call an outage.
/// </summary>
public sealed class LeafHealthWatcherTests
{
    private static Capability Up() => new(Provisioned: true, Status: CapabilityStatus.Operational);
    private static Capability Down() => new(Provisioned: true, Status: CapabilityStatus.Down);
    private static Capability NotYetProbed() => new(Provisioned: true, Status: CapabilityStatus.Unknown);
    private static Capability Absent() => new(Provisioned: false, Status: CapabilityStatus.Unknown);

    private static HostCapabilities Caps(
        Capability? metrics = null, Capability? assistant = null,
        Capability? watchdog = null, Capability? scheduler = null, Capability? reactor = null) =>
        new(Metrics: metrics ?? Up(), Assistant: assistant ?? Up(),
            Watchdog: watchdog ?? Up(), Scheduler: scheduler ?? Up(), Reactor: reactor ?? Up());

    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);

    private sealed class Recorder : INotificationBus
    {
        public List<NotificationEvent> Events { get; } = [];
        public void Publish(AuditRecord record) => throw new NotSupportedException();
        public void PublishDerived(NotificationEvent ev) => Events.Add(ev);
        public IAsyncEnumerable<NotificationEvent> ReadAllAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static (LeafHealthWatcher Watcher, Recorder Bus, Action<HostCapabilities> Set) New()
    {
        HostCapabilities current = Caps();
        var bus = new Recorder();
        var watcher = new LeafHealthWatcher(
            () => current, bus, Microsoft.Extensions.Logging.Abstractions.NullLogger<LeafHealthWatcher>.Instance);
        return (watcher, bus, c => current = c);
    }

    [Fact]
    public void A_deploy_length_outage_is_never_announced()
    {
        // The whole reason the dwell exists: delivering a leaf restarts it, and a channel that pages on
        // every deploy gets switched off — after which it reports nothing at all.
        (LeafHealthWatcher w, Recorder bus, Action<HostCapabilities> set) = New();

        set(Caps(metrics: Down()));
        w.Tick(T0);
        w.Tick(T0.AddSeconds(20));
        set(Caps(metrics: Up()));
        w.Tick(T0.AddSeconds(40));

        Assert.Empty(bus.Events);
    }

    [Fact]
    public void A_real_outage_is_announced_once_and_names_the_leaf()
    {
        (LeafHealthWatcher w, Recorder bus, Action<HostCapabilities> set) = New();

        set(Caps(metrics: Down()));
        w.Tick(T0);
        w.Tick(T0.AddSeconds(90));
        w.Tick(T0.AddSeconds(100));   // still down — already said
        w.Tick(T0.AddMinutes(30));    // and still down half an hour later

        NotificationEvent only = Assert.Single(bus.Events);
        Assert.Equal("leaf_down", only.CatalogId);
        Assert.Equal("monitor", only.ActionSubject);
        Assert.Equal(AuditSeverity.Danger, only.Severity);
        // A leaf is not a game server; putting its id in the server slot would send a tap to a page
        // that does not exist.
        Assert.Null(only.ServerId);
    }

    [Fact]
    public void The_recovery_only_goes_to_somebody_who_heard_about_the_outage()
    {
        (LeafHealthWatcher w, Recorder bus, Action<HostCapabilities> set) = New();

        // A blip: down, then up, without ever crossing the dwell. Announcing "it came back" here would be
        // the first anybody heard of it.
        set(Caps(watchdog: Down()));
        w.Tick(T0);
        set(Caps(watchdog: Up()));
        w.Tick(T0.AddSeconds(20));
        Assert.Empty(bus.Events);

        // Now a real one, and its recovery does arrive.
        set(Caps(watchdog: Down()));
        w.Tick(T0.AddMinutes(5));
        w.Tick(T0.AddMinutes(7));
        set(Caps(watchdog: Up()));
        w.Tick(T0.AddMinutes(8));

        Assert.Collection(bus.Events,
            e => Assert.Equal("leaf_down", e.CatalogId),
            e =>
            {
                Assert.Equal("leaf_up", e.CatalogId);
                Assert.Equal(AuditSeverity.Success, e.Severity);
            });
    }

    [Fact]
    public void Not_yet_probed_is_not_an_outage()
    {
        // The API having just started is the common case, and treating unknown as down would announce
        // an outage on every one of its own redeploys.
        (LeafHealthWatcher w, Recorder bus, Action<HostCapabilities> set) = New();

        set(Caps(assistant: NotYetProbed()));
        w.Tick(T0);
        w.Tick(T0.AddMinutes(10));

        Assert.Empty(bus.Events);
    }

    [Fact]
    public void A_leaf_this_host_does_not_run_is_not_an_outage()
    {
        (LeafHealthWatcher w, Recorder bus, Action<HostCapabilities> set) = New();

        set(Caps(scheduler: Absent()));
        w.Tick(T0);
        w.Tick(T0.AddMinutes(10));

        Assert.Empty(bus.Events);
    }

    [Fact]
    public void Two_leaves_failing_at_once_are_two_facts()
    {
        (LeafHealthWatcher w, Recorder bus, Action<HostCapabilities> set) = New();

        set(Caps(metrics: Down(), watchdog: Down()));
        w.Tick(T0);
        w.Tick(T0.AddMinutes(2));

        // Distinct subject keys, or the delivery worker's window would report only the first.
        Assert.Equal(2, bus.Events.Count);
        Assert.Equal(2, bus.Events.Select(e => e.SubjectKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Coming_back_and_going_down_again_is_announced_again()
    {
        (LeafHealthWatcher w, Recorder bus, Action<HostCapabilities> set) = New();

        set(Caps(metrics: Down()));
        w.Tick(T0);
        w.Tick(T0.AddMinutes(2));
        set(Caps(metrics: Up()));
        w.Tick(T0.AddMinutes(3));
        set(Caps(metrics: Down()));
        w.Tick(T0.AddMinutes(4));
        w.Tick(T0.AddMinutes(6));

        Assert.Equal(["leaf_down", "leaf_up", "leaf_down"], bus.Events.Select(e => e.CatalogId));
    }

    [Fact]
    public void The_two_leaves_with_no_probe_are_never_reported_either_way()
    {
        // The firewall idle-exits by design and the bot serves no health endpoint. Neither has a signal,
        // so neither is reported — the alternative is inventing one, which would be a lie in whichever
        // direction it was set.
        (LeafHealthWatcher w, Recorder bus, Action<HostCapabilities> set) = New();

        set(Caps(metrics: Down(), assistant: Down(), watchdog: Down(), scheduler: Down()));
        w.Tick(T0);
        w.Tick(T0.AddMinutes(2));

        string[] reported = bus.Events.Select(e => e.ActionSubject!).ToArray();
        Assert.DoesNotContain("firewall", reported);
        Assert.DoesNotContain("bot", reported);
        Assert.DoesNotContain("api", reported);
        Assert.Equal(4, reported.Length);
    }
}
