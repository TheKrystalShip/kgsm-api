using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M6·a coverage for the condition-mirror <see cref="AlertEngine"/> — the reconcile (<c>Tick</c>) driven
/// with crafted watchdog states + controlled time. Load-bearing invariants: a crash raises one stable
/// <c>crash:&lt;id&gt;</c> firing record; <c>failed</c> escalates to danger and never auto-resolves; a clear
/// resolves ONLY after the probation dwell (a crash-loop never flaps); a re-crash inside the window cancels
/// the pending resolve; a vanished instance retracts (no rear-view); the alert↔audit bridge stamps the
/// stashed <c>server.start</c> id as <c>resolution.actionId</c> — but only when it post-dates the raise
/// (episode-scoped: a stale prior-episode action is never linked); the rear-view ages off at retention.
/// </summary>
public sealed class AlertEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Crash_RaisesOneWarnFiringAlert()
    {
        AlertEngine engine = Engine();

        engine.Tick([Crashing("mc", restarts: 1, reason: "crashed (exit 1); restart in 2s")], T0);

        Alert a = Assert.Single(engine.Firing);
        Assert.Equal("crash:mc", a.Id);
        Assert.Equal(AlertSeverity.Warn, a.Severity);
        Assert.Equal(AlertSource.Watchdog, a.Source);
        Assert.Equal(AlertStatus.Firing, a.Status);
        Assert.Equal("mc", a.ServerId);
        Assert.Equal("hotrod", a.HostId);
        Assert.False(a.Escalated);
        Assert.Equal(1, a.Attempts);
        Assert.Equal(T0, a.RaisedAt);
        Assert.Equal("crashed (exit 1); restart in 2s", a.Detail);
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Stopped_OrRunning_NeverRaises()
    {
        AlertEngine engine = Engine();

        // A deliberately-stopped instance and a healthy running one are NOT crash conditions.
        engine.Tick(
        [
            new WatchdogInstanceState { Name = "stopped", Desired = "stopped", Phase = "stopped" },
            new WatchdogInstanceState { Name = "healthy", Desired = "running", Phase = "running" },
        ], T0);

        Assert.Empty(engine.Firing);
    }

    [Fact]
    public void Failed_EscalatesToDanger_AndNeverAutoResolves()
    {
        AlertEngine engine = Engine();

        // First a normal crash (warn), then the supervisor gives up (failed → danger, escalated).
        engine.Tick([Crashing("mc", restarts: 2)], T0);
        engine.Tick([Failed("mc", restarts: 5)], T0 + TimeSpan.FromSeconds(5));

        Alert a = Assert.Single(engine.Firing);
        Assert.Equal(AlertSeverity.Danger, a.Severity);
        Assert.True(a.Escalated);
        Assert.Equal(5, a.Attempts);
        Assert.Equal(T0, a.RaisedAt); // re-push keeps the original raise time (upsert, not a new alert)

        // It stays failed well past the probation window — an escalated condition NEVER auto-resolves.
        engine.Tick([Failed("mc", restarts: 5)], T0 + AlertEngine.ResolveProbation + TimeSpan.FromMinutes(5));
        Assert.Single(engine.Firing);
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Clear_DoesNotResolveBeforeProbation_ThenResolvesWithBridgeActionId()
    {
        AlertEngine engine = Engine();

        engine.Tick([Crashing("mc")], T0);
        // The start|restart that held, stamped AFTER the raise (T0) — bridge-eligible under episode-scoping.
        engine.NoteRecoveryAction("mc", "evt_recovered", T0 + TimeSpan.FromSeconds(2));

        // The condition reads clear (probation starts HERE — at the first clear observation, not the raise).
        DateTimeOffset tClear = T0 + TimeSpan.FromSeconds(5);
        engine.Tick([Running("mc")], tClear);
        Assert.Single(engine.Firing);                 // the arming tick itself never resolves
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));

        // Past the dwell, staying clear → resolved, moved to the rear-view, bridged to the start action.
        DateTimeOffset tResolve = tClear + AlertEngine.ResolveProbation + TimeSpan.FromSeconds(1);
        engine.Tick([Running("mc")], tResolve);

        Assert.Empty(engine.Firing);
        Alert r = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.Equal("crash:mc", r.Id);
        Assert.Equal(AlertStatus.Resolved, r.Status);
        Assert.Equal(tResolve, r.ResolvedAt);
        Assert.NotNull(r.Resolution);
        Assert.Equal(AlertResolvedBy.System, r.Resolution!.By);
        Assert.Equal(AlertSource.Watchdog, r.Resolution.Source);
        Assert.Equal("evt_recovered", r.Resolution.ActionId);
    }

    [Fact]
    public void AutonomousAutoHeal_NoRecoveryAction_ResolvesWithNullActionId()
    {
        AlertEngine engine = Engine();

        // The watchdog auto-restarts but its recovery event was NOT audited (dropped best-effort, or the API
        // wasn't listening), so the engine never gets a NoteRecoveryAction. The condition still clears
        // (Phase=running) and resolves after probation, but with actionId NULL: no audited action to link, so
        // we never fabricate one. (When the instance_restarted event IS audited it bridges — see below.)
        engine.Tick([Crashing("mc")], T0);
        DateTimeOffset tClear = T0 + TimeSpan.FromSeconds(5);
        engine.Tick([Running("mc")], tClear);
        engine.Tick([Running("mc")], tClear + AlertEngine.ResolveProbation + TimeSpan.FromSeconds(1));

        Alert r = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.Equal("Recovered — running and stable.", r.Resolution!.Reason);
        Assert.Null(r.Resolution.ActionId); // honest: autonomous restart is not an audited action
    }

    [Fact]
    public void ReCrashWithinProbation_CancelsTheResolve()
    {
        AlertEngine engine = Engine();

        engine.Tick([Crashing("mc")], T0);
        engine.Tick([Running("mc")], T0 + TimeSpan.FromSeconds(10));   // arm probation
        engine.Tick([Crashing("mc")], T0 + TimeSpan.FromSeconds(20));  // re-crash inside the window
        // Even at T0+probation+ from the FIRST clear, the re-crash reset the clock → still firing.
        engine.Tick([Running("mc")], T0 + AlertEngine.ResolveProbation + TimeSpan.FromSeconds(5));

        Assert.Single(engine.Firing);                 // the second clear hasn't held long enough yet
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
    }

    [Fact]
    public void VanishedInstance_IsRetracted_NotResolved()
    {
        AlertEngine engine = Engine();

        engine.Tick([Crashing("mc")], T0);
        engine.Tick([], T0 + TimeSpan.FromSeconds(5)); // instance gone (uninstalled) — not in the list

        Assert.Empty(engine.Firing);
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(1))); // retract = no rear-view
    }

    [Fact]
    public void StoppedAfterCrash_ResolvesWithoutActionId()
    {
        AlertEngine engine = Engine();

        engine.Tick([Crashing("mc")], T0);
        engine.NoteRecoveryAction("mc", "evt_irrelevant", T0 + TimeSpan.FromSeconds(2)); // a recovery id exists, but a STOP cleared it, not a (re)start

        // The operator stops it; still present but Desired=stopped → clears, then resolves after probation.
        DateTimeOffset tClear = T0 + TimeSpan.FromSeconds(5);
        engine.Tick([new WatchdogInstanceState { Name = "mc", Desired = "stopped", Phase = "stopped" }], tClear);
        engine.Tick([new WatchdogInstanceState { Name = "mc", Desired = "stopped", Phase = "stopped" }],
            tClear + AlertEngine.ResolveProbation + TimeSpan.FromSeconds(1));

        Alert r = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.Null(r.Resolution!.ActionId); // honest: we don't fabricate a start id for a stop-cleared crash
    }

    [Fact]
    public void StaleRecoveryFromPriorEpisode_IsNotBridged()
    {
        AlertEngine engine = Engine();

        // A recovery action lingers from an EARLIER episode (or a fast auto-heal blip that never fired):
        // stashed at T0, well before the crash below.
        engine.NoteRecoveryAction("mc", "evt_stale", T0);

        // A NEW crash episode raises an hour later; its OWN recovery event dropped (best-effort emit), so
        // nothing fresh is stashed. The stale id predates this episode's raise → it must NOT be bridged:
        // episode-scoping resolves with actionId null rather than mislinking the unrelated prior action.
        DateTimeOffset tCrash = T0 + TimeSpan.FromHours(1);
        engine.Tick([Crashing("mc")], tCrash);
        DateTimeOffset tClear = tCrash + TimeSpan.FromSeconds(5);
        engine.Tick([Running("mc")], tClear); // arm probation
        engine.Tick([Running("mc")], tClear + AlertEngine.ResolveProbation + TimeSpan.FromSeconds(1));

        Alert r = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.Null(r.Resolution!.ActionId); // a pre-raise action is never THIS crash's fix
    }

    [Fact]
    public void AutonomousAutoHeal_AuditedRecovery_BridgesActionId()
    {
        AlertEngine engine = Engine();

        // The watchdog's autonomous crash-restart (d4b453f) emits instance_restarted → a server.restart
        // audit row, stamped at its COMPLETION time (after the crash was observed). Under episode-scoping
        // that still bridges, because the recovery post-dates the raise — the auto-heal link is preserved.
        engine.Tick([Crashing("mc")], T0);
        engine.NoteRecoveryAction("mc", "evt_autoheal", T0 + TimeSpan.FromSeconds(3)); // restart completed after the raise
        DateTimeOffset tClear = T0 + TimeSpan.FromSeconds(5);
        engine.Tick([Running("mc")], tClear); // arm probation
        engine.Tick([Running("mc")], tClear + AlertEngine.ResolveProbation + TimeSpan.FromSeconds(1));

        Alert r = Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));
        Assert.Equal("evt_autoheal", r.Resolution!.ActionId); // a real recovery within the episode bridges
    }

    [Fact]
    public void ResolvedRecords_AgeOffAfterRetention()
    {
        AlertEngine engine = Engine();

        engine.Tick([Crashing("mc")], T0);
        DateTimeOffset tClear = T0 + TimeSpan.FromSeconds(5);
        DateTimeOffset tResolve = tClear + AlertEngine.ResolveProbation + TimeSpan.FromSeconds(1);
        engine.Tick([Running("mc")], tClear);   // arm
        engine.Tick([Running("mc")], tResolve); // resolve
        Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1)));

        // A tick past the retention window ages the resolved record off the rear-view.
        engine.Tick([Running("mc")], tResolve + AlertEngine.ResolvedRetention + TimeSpan.FromMinutes(1));
        Assert.Empty(engine.ResolvedSince(T0 - TimeSpan.FromDays(7)));
    }

    [Fact]
    public void NewCrashAfterResolve_IsADistinctFiringRecord()
    {
        AlertEngine engine = Engine();

        engine.Tick([Crashing("mc")], T0);
        DateTimeOffset tClear = T0 + TimeSpan.FromSeconds(5);
        engine.Tick([Running("mc")], tClear);
        engine.Tick([Running("mc")], tClear + AlertEngine.ResolveProbation + TimeSpan.FromSeconds(1)); // resolved

        DateTimeOffset tNew = T0 + TimeSpan.FromHours(1);
        engine.Tick([Crashing("mc")], tNew); // crashes again later

        Alert a = Assert.Single(engine.Firing);
        Assert.Equal(AlertStatus.Firing, a.Status);
        Assert.Equal(tNew, a.RaisedAt); // a fresh raise, not the stale resolved time
        Assert.Single(engine.ResolvedSince(T0 - TimeSpan.FromDays(1))); // the prior resolution still in the rear-view
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static AlertEngine Engine() =>
        new(BuildOptions(), new StubProvider(), Hub(), NullLogger<AlertEngine>.Instance);

    private static ApiOptions BuildOptions() =>
        ApiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["KGSM_API_HOST_ID"] = "hotrod" })
            .Build());

    // A hub with no connections: Publish never serializes (no subscribers), so default JSON options suffice.
    private static StreamHub Hub() => new(Options.Create(new JsonOptions()));

    private static WatchdogInstanceState Crashing(string name, int restarts = 1, string reason = "") =>
        new() { Name = name, Desired = "running", Phase = "restart-pending", Restarts = restarts, Reason = reason };

    private static WatchdogInstanceState Failed(string name, int restarts = 5, string reason = "") =>
        new() { Name = name, Desired = "running", Phase = "failed", Restarts = restarts, Reason = reason };

    private static WatchdogInstanceState Running(string name) =>
        new() { Name = name, Desired = "running", Phase = "running", Populated = true };

    private sealed class StubProvider : IServiceProvider
    {
        // Tick never resolves services (it takes the watchdog states as an argument); the loop does.
        public object? GetService(Type serviceType) => null;
    }
}
