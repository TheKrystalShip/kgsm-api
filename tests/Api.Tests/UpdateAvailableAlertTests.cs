using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Coverage for the <c>engine</c> alert source — <see cref="AlertEngine.TickUpdates"/> driven with crafted
/// status readings and controlled time. It is the one producer that measures nothing: kgsm records what the
/// scheduler's networked check found and this pass mirrors that record into the feed, so what the tests pin
/// is the mirroring, not a threshold.
///
/// Load-bearing invariants: an available update fires once as <c>info</c> with the honest version pair; a
/// re-read of the same record is silent; a newer target version re-pushes; applying the update resolves
/// <c>by:system</c> with <c>actionId:null</c>; an instance nothing has ever checked HOLDS rather than
/// reading as up-to-date; a non-measured reading holds; an uninstall retracts; and the pass never disturbs a
/// crash alert in the shared firing set.
/// </summary>
public sealed class UpdateAvailableAlertTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // --- 1. an available update fires once, as info, carrying both versions -----------------------
    [Fact]
    public void AnAvailableUpdate_Fires_AsInfo_WithBothVersions()
    {
        AlertEngine engine = Engine();

        engine.TickUpdates(Statuses(("starbound", Update(current: "16000000", latest: "16302742"))), T0);

        Alert a = Assert.Single(engine.Firing);
        Assert.Equal("update:starbound", a.Id);
        Assert.Equal(AlertSeverity.Info, a.Severity);
        Assert.Equal(AlertSource.Engine, a.Source);
        Assert.Equal("starbound has an update available", a.Title);
        Assert.Contains("16000000", a.Detail);
        Assert.Contains("16302742", a.Detail);
        Assert.Equal("starbound", a.ServerId);
        Assert.Equal("hotrod", a.HostId);
        Assert.Equal(AlertSurface.Server, a.Anchor!.Surface);
        Assert.Equal(T0, a.RaisedAt);

        // Nothing is retrying and nothing gives up — the condition waits for a person, so neither field
        // carries a number that would be read as auto-recovery having tried.
        Assert.False(a.Escalated);
        Assert.Equal(0, a.Attempts);
    }

    // --- 2. the same record re-read is silent (no dwell, but no re-raise either) -------------------
    [Fact]
    public void TheSameRecord_ReadAgain_KeepsTheOriginalRaiseTime()
    {
        AlertEngine engine = Engine();
        var reading = Statuses(("starbound", Update("16000000", "16302742")));

        engine.TickUpdates(reading, T0);
        engine.TickUpdates(reading, T0 + TimeSpan.FromMinutes(30));

        Alert a = Assert.Single(engine.Firing);
        Assert.Equal(T0, a.RaisedAt); // when the update appeared, not when it was last observed
    }

    // --- 3. a newer build landing before the operator acts re-pushes the record --------------------
    [Fact]
    public void ASecondBuild_BeforeTheFirstIsApplied_RepushesWithTheNewTarget()
    {
        AlertEngine engine = Engine();

        engine.TickUpdates(Statuses(("starbound", Update("16000000", "16302742"))), T0);
        engine.TickUpdates(Statuses(("starbound", Update("16000000", "16400000"))), T0 + TimeSpan.FromHours(1));

        Alert a = Assert.Single(engine.Firing);
        Assert.Contains("16400000", a.Detail);
        Assert.DoesNotContain("16302742", a.Detail); // the card must not keep naming a build already superseded
        Assert.Equal(T0, a.RaisedAt);                // still the same condition, so the same episode
    }

    // --- 4. applying the update resolves it, by:system, with no audit bridge -----------------------
    [Fact]
    public void ApplyingTheUpdate_Resolves_BySystem_WithNoActionId()
    {
        AlertEngine engine = Engine();

        engine.TickUpdates(Statuses(("starbound", Update("16000000", "16302742"))), T0);
        engine.TickUpdates(Statuses(("starbound", UpToDate("16302742"))), T0 + TimeSpan.FromMinutes(5));

        Assert.Empty(engine.Firing);
        Alert resolved = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.Equal("update:starbound", resolved.Id);
        Assert.Equal(AlertStatus.Resolved, resolved.Status);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), resolved.ResolvedAt);
        Assert.Equal(AlertResolvedBy.System, resolved.Resolution!.By);
        Assert.Equal(AlertSource.Engine, resolved.Resolution.Source);
        Assert.Contains("16302742", resolved.Resolution.Reason);

        // The actionId bridge is crash-specific: an update clears because the installed version caught up,
        // not because a stashed audit action was observed to fix it.
        Assert.Null(resolved.Resolution.ActionId);
    }

    // --- 5. never checked is NOT up to date -------------------------------------------------------
    [Fact]
    public void AnInstanceNothingHasChecked_Holds_AndNeverResolvesAPendingUpdate()
    {
        AlertEngine engine = Engine();

        // On its own, an unchecked instance raises nothing — the honest answer is "we do not know",
        // and a feed that stayed silent for it is correct.
        engine.TickUpdates(Statuses(("starbound", NeverChecked())), T0);
        Assert.Empty(engine.Firing);

        // And once something IS firing, an unchecked read must not clear it. Reading null as "no update"
        // is exactly the fabrication this pass exists to avoid.
        engine.TickUpdates(Statuses(("starbound", Update("16000000", "16302742"))), T0 + TimeSpan.FromMinutes(1));
        engine.TickUpdates(Statuses(("starbound", NeverChecked())), T0 + TimeSpan.FromMinutes(2));

        Alert a = Assert.Single(engine.Firing);
        Assert.Equal("update:starbound", a.Id);
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    // --- 6. a reading that measured nothing holds -------------------------------------------------
    [Fact]
    public void ANonMeasuredReading_Holds()
    {
        AlertEngine engine = Engine();

        engine.TickUpdates(Statuses(("starbound", Update("16000000", "16302742"))), T0);
        engine.TickUpdates(
            new Dictionary<string, Reading<InstanceRuntimeStatus>>
            {
                ["starbound"] = Reading<InstanceRuntimeStatus>.Unavailable("engine read failed"),
            },
            T0 + TimeSpan.FromMinutes(1));

        Assert.Single(engine.Firing);
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    // --- 7. uninstalling retracts — no rear-view for something that no longer exists ---------------
    [Fact]
    public void UninstallingWhileAnUpdateIsPending_Retracts_RatherThanResolves()
    {
        AlertEngine engine = Engine();

        engine.TickUpdates(Statuses(("starbound", Update("16000000", "16302742"))), T0);
        engine.TickUpdates(new Dictionary<string, Reading<InstanceRuntimeStatus>>(), T0 + TimeSpan.FromMinutes(1));

        Assert.Empty(engine.Firing);
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1))); // it was never fixed, it is simply gone
    }

    // --- 8. the shared firing set stays namespaced ------------------------------------------------
    [Fact]
    public void TheUpdatePass_NeverDisturbsACrashAlert()
    {
        AlertEngine engine = Engine();

        engine.Tick([Crashing("factorio")], T0);
        Assert.Single(engine.Firing);

        // factorio is absent from the status map entirely, which is this pass's retract condition — but a
        // crash: id is the crash pass's to reconcile and must survive untouched.
        engine.TickUpdates(Statuses(("starbound", Update("16000000", "16302742"))), T0 + TimeSpan.FromMinutes(1));

        Assert.Equal(2, engine.Firing.Count);
        Assert.Contains(engine.Firing, a => a.Id == "crash:factorio");
        Assert.Contains(engine.Firing, a => a.Id == "update:starbound");
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static Dictionary<string, Reading<InstanceRuntimeStatus>> Statuses(
        params (string Name, InstanceRuntimeStatus Status)[] rows) =>
        rows.ToDictionary(r => r.Name, r => Reading<InstanceRuntimeStatus>.Measured(r.Status));

    private static InstanceRuntimeStatus Update(string current, string latest) =>
        Status(new VersionInfo
        {
            Current = current,
            Latest = latest,
            Checked = true,
            CheckedAt = T0,
            UpdatesAvailable = true,
        });

    private static InstanceRuntimeStatus UpToDate(string version) =>
        Status(new VersionInfo
        {
            Current = version,
            Latest = version,
            Checked = true,
            CheckedAt = T0,
            UpdatesAvailable = false,
        });

    // Nothing has ever asked this instance's upstream: kgsm reports the honest-null triple rather than a
    // fabricated "no update".
    private static InstanceRuntimeStatus NeverChecked() =>
        Status(new VersionInfo { Current = "16000000", Latest = null, Checked = false, UpdatesAvailable = null });

    private static InstanceRuntimeStatus Status(VersionInfo version) =>
        new() { Status = false, Version = version };

    private static WatchdogInstanceState Crashing(string name) =>
        new() { Name = name, Desired = "running", Phase = "restart-pending", Restarts = 1, Reason = "" };

    private static AlertEngine Engine()
    {
        ApiOptions options = ApiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:HostId"] = "hotrod" })
            .Build());
        return new(options, new StubProvider(), Monitor(options),
            new InstanceCache(new StubProvider(), options, NullLogger<InstanceCache>.Instance),
            new StreamHub(Options.Create(new JsonOptions())), NullLogger<AlertEngine>.Instance);
    }

    // Inert: TickUpdates takes the status map as an argument, so neither the monitor socket nor the instance
    // cache above is ever dialed.
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
