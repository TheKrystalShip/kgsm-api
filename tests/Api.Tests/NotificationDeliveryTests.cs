using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Integrations;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The notification delivery worker (the always-on audit tap → providers). Two layers:
/// (1) <see cref="NotificationMappingTests"/> pins the audit-action→catalog map (pure);
/// (2) <see cref="NotificationDeliveryE2ETests"/> drives the real pipeline — an audit row appended through
/// <see cref="AuditService"/> reaches a recording webhook — and proves the routing gates (rule-disabled,
/// once/digest, anti-spam suppression) <b>deterministically</b> via a barrier event (a gated event
/// followed by a delivered one; the worker is sequential, so the delivered POST proves the gated one was
/// already processed — no sleeps). A provider's own send formatting is tested beside it, in
/// <see cref="SlackProviderTests"/>.
/// </summary>
public sealed class NotificationMappingTests
{
    [Theory]
    [InlineData("server.started", "online")]
    [InlineData("server.restarted", "online")] // a completed restart = back online (closes the auto-heal gap)
    [InlineData("server.stopped", "offline")]
    [InlineData("server.crashed", "crash")]
    [InlineData("server.updated", "update")]
    [InlineData("server.update.available", "update_available")]
    [InlineData("server.installed", "installed")]
    [InlineData("backup.created", "backup")]
    [InlineData("host.threshold.breached", "threshold_breach")]
    [InlineData("host.threshold.cleared", "threshold_clear")] // separate ids: the all-clear is its own choice
    [InlineData("player.joined", "player_join")]
    public void CatalogIdForAction_MapsNotifiableActions(string action, string expected) =>
        Assert.Equal(expected, NotificationCatalog.CatalogIdForAction(action));

    [Theory]
    [InlineData("server.crashed", "crash")]                 // the watchdog is restarting it
    [InlineData("server.crash.exhausted", "crash_loop")]    // it has given up
    public void A_crash_and_a_give_up_are_two_events_and_stay_two(string action, string expected) =>
        // Nothing outside the name has to be read to tell them apart, so the escalation cannot be lost
        // by a row that failed to carry the severity that used to be the only thing separating them.
        Assert.Equal(expected, NotificationCatalog.CatalogIdForAction(action));

    [Theory]
    [InlineData("player_join")]
    [InlineData("server_empty")]
    public void The_two_events_other_people_drive_arrive_switched_off(string catalogId) =>
        // Their rate is set by how popular a server is, not by what the host does, so an admin turns them
        // on deliberately — adding them must not change what an already-configured host sends.
        Assert.False(NotificationCatalog.DefaultRule(catalogId).Enabled);

    [Theory]
    [InlineData("crash_loop")]
    [InlineData("offline")]
    [InlineData("threshold_breach")]
    public void Everything_else_still_defaults_on(string catalogId) =>
        Assert.True(NotificationCatalog.DefaultRule(catalogId).Enabled);

    [Theory]
    [InlineData("server.uninstalled")]
    [InlineData("backup.restored")]
    [InlineData("network.ports.opened")]
    [InlineData("network.ports.closed")]
    [InlineData("auth.signed_in")]
    [InlineData("auth.signed_out")]
    public void CatalogIdForAction_DropsNonNotifiable(string action) =>
        Assert.Null(NotificationCatalog.CatalogIdForAction(action));

    [Fact]
    public void CatalogIdForAction_OnlyEverMapsToKnownCatalogEvents()
    {
        string[] notifiable =
        [
            "server.started", "server.restarted", "server.stopped",
            "server.crashed", "server.updated", "server.update.available",
            "server.installed", "backup.created",
            "host.threshold.breached", "host.threshold.cleared", "player.joined",
            "server.crash.exhausted",
        ];
        foreach (string action in notifiable)
            Assert.True(NotificationCatalog.IsKnown(NotificationCatalog.CatalogIdForAction(action)!));
    }
}

/// <summary>
/// The bus's own two decisions, in isolation from the worker: what it refuses to enqueue at all, and what
/// subject it says an event is about. Both matter most for threshold rows, which are transcribed from the
/// monitor's durable episodes rather than produced live, and which carry no server to key on.
/// </summary>
public sealed class NotificationBusTests
{
    private static AuditRecord ThresholdRow(
        string action, string ruleKey, string? sensor, DateTimeOffset ts, string? reason = null)
    {
        var meta = new Dictionary<string, string> { ["episodeId"] = "ep_" + Guid.NewGuid().ToString("N")[..8], ["ruleKey"] = ruleKey };
        if (sensor is not null) meta["ref"] = sensor;
        if (reason is not null) meta["reason"] = reason;
        return new AuditRecord(
            Id: "evt_" + Guid.NewGuid().ToString("N")[..10], Ts: ts, Origin: AuditOrigin.System,
            Actor: new AuditActor(ActorKind.System, "monitor", ActorProvider.System),
            Action: action, Severity: AuditSeverity.Warn,
            Target: new AuditTarget(AuditTargetKind.Host, "test-host", "test-host"),
            ServerId: null, HostId: "test-host", Summary: "something crossed something", Meta: meta);
    }

    private static NotificationBus NewBus() => new(NullLogger<NotificationBus>.Instance);

    private static async Task<List<NotificationEvent>> DrainAsync(NotificationBus bus, int expected)
    {
        var got = new List<NotificationEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (NotificationEvent ev in bus.ReadAllAsync(cts.Token))
        {
            got.Add(ev);
            if (got.Count == expected) break;
        }
        return got;
    }

    [Fact]
    public async Task Breach_IsAboutTheCondition_NotTheHost()
    {
        NotificationBus bus = NewBus();
        bus.Publish(ThresholdRow("host.threshold.breached", "host-temp", "k10temp/Tctl", DateTimeOffset.UtcNow));
        bus.Publish(ThresholdRow("host.threshold.breached", "host-disk", "/", DateTimeOffset.UtcNow));

        List<NotificationEvent> got = await DrainAsync(bus, 2);

        // Both carry a null server; if the subject were the server they would coalesce into one and the
        // second condition would never be heard about.
        Assert.All(got, ev => Assert.Null(ev.ServerId));
        Assert.Equal(2, got.Select(ev => ev.SubjectKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task LifecycleEvent_NamesNoSubject_AndFallsBackToTheServer()
    {
        NotificationBus bus = NewBus();
        bus.Publish(new AuditRecord(
            Id: "evt_x", Ts: DateTimeOffset.UtcNow, Origin: AuditOrigin.System,
            Actor: new AuditActor(ActorKind.System, "watchdog", ActorProvider.System),
            Action: "server.crashed", Severity: AuditSeverity.Warn,
            Target: new AuditTarget(AuditTargetKind.Server, "srv", "srv"),
            ServerId: "srv", HostId: "test-host", Summary: "srv crashed", Meta: null));

        NotificationEvent only = Assert.Single(await DrainAsync(bus, 1));
        Assert.Null(only.SubjectKey);
    }

    private static AuditRecord JoinRow(string server, string? id, string? name, string? addr) =>
        new(Id: "evt_" + Guid.NewGuid().ToString("N")[..10], Ts: DateTimeOffset.UtcNow, Origin: AuditOrigin.System,
            Actor: new AuditActor(ActorKind.System, "watchdog", ActorProvider.System),
            Action: "player.joined", Severity: AuditSeverity.Info,
            Target: new AuditTarget(AuditTargetKind.Server, server, server),
            ServerId: server, HostId: "test-host", Summary: "somebody joined",
            Meta: new Dictionary<string, string>(
                new[] { ("playerId", id), ("playerName", name), ("playerAddr", addr) }
                    .Where(p => p.Item2 is not null)
                    .Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2!))));

    [Fact]
    public async Task Two_people_joining_one_server_are_two_facts()
    {
        NotificationBus bus = NewBus();
        bus.Publish(JoinRow("romestead", null, "Ana", "10.0.0.1:5000"));
        bus.Publish(JoinRow("romestead", null, "Bo", "10.0.0.2:5000"));

        List<NotificationEvent> got = await DrainAsync(bus, 2);

        // Keyed on the server alone the second would be coalesced away inside the anti-spam window — and
        // the whole value of a join notification is that it names who arrived, so the one dropped could
        // be the one worth answering.
        Assert.Equal(2, got.Select(ev => ev.SubjectKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["Ana", "Bo"], got.Select(ev => ev.ActionSubject));
    }

    [Fact]
    public async Task A_join_resolves_the_player_by_the_rosters_own_rule()
    {
        NotificationBus bus = NewBus();
        // The account id wins over the name, and the name over the address — the same precedence the
        // roster keyed its row on, because a button staged here has to address that row.
        bus.Publish(JoinRow("romestead", "7656119", "Ana", "10.0.0.1:5000"));
        bus.Publish(JoinRow("romestead", null, "Bo", "10.0.0.2:5000"));
        bus.Publish(JoinRow("romestead", null, null, "10.0.0.3:5000"));

        List<NotificationEvent> got = await DrainAsync(bus, 3);
        Assert.Equal(["7656119", "Bo", "10.0.0.3:5000"], got.Select(ev => ev.ActionSubject));
    }

    /// <summary>
    /// A provisioning row, with its <c>meta</c> built by the <b>real mapper</b> rather than by hand.
    /// </summary>
    /// <remarks>
    /// Hand-building it is how this suite came to pass against a shape production never emitted: the
    /// fixture wrote <c>meta["status"]</c>, the mapper writes the landing state under
    /// <c>meta["to"]</c>, and the guard that reads it therefore dropped every real
    /// <c>awaiting_approval</c> silently. Deriving the meta from
    /// <see cref="AuditMapping.FromUserAccountEvent"/> means a future rename moves both at once or
    /// fails here.
    /// </remarks>
    private static AuditRecord ProvisionRow(string userId, string status)
    {
        AuditWrite mapped = AuditMapping.FromUserAccountEvent(
            new UserAccountEventData
            {
                Timestamp = DateTimeOffset.UtcNow,
                Actor = "discord:newcomer",
                Origin = AuditOrigin.Ui,
                UserId = userId,
                Username = "newcomer",
                ToTier = KgsmTiers.None,
                ToStatus = status,
            },
            ApiJournal.UserProvisionedEvent,
            "test-host");

        return new AuditRecord(
            Id: "evt_" + Guid.NewGuid().ToString("N")[..10], Ts: mapped.Ts, Origin: mapped.Origin,
            Actor: mapped.Actor, Action: mapped.Action, Severity: mapped.Severity,
            Target: mapped.Target, ServerId: mapped.ServerId, HostId: mapped.HostId,
            Summary: mapped.Summary, Meta: mapped.Meta);
    }

    [Fact]
    public async Task A_provisioning_that_left_somebody_waiting_asks_for_an_approval()
    {
        NotificationBus bus = NewBus();
        bus.Publish(ProvisionRow("usr_abc", "pending"));

        NotificationEvent only = Assert.Single(await DrainAsync(bus, 1));
        Assert.Equal("awaiting_approval", only.CatalogId);
        // The account id, never the username: a label somebody can change out from under a staged handle.
        Assert.Equal("usr_abc", only.ActionSubject);
    }

    [Fact]
    public async Task A_provisioning_that_did_not_is_not_an_approval_request()
    {
        NotificationBus bus = NewBus();
        // A host whose policy activates on sight writes the same action with a different status, and
        // asking an admin to approve what is already approved is worse than saying nothing.
        bus.Publish(ProvisionRow("usr_auto", "active"));
        // The barrier: the channel is FIFO, so receiving this proves the first was dropped, not delayed.
        bus.Publish(ProvisionRow("usr_waiting", "pending"));

        NotificationEvent only = Assert.Single(await DrainAsync(bus, 1));
        Assert.Equal("usr_waiting", only.ActionSubject);
    }

    [Fact]
    public async Task Two_people_signing_up_are_two_people_to_approve()
    {
        NotificationBus bus = NewBus();
        bus.Publish(ProvisionRow("usr_a", "pending"));
        bus.Publish(ProvisionRow("usr_b", "pending"));

        List<NotificationEvent> got = await DrainAsync(bus, 2);
        // Both carry a null server, so a window keyed on that would coalesce the second away.
        Assert.All(got, ev => Assert.Null(ev.ServerId));
        Assert.Equal(2, got.Select(ev => ev.SubjectKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_join_that_names_nobody_carries_no_identity()
    {
        NotificationBus bus = NewBus();
        bus.Publish(JoinRow("romestead", null, null, null));

        NotificationEvent only = Assert.Single(await DrainAsync(bus, 1));
        // Still announced — somebody did join — but with nobody to name, so no button can be staged
        // against a person this row cannot identify.
        Assert.Null(only.ActionSubject);
        Assert.Null(only.SubjectKey);
    }

    [Theory]
    [InlineData("unwatched")]   // the rule was retuned or switched off — nobody measured a recovery
    [InlineData("interrupted")] // the monitor restarted while it was firing — same
    public async Task Clear_ThatDidNotRecover_IsNotAnnounced(string reason)
    {
        NotificationBus bus = NewBus();
        bus.Publish(ThresholdRow("host.threshold.cleared", "host-temp", "k10temp/Tctl", DateTimeOffset.UtcNow, reason));
        // The barrier: a real recovery published after it. The channel is FIFO, so receiving this one
        // proves the first was dropped rather than merely slow.
        bus.Publish(ThresholdRow("host.threshold.cleared", "host-disk", "/", DateTimeOffset.UtcNow));

        NotificationEvent only = Assert.Single(await DrainAsync(bus, 1));
        Assert.Contains("host-disk", only.SubjectKey);
    }

    [Fact]
    public async Task Episode_OlderThanTheNoticeWindow_IsNotAnnounced()
    {
        NotificationBus bus = NewBus();
        // What a cold start transcribing yesterday's episodes looks like.
        bus.Publish(ThresholdRow("host.threshold.breached", "host-temp", "k10temp/Tctl",
            DateTimeOffset.UtcNow.AddHours(-6)));
        bus.Publish(ThresholdRow("host.threshold.breached", "host-disk", "/", DateTimeOffset.UtcNow));

        NotificationEvent only = Assert.Single(await DrainAsync(bus, 1));
        Assert.Contains("host-disk", only.SubjectKey);
    }

    [Fact]
    public async Task StaleLifecycleEvent_IsStillAnnounced()
    {
        NotificationBus bus = NewBus();
        // The age gate is about transcribed history. An engine echo is published as it happens, and a
        // timestamp that drifted must never be a reason to swallow a crash.
        bus.Publish(new AuditRecord(
            Id: "evt_y", Ts: DateTimeOffset.UtcNow.AddHours(-6), Origin: AuditOrigin.System,
            Actor: new AuditActor(ActorKind.System, "watchdog", ActorProvider.System),
            Action: "server.crashed", Severity: AuditSeverity.Warn,
            Target: new AuditTarget(AuditTargetKind.Server, "srv", "srv"),
            ServerId: "srv", HostId: "test-host", Summary: "srv crashed", Meta: null));

        Assert.Single(await DrainAsync(bus, 1));
    }
}

/// <summary>End-to-end: an audit row appended through the real always-on <see cref="AuditService"/> reaches
/// a recording webhook through the bus + worker + provider — and the routing gates hold.</summary>
public sealed class NotificationDeliveryE2ETests
{
    private const string Webhook = "https://hooks.slack.com/services/T777/B777/e2esecrettoken";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static HttpClient AdminClient(NotificationDeliveryFactory f)
    {
        HttpClient c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(KgsmTier.Admin));
        return c;
    }

    private static AuditWrite CrashWrite(string server) => new(
        Ts: DateTimeOffset.UtcNow, Origin: AuditOrigin.System,
        Actor: new AuditActor(ActorKind.System, "system", ActorProvider.System),
        Action: "server.crashed", Severity: AuditSeverity.Warn,
        Target: new AuditTarget(AuditTargetKind.Server, server, server),
        ServerId: server, HostId: "test-host", Summary: $"{server} crashed — auto-restarting", Meta: null);

    private static AuditWrite StartWrite(string server) => new(
        Ts: DateTimeOffset.UtcNow, Origin: AuditOrigin.Api,
        Actor: new AuditActor(ActorKind.Token, "tester", ActorProvider.Api),
        Action: "server.started", Severity: AuditSeverity.Info,
        Target: new AuditTarget(AuditTargetKind.Server, server, server),
        ServerId: server, HostId: "test-host", Summary: $"started {server}", Meta: null);

    [Fact]
    public async Task Crash_AuditRow_DeliversNotification()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new { webhook = Webhook, enabled = true });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        audit.PublishLive(AuditMapping.ToRecordDirect(CrashWrite("factorio-01"), "evt_" + Guid.NewGuid().ToString("N")[..10]));

        await f.Webhook.WaitForAsync(1, Timeout); // the worker drains off-thread — wait for the POST, never assert eagerly
        Assert.True(f.Webhook.Requests.TryDequeue(out RecordedRequest? req));
        Assert.Equal(Webhook, req!.Uri);
        Assert.Contains("factorio-01", req.Body);
        Assert.Contains("crashed", req.Body);
    }

    [Fact]
    public async Task DisabledProvider_DoesNotDeliver_EvenWithWebhook()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        // Provider OFF but a webhook is set, and a second enabled provider is the barrier... there is only one
        // provider, so prove the gate the deterministic way: the disabled provider is enabled mid-flight is a
        // race, so instead we assert no delivery within a generous bound (the positive test shows sub-second
        // latency). This is the one timing-bounded check; every other gate below is barrier-deterministic.
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new { webhook = Webhook, enabled = false });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        audit.PublishLive(AuditMapping.ToRecordDirect(CrashWrite("factorio-01"), "evt_" + Guid.NewGuid().ToString("N")[..10]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Webhook.WaitForAsync(1, TimeSpan.FromSeconds(2)));
        Assert.Empty(f.Webhook.Requests);
    }

    [Fact]
    public async Task DisabledRule_Gated_OnlineStillDelivers()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new
        {
            webhook = Webhook,
            enabled = true,
            events = new[] { new { id = "crash", enabled = false } }, // crash OFF; online stays default (enabled/every)
        });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        audit.PublishLive(AuditMapping.ToRecordDirect(CrashWrite("srv-a"), "evt_" + Guid.NewGuid().ToString("N")[..10])); // gated by the disabled rule
        audit.PublishLive(AuditMapping.ToRecordDirect(StartWrite("srv-a"), "evt_" + Guid.NewGuid().ToString("N")[..10])); // the barrier — delivers, proving the worker passed the crash

        await f.Webhook.WaitForAsync(1, Timeout);
        RecordedRequest only = Assert.Single(f.Webhook.Requests);
        Assert.Contains("is online", only.Body);
        Assert.DoesNotContain("crashed", only.Body); // the crash was gated, not delivered
    }

    [Fact]
    public async Task OnceCadence_DeliversTheFirst_AndSuppressesTheRest()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new
        {
            webhook = Webhook,
            enabled = true,
            events = new[] { new { id = "crash", cadence = "once" } },
        });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        audit.PublishLive(AuditMapping.ToRecordDirect(CrashWrite("srv-b"), "evt_" + Guid.NewGuid().ToString("N")[..10]));  // the first — delivers
        audit.PublishLive(AuditMapping.ToRecordDirect(CrashWrite("srv-b"), "evt_" + Guid.NewGuid().ToString("N")[..10]));  // the same news — held for a day
        audit.PublishLive(AuditMapping.ToRecordDirect(StartWrite("srv-b"), "evt_" + Guid.NewGuid().ToString("N")[..10]));  // the barrier: every → delivers, and it is sequential

        await f.Webhook.WaitForAsync(2, Timeout);
        RecordedRequest[] sent = f.Webhook.Requests.ToArray();

        // Two posts, not three: `once` is the same coalescing as `every` over a much longer window, so the
        // first occurrence still arrives — a rule that swallowed it too would be a mute, not a cadence.
        Assert.Equal(2, sent.Length);
        Assert.Contains("crashed", sent[0].Body);
        Assert.Contains("is online", sent[1].Body);
    }

    [Fact]
    public async Task DigestCadence_HoldsItBack_RatherThanDroppingIt()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new
        {
            webhook = Webhook,
            enabled = true,
            events = new[] { new { id = "crash", cadence = "digest" } },
        });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        audit.PublishLive(AuditMapping.ToRecordDirect(CrashWrite("srv-d"), "evt_" + Guid.NewGuid().ToString("N")[..10]));  // held
        audit.PublishLive(AuditMapping.ToRecordDirect(StartWrite("srv-d"), "evt_" + Guid.NewGuid().ToString("N")[..10]));  // the barrier — every → delivers

        await f.Webhook.WaitForAsync(1, Timeout);
        RecordedRequest only = Assert.Single(f.Webhook.Requests);
        Assert.Contains("is online", only.Body);
        Assert.DoesNotContain("crashed", only.Body);

        // Held, not dropped: the row is waiting for its window, and a restart would not lose it.
        var digests = f.Services.GetRequiredService<NotificationDigestStore>();
        Assert.Empty(await digests.TakeDueAsync("slack", DateTimeOffset.UtcNow, default));
        IReadOnlyList<Data.NotificationDigestEntity> due =
            await digests.TakeDueAsync("slack", DateTimeOffset.UtcNow + NotificationDigestStore.Window, default);
        Assert.Equal("crash", Assert.Single(due).CatalogId);
    }

    [Fact]
    public async Task ADueDigest_GoesOutAsOneMessage()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new
        {
            webhook = Webhook,
            enabled = true,
            events = new[] { new { id = "crash", cadence = "digest" } },
        });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        audit.PublishLive(AuditMapping.ToRecordDirect(CrashWrite("srv-e1"), "evt_" + Guid.NewGuid().ToString("N")[..10]));
        audit.PublishLive(AuditMapping.ToRecordDirect(CrashWrite("srv-e2"), "evt_" + Guid.NewGuid().ToString("N")[..10]));
        audit.PublishLive(AuditMapping.ToRecordDirect(StartWrite("srv-e1"), "evt_" + Guid.NewGuid().ToString("N")[..10]));           // barrier: both crashes are now held
        await f.Webhook.WaitForAsync(1, Timeout);
        f.Webhook.Requests.Clear();

        var worker = f.Services.GetServices<IHostedService>().OfType<NotificationDigestWorker>().Single();
        await worker.FlushAsync(DateTimeOffset.UtcNow + NotificationDigestStore.Window, default);

        RecordedRequest only = Assert.Single(f.Webhook.Requests);
        // ONE message naming both — sending two would be the cadence nobody chose.
        Assert.Contains("srv-e1", only.Body);
        Assert.Contains("srv-e2", only.Body);
        Assert.Contains("2 crashes", only.Body);
    }

    [Fact]
    public async Task RepeatedCrash_SuppressedWithinWindow()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new { webhook = Webhook, enabled = true });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        audit.PublishLive(AuditMapping.ToRecordDirect(CrashWrite("srv-c"), "evt_" + Guid.NewGuid().ToString("N")[..10])); // crash#1 → delivers
        audit.PublishLive(AuditMapping.ToRecordDirect(CrashWrite("srv-c"), "evt_" + Guid.NewGuid().ToString("N")[..10])); // crash#2 → suppressed (same provider:server:catalog, within 60s)
        audit.PublishLive(AuditMapping.ToRecordDirect(StartWrite("srv-c"), "evt_" + Guid.NewGuid().ToString("N")[..10])); // online → different catalog key → delivers (barrier)

        await f.Webhook.WaitForAsync(2, Timeout); // exactly crash#1 + online; crash#2 never posts
        Assert.Equal(2, f.Webhook.Requests.Count);
        List<string> bodies = f.Webhook.Requests.Select(r => r.Body).ToList();
        Assert.Single(bodies, b => b.Contains("crashed", StringComparison.Ordinal));  // one crash, not two
        Assert.Single(bodies, b => b.Contains("is online", StringComparison.Ordinal));
    }

    private static AuditWrite BreachWrite(string ruleKey, string sensor, string summary) => new(
        Ts: DateTimeOffset.UtcNow, Origin: AuditOrigin.System,
        Actor: new AuditActor(ActorKind.System, "monitor", ActorProvider.System),
        Action: "host.threshold.breached", Severity: AuditSeverity.Warn,
        Target: new AuditTarget(AuditTargetKind.Host, "test-host", "test-host"),
        ServerId: null, HostId: "test-host", Summary: summary,
        Meta: new Dictionary<string, string>
        {
            ["episodeId"] = "ep_" + Guid.NewGuid().ToString("N")[..8],
            ["ruleKey"] = ruleKey,
            ["ref"] = sensor,
        });

    [Fact]
    public async Task TwoConditionsOnOneHost_BothDeliver_AndARepeatDoesNot()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new { webhook = Webhook, enabled = true });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        // Every host-scope threshold row carries a null server. Keyed on the server they would all be one
        // subject, and the disk would be silently swallowed by the sensor that breached first.
        audit.PublishLive(AuditMapping.ToRecordDirect(BreachWrite("host-temp", "k10temp", "k10temp temperature crossed 70C"), "evt_" + Guid.NewGuid().ToString("N")[..10]));
        audit.PublishLive(AuditMapping.ToRecordDirect(BreachWrite("host-temp", "k10temp", "k10temp temperature crossed 70C"), "evt_" + Guid.NewGuid().ToString("N")[..10])); // suppressed
        audit.PublishLive(AuditMapping.ToRecordDirect(BreachWrite("host-disk", "root", "root disk usage crossed 90%"), "evt_" + Guid.NewGuid().ToString("N")[..10]));

        await f.Webhook.WaitForAsync(2, Timeout);
        Assert.Equal(2, f.Webhook.Requests.Count);
        List<string> bodies = f.Webhook.Requests.Select(r => r.Body).ToList();
        Assert.Single(bodies, b => b.Contains("temperature", StringComparison.Ordinal));
        Assert.Single(bodies, b => b.Contains("disk usage", StringComparison.Ordinal));
    }
}

/// <summary>Boots the real app with the provider's OUTBOUND HTTP swapped for a recording handler, so the
/// full bus → worker → provider delivery path is exercised end-to-end with nothing leaving the process.
/// The provider keeps its real formatting/validation; only the webhook POST is recorded. Singleton so
/// every scope-per-event resolution hits the same recorder.</summary>
public sealed class NotificationDeliveryFactory : AuthTestFactory
{
    public readonly RecordingHandler Webhook = new(HttpStatusCode.OK);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<INotificationProvider>();
            services.AddSingleton<INotificationProvider>(sp => new SlackNotificationProvider(
                new HttpClient(Webhook), sp.GetRequiredService<ILogger<SlackNotificationProvider>>()));
        });
    }
}

/// <summary>An HttpMessageHandler that records every request (uri + body) and signals each arrival on a
/// semaphore — so a test can deterministically wait for N posts (no sleeps).</summary>
public sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
{
    public readonly ConcurrentQueue<RecordedRequest> Requests = new();
    private readonly SemaphoreSlim _arrived = new(0);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new RecordedRequest(request.RequestUri?.ToString() ?? "", body));
        _arrived.Release();
        return new HttpResponseMessage(status);
    }

    /// <summary>Wait until <paramref name="count"/> requests have arrived, or throw on timeout.</summary>
    public async Task WaitForAsync(int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        for (int i = 0; i < count; i++)
            await _arrived.WaitAsync(cts.Token);
    }
}

public sealed record RecordedRequest(string Uri, string Body);
