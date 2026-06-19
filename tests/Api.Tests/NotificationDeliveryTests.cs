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

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M8·c Increment B — the notification delivery worker (the always-on audit tap → providers). Three
/// layers: (1) <see cref="NotificationMappingTests"/> pins the audit-action→catalog map (pure); (2)
/// <see cref="DiscordSendTests"/> proves <see cref="DiscordNotificationProvider.SendAsync"/> formats and
/// posts (faked + recording HTTP); (3) <see cref="NotificationDeliveryE2ETests"/> drives the real
/// pipeline — an audit row appended through <see cref="AuditService"/> reaches a recording webhook — and
/// proves the routing gates (rule-disabled, once/digest, anti-spam suppression) <b>deterministically</b>
/// via a barrier event (a gated event followed by a delivered one; the worker is sequential, so the
/// delivered POST proves the gated one was already processed — no sleeps).
/// </summary>
public sealed class NotificationMappingTests
{
    [Theory]
    [InlineData(AuditAction.ServerStart, "online")]
    [InlineData(AuditAction.ServerRestart, "online")] // a completed restart = back online (closes the auto-heal gap)
    [InlineData(AuditAction.ServerStop, "offline")]
    [InlineData(AuditAction.ServerCrash, "crash")]
    [InlineData(AuditAction.ServerUpdate, "update")]
    [InlineData(AuditAction.ServerInstall, "installed")]
    [InlineData(AuditAction.BackupCreate, "backup")]
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
            AuditAction.ServerCrash, AuditAction.ServerUpdate, AuditAction.ServerInstall, AuditAction.BackupCreate,
        ];
        foreach (string action in notifiable)
            Assert.True(NotificationCatalog.IsKnown(NotificationCatalog.CatalogIdForAction(action)!));
    }
}

/// <summary>The real <see cref="DiscordNotificationProvider.SendAsync"/> with its outbound HTTP recorded —
/// proves the message formatting, the honest failure, and the ops-role ping / allowed_mentions scoping.</summary>
public sealed class DiscordSendTests
{
    private const string Webhook = "https://discord.com/api/webhooks/1/tok";

    private static DiscordNotificationProvider Provider(RecordingHandler handler) =>
        new(new HttpClient(handler), NullLogger<DiscordNotificationProvider>.Instance);

    private static IntegrationRecord Configured(IReadOnlyDictionary<string, string>? settings = null) =>
        IntegrationRecord.Empty("discord") with { Secret = Webhook, Enabled = true, Settings = settings ?? new Dictionary<string, string>() };

    private static NotificationEvent CrashEvent(string server) =>
        new("crash", AuditAction.ServerCrash, server, AuditSeverity.Warn, $"{server} crashed — auto-restarting", DateTimeOffset.UtcNow, "evt_x");

    private static NotificationRule Rule(bool ping = false) => new("crash", Enabled: true, NotificationCadence.Every, ping);

    [Fact]
    public async Task SendAsync_PostsToWebhook_WithServerNameAndSuppressedMentions()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        NotificationDeliveryResult r = await Provider(handler).SendAsync(CrashEvent("factorio-01"), Rule(), Configured(), default);

        Assert.True(r.Ok);
        Assert.True(handler.Requests.TryDequeue(out RecordedRequest? req));
        Assert.Equal(Webhook, req!.Uri);
        Assert.Contains("factorio-01", req.Body);
        Assert.Contains("crashed", req.Body);
        Assert.Contains("\"allowed_mentions\":{\"parse\":[]}", req.Body); // no role array → nothing is pinged
        Assert.DoesNotContain("<@&", req.Body);
    }

    [Fact]
    public async Task SendAsync_Ping_MentionsConfiguredRole_AndScopesAllowedMentions()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        IntegrationRecord rec = Configured(new Dictionary<string, string> { [DiscordNotificationProvider.PingRoleSetting] = "555" });

        NotificationDeliveryResult r = await Provider(handler).SendAsync(CrashEvent("factorio-01"), Rule(ping: true), rec, default);

        Assert.True(r.Ok);
        Assert.True(handler.Requests.TryDequeue(out RecordedRequest? req));
        // Parse the JSON (STJ escapes <, >, & as </>/& — harmless, Discord decodes them).
        JsonElement body = JsonDocument.Parse(req!.Body).RootElement;
        Assert.Contains("<@&555>", body.GetProperty("content").GetString());        // the role is mentioned
        Assert.Equal("555", body.GetProperty("allowed_mentions").GetProperty("roles")[0].GetString()); // scoped to it
    }

    [Fact]
    public async Task SendAsync_Ping_NoRoleConfigured_DoesNotMention()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        // Ping requested but no pingRoleId in Settings → we can't ping a role we don't have (honest, never invented).
        NotificationDeliveryResult r = await Provider(handler).SendAsync(CrashEvent("factorio-01"), Rule(ping: true), Configured(), default);

        Assert.True(r.Ok);
        Assert.True(handler.Requests.TryDequeue(out RecordedRequest? req));
        JsonElement body = JsonDocument.Parse(req!.Body).RootElement;
        Assert.DoesNotContain("<@&", body.GetProperty("content").GetString());
    }

    [Fact]
    public async Task SendAsync_DiscordRejects_HonestFailure()
    {
        NotificationDeliveryResult r = await Provider(new RecordingHandler(HttpStatusCode.InternalServerError))
            .SendAsync(CrashEvent("factorio-01"), Rule(), Configured(), default);
        Assert.False(r.Ok);          // never a fabricated ok
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task SendAsync_NoSecret_HonestFailure()
    {
        NotificationDeliveryResult r = await Provider(new RecordingHandler(HttpStatusCode.NoContent))
            .SendAsync(CrashEvent("factorio-01"), Rule(), IntegrationRecord.Empty("discord"), default);
        Assert.False(r.Ok);
        Assert.Equal("no webhook configured", r.Error);
    }
}

/// <summary>End-to-end: an audit row appended through the real always-on <see cref="AuditService"/> reaches
/// a recording webhook through the bus + worker + provider — and the routing gates hold.</summary>
public sealed class NotificationDeliveryE2ETests
{
    private const string Webhook = "https://discord.com/api/webhooks/777/e2esecrettoken";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static HttpClient AdminClient(NotificationDeliveryFactory f)
    {
        HttpClient c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(AuthTier.Admin));
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
        await c.PatchAsJsonAsync("/api/v1/integrations/discord", new { webhook = Webhook, enabled = true });

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
        await c.PatchAsJsonAsync("/api/v1/integrations/discord", new { webhook = Webhook, enabled = false });

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
        await c.PatchAsJsonAsync("/api/v1/integrations/discord", new
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
        await c.PatchAsJsonAsync("/api/v1/integrations/discord", new
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
        await c.PatchAsJsonAsync("/api/v1/integrations/discord", new { webhook = Webhook, enabled = true });

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
}

/// <summary>Boots the real app with the Discord provider's OUTBOUND HTTP swapped for a recording handler
/// (no real Discord) so the full bus → worker → provider delivery path is exercised end-to-end. The
/// provider keeps its real formatting/validation; only the webhook POST is recorded. Singleton so every
/// scope-per-event resolution hits the same recorder.</summary>
public sealed class NotificationDeliveryFactory : AuthTestFactory
{
    public readonly RecordingHandler Webhook = new(HttpStatusCode.NoContent);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<INotificationProvider>();
            services.AddSingleton<INotificationProvider>(sp => new DiscordNotificationProvider(
                new HttpClient(Webhook), sp.GetRequiredService<ILogger<DiscordNotificationProvider>>()));
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
