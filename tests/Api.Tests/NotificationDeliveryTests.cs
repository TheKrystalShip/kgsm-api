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
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Integrations;

using TheKrystalShip.KGSM.Auth;

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
    [InlineData(AuditAction.ServerStart, "online")]
    [InlineData(AuditAction.ServerRestart, "online")] // a completed restart = back online (closes the auto-heal gap)
    [InlineData(AuditAction.ServerStop, "offline")]
    [InlineData(AuditAction.ServerCrash, "crash")]
    [InlineData(AuditAction.ServerUpdate, "update")]
    [InlineData(AuditAction.ServerUpdateAvailable, "update_available")]
    [InlineData(AuditAction.ServerInstall, "installed")]
    [InlineData(AuditAction.BackupCreate, "backup")]
    [InlineData(AuditAction.HostThresholdBreach, "threshold_breach")]
    [InlineData(AuditAction.HostThresholdClear, "threshold_clear")] // separate ids: the all-clear is its own choice
    public void CatalogIdForAction_MapsNotifiableActions(string action, string expected) =>
        Assert.Equal(expected, NotificationCatalog.CatalogIdForAction(action));

    [Theory]
    [InlineData(AuditAction.ServerUninstall)]
    [InlineData(AuditAction.BackupRestore)]
    [InlineData(AuditAction.NetworkPortsOpen)]
    [InlineData(AuditAction.NetworkPortsClose)]
    [InlineData(AuditAction.AuthLogin)]
    [InlineData(AuditAction.AuthLogout)]
    public void CatalogIdForAction_DropsNonNotifiable(string action) =>
        Assert.Null(NotificationCatalog.CatalogIdForAction(action));

    [Fact]
    public void CatalogIdForAction_OnlyEverMapsToKnownCatalogEvents()
    {
        string[] notifiable =
        [
            AuditAction.ServerStart, AuditAction.ServerRestart, AuditAction.ServerStop,
            AuditAction.ServerCrash, AuditAction.ServerUpdate, AuditAction.ServerUpdateAvailable,
            AuditAction.ServerInstall, AuditAction.BackupCreate,
            AuditAction.HostThresholdBreach, AuditAction.HostThresholdClear,
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
        bus.Publish(ThresholdRow(AuditAction.HostThresholdBreach, "host-temp", "k10temp/Tctl", DateTimeOffset.UtcNow));
        bus.Publish(ThresholdRow(AuditAction.HostThresholdBreach, "host-disk", "/", DateTimeOffset.UtcNow));

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
            Action: AuditAction.ServerCrash, Severity: AuditSeverity.Warn,
            Target: new AuditTarget(AuditTargetKind.Server, "srv", "srv"),
            ServerId: "srv", HostId: "test-host", Summary: "srv crashed", Meta: null));

        NotificationEvent only = Assert.Single(await DrainAsync(bus, 1));
        Assert.Null(only.SubjectKey);
    }

    [Theory]
    [InlineData("unwatched")]   // the rule was retuned or switched off — nobody measured a recovery
    [InlineData("interrupted")] // the monitor restarted while it was firing — same
    public async Task Clear_ThatDidNotRecover_IsNotAnnounced(string reason)
    {
        NotificationBus bus = NewBus();
        bus.Publish(ThresholdRow(AuditAction.HostThresholdClear, "host-temp", "k10temp/Tctl", DateTimeOffset.UtcNow, reason));
        // The barrier: a real recovery published after it. The channel is FIFO, so receiving this one
        // proves the first was dropped rather than merely slow.
        bus.Publish(ThresholdRow(AuditAction.HostThresholdClear, "host-disk", "/", DateTimeOffset.UtcNow));

        NotificationEvent only = Assert.Single(await DrainAsync(bus, 1));
        Assert.Contains("host-disk", only.SubjectKey);
    }

    [Fact]
    public async Task Episode_OlderThanTheNoticeWindow_IsNotAnnounced()
    {
        NotificationBus bus = NewBus();
        // What a cold start transcribing yesterday's episodes looks like.
        bus.Publish(ThresholdRow(AuditAction.HostThresholdBreach, "host-temp", "k10temp/Tctl",
            DateTimeOffset.UtcNow.AddHours(-6)));
        bus.Publish(ThresholdRow(AuditAction.HostThresholdBreach, "host-disk", "/", DateTimeOffset.UtcNow));

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
            Action: AuditAction.ServerCrash, Severity: AuditSeverity.Warn,
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
        Action: AuditAction.ServerCrash, Severity: AuditSeverity.Warn,
        Target: new AuditTarget(AuditTargetKind.Server, server, server),
        ServerId: server, HostId: "test-host", Summary: $"{server} crashed — auto-restarting", Meta: null);

    private static AuditWrite StartWrite(string server) => new(
        Ts: DateTimeOffset.UtcNow, Origin: AuditOrigin.Api,
        Actor: new AuditActor(ActorKind.Token, "tester", ActorProvider.Api),
        Action: AuditAction.ServerStart, Severity: AuditSeverity.Info,
        Target: new AuditTarget(AuditTargetKind.Server, server, server),
        ServerId: server, HostId: "test-host", Summary: $"started {server}", Meta: null);

    [Fact]
    public async Task Crash_AuditRow_DeliversNotification()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new { webhook = Webhook, enabled = true });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        await audit.AppendAsync(CrashWrite("factorio-01"));

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
        await audit.AppendAsync(CrashWrite("factorio-01"));

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
        await audit.AppendAsync(CrashWrite("srv-a")); // gated by the disabled rule
        await audit.AppendAsync(StartWrite("srv-a")); // the barrier — delivers, proving the worker passed the crash

        await f.Webhook.WaitForAsync(1, Timeout);
        RecordedRequest only = Assert.Single(f.Webhook.Requests);
        Assert.Contains("is online", only.Body);
        Assert.DoesNotContain("crashed", only.Body); // the crash was gated, not delivered
    }

    [Fact]
    public async Task OnceCadence_Gated_OnlineStillDelivers()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new
        {
            webhook = Webhook,
            enabled = true,
            events = new[] { new { id = "crash", cadence = "once" } }, // once/digest deliver nothing in B
        });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        await audit.AppendAsync(CrashWrite("srv-b")); // gated (cadence once, deferred to C)
        await audit.AppendAsync(StartWrite("srv-b")); // the barrier — every → delivers

        await f.Webhook.WaitForAsync(1, Timeout);
        RecordedRequest only = Assert.Single(f.Webhook.Requests);
        Assert.Contains("is online", only.Body);
        Assert.DoesNotContain("crashed", only.Body);
    }

    [Fact]
    public async Task RepeatedCrash_SuppressedWithinWindow()
    {
        using var f = new NotificationDeliveryFactory();
        HttpClient c = AdminClient(f);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new { webhook = Webhook, enabled = true });

        AuditService audit = f.Services.GetRequiredService<AuditService>();
        await audit.AppendAsync(CrashWrite("srv-c")); // crash#1 → delivers
        await audit.AppendAsync(CrashWrite("srv-c")); // crash#2 → suppressed (same provider:server:catalog, within 60s)
        await audit.AppendAsync(StartWrite("srv-c")); // online → different catalog key → delivers (barrier)

        await f.Webhook.WaitForAsync(2, Timeout); // exactly crash#1 + online; crash#2 never posts
        Assert.Equal(2, f.Webhook.Requests.Count);
        List<string> bodies = f.Webhook.Requests.Select(r => r.Body).ToList();
        Assert.Single(bodies, b => b.Contains("crashed", StringComparison.Ordinal));  // one crash, not two
        Assert.Single(bodies, b => b.Contains("is online", StringComparison.Ordinal));
    }

    private static AuditWrite BreachWrite(string ruleKey, string sensor, string summary) => new(
        Ts: DateTimeOffset.UtcNow, Origin: AuditOrigin.System,
        Actor: new AuditActor(ActorKind.System, "monitor", ActorProvider.System),
        Action: AuditAction.HostThresholdBreach, Severity: AuditSeverity.Warn,
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
        await audit.AppendAsync(BreachWrite("host-temp", "k10temp", "k10temp temperature crossed 70C"));
        await audit.AppendAsync(BreachWrite("host-temp", "k10temp", "k10temp temperature crossed 70C")); // suppressed
        await audit.AppendAsync(BreachWrite("host-disk", "root", "root disk usage crossed 90%"));

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
