using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Integrations.WebPush;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Coverage for what a firing condition offers to do about itself — <see cref="AlertActionCatalog"/> and the
/// three <see cref="AlertEngine"/> passes that attach its answer to the records they publish.
///
/// Load-bearing invariants: an available update offers Update; a crash the supervisor is still retrying
/// offers Stop while one it gave up on offers Start (the inversion is the whole point); a threshold breach
/// offers nothing; a resolved record offers nothing; and — the reason the shared catalog exists — the alert
/// feed and a push notification never name different verbs for the same condition.
/// </summary>
public sealed class AlertActionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // --- the catalog ------------------------------------------------------------------------------

    [Fact]
    public void AnAvailableUpdate_OffersTheUpdateVerb()
    {
        AlertAction a = Assert.Single(AlertActionCatalog.For(AlertSource.Engine, "starbound", escalated: false));
        Assert.Equal(PushActionKind.ServerUpdate, a.Kind);
        Assert.Equal("starbound", a.Target);
    }

    // Stop, not Restart: the watchdog is already restarting it, so Restart asks for what is happening anyway.
    [Fact]
    public void ACrashBeingRetried_OffersStop()
    {
        AlertAction a = Assert.Single(AlertActionCatalog.For(AlertSource.Watchdog, "mc", escalated: false));
        Assert.Equal(PushActionKind.ServerStop, a.Kind);
    }

    // Start, not Stop: the supervisor gave up, so the server is already down and Stop asks for what already is.
    [Fact]
    public void ACrashTheSupervisorGaveUpOn_OffersStart()
    {
        AlertAction a = Assert.Single(AlertActionCatalog.For(AlertSource.Watchdog, "mc", escalated: true));
        Assert.Equal(PushActionKind.ServerStart, a.Kind);
    }

    // A number over a line names no cause, and every verb available here would be a guess at which.
    [Theory]
    [InlineData(AlertSource.HostMonitor)]
    [InlineData(AlertSource.Metrics)]
    public void AThresholdBreach_OffersNothing(string source)
    {
        Assert.Empty(AlertActionCatalog.For(source, "factorio", escalated: false));
    }

    // A host-wide condition names no server, so there is nothing for a server verb to act on.
    [Fact]
    public void AConditionWithNoServer_OffersNothing()
    {
        Assert.Empty(AlertActionCatalog.For(AlertSource.HostMonitor, serverId: null, escalated: false));
        Assert.Empty(AlertActionCatalog.For(AlertSource.Engine, serverId: null, escalated: false));
    }

    // --- the anti-drift test: one condition, one verb, whichever surface describes it --------------
    //
    // This is the reason ConditionActions exists as a shared catalog rather than as a second opinion. The
    // labels differ deliberately (a lock screen has one line of context) and are not compared; the verb is
    // the half that can be wrong, and the crash pair is where it would be wrong in the most damaging way —
    // reversed, each button asks for exactly what is already happening.
    [Theory]
    [InlineData("update_available", AlertSource.Engine, false)]
    [InlineData("crash", AlertSource.Watchdog, false)]
    [InlineData("crash_loop", AlertSource.Watchdog, true)]
    public void TheAlertFeedAndAPushNotification_NameTheSameVerb(string catalogId, string source, bool escalated)
    {
        IReadOnlyList<PushActionOffer> push = PushActionCatalog.For(
            new NotificationEvent(CatalogId: catalogId, Action: "server.crash", ServerId: "mc",
                Severity: AuditSeverity.Warn, Summary: "s", Ts: T0, AuditId: "evt_1"));

        IReadOnlyList<AlertAction> alert = AlertActionCatalog.For(source, "mc", escalated);

        Assert.Equal(Assert.Single(push).Kind, Assert.Single(alert).Kind);
    }

    // --- the engine attaches it -------------------------------------------------------------------

    [Fact]
    public void TheUpdatePass_PublishesTheOfferOnTheFiringRecord()
    {
        AlertEngine engine = Engine();

        engine.TickUpdates(Statuses(("starbound", UpdateAvailable())), T0);

        Alert a = Assert.Single(engine.Firing);
        Assert.Equal(PushActionKind.ServerUpdate, Assert.Single(a.Actions!).Kind);
    }

    [Fact]
    public void TheCrashPass_InvertsTheOfferWhenTheSupervisorGivesUp()
    {
        AlertEngine engine = Engine();

        engine.Tick([Crashing("mc")], T0);
        Assert.Equal(PushActionKind.ServerStop, Assert.Single(Assert.Single(engine.Firing).Actions!).Kind);

        engine.Tick([Failed("mc")], T0 + TimeSpan.FromSeconds(5));
        Assert.Equal(PushActionKind.ServerStart, Assert.Single(Assert.Single(engine.Firing).Actions!).Kind);
    }

    // A cleared condition is a rear-view entry — there is nothing left to do about it, so it carries no
    // offer for a surface to draw a button from.
    [Fact]
    public void AResolvedRecord_CarriesNoOffer()
    {
        AlertEngine engine = Engine();

        engine.TickUpdates(Statuses(("starbound", UpdateAvailable())), T0);
        engine.TickUpdates(Statuses(("starbound", UpToDate())), T0 + TimeSpan.FromMinutes(1));

        Alert resolved = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.Equal(AlertStatus.Resolved, resolved.Status);
        Assert.Null(resolved.Actions);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static Dictionary<string, Reading<InstanceRuntimeStatus>> Statuses(
        params (string Name, InstanceRuntimeStatus Status)[] rows) =>
        rows.ToDictionary(r => r.Name, r => Reading<InstanceRuntimeStatus>.Measured(r.Status));

    private static InstanceRuntimeStatus UpdateAvailable() =>
        Status(new VersionInfo
        {
            Current = "16000000", Latest = "16302742", Checked = true, CheckedAt = T0, UpdatesAvailable = true,
        });

    private static InstanceRuntimeStatus UpToDate() =>
        Status(new VersionInfo
        {
            Current = "16302742", Latest = "16302742", Checked = true, CheckedAt = T0, UpdatesAvailable = false,
        });

    private static InstanceRuntimeStatus Status(VersionInfo version) =>
        new() { Status = false, Version = version };

    private static WatchdogInstanceState Crashing(string name) =>
        new() { Name = name, Desired = "running", Phase = "restart-pending", Restarts = 1, Reason = "" };

    private static WatchdogInstanceState Failed(string name) =>
        new() { Name = name, Desired = "running", Phase = "failed", Restarts = 5, Reason = "" };

    private static AlertEngine Engine()
    {
        ApiOptions options = ApiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:HostId"] = "hotrod" })
            .Build());
        return new(options, new StubProvider(), Monitor(options),
            new InstanceCache(new StubProvider(), options, NullLogger<InstanceCache>.Instance),
            new StreamHub(Options.Create(new JsonOptions())), NullLogger<AlertEngine>.Instance);
    }

    // Inert: both passes take their input as an argument, so neither the monitor socket nor the instance
    // cache is ever dialed.
    private static MonitorClient Monitor(ApiOptions options)
    {
        IServiceScopeFactory scopeFactory =
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var registry = new LeafRegistry(scopeFactory, options, NullLogger<LeafRegistry>.Instance);
        return new MonitorClient(options, registry, NullLogger<MonitorClient>.Instance);
    }

    private sealed class StubProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
