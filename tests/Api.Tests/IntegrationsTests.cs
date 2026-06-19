using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Integrations;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M8·c Increment A — the outbound-notification integration (`/integrations/{provider}`). Two layers:
/// (1) <see cref="DiscordProviderTests"/> proves the real <see cref="DiscordNotificationProvider"/> in
/// isolation (mask/validate/test-send with a faked HttpMessageHandler — no real Discord); (2)
/// <see cref="IntegrationsApiTests"/> drives the real pipeline (store + admin gate + the envelope) with
/// the provider's HTTP faked. Load-bearing honesty: the webhook secret is NEVER echoed, the catalog lists
/// only deliverable events, `bot` is null, and `/test` is honest (no faked ok).
/// </summary>
public sealed class DiscordProviderTests
{
    private static DiscordNotificationProvider Provider(HttpStatusCode sendStatus) =>
        new(new HttpClient(new StubHandler(sendStatus)), NullLogger<DiscordNotificationProvider>.Instance);

    private static IntegrationRecord Configured() =>
        IntegrationRecord.Empty("discord") with
        {
            Secret = "https://discord.com/api/webhooks/123456789/abcXYZsecrettoken",
            ChannelLabel = "#krystal-ops",
            Enabled = true,
        };

    [Fact]
    public void Describe_MasksTheSecret_BotNull_OnlyDeliverableEvents()
    {
        var view = (DiscordIntegrationView)Provider(HttpStatusCode.NoContent).Describe(Configured());

        Assert.True(view.Webhook.Configured);
        Assert.Equal("…/webhooks/123456789/abc***", view.Webhook.Hint);
        Assert.DoesNotContain("secrettoken", view.Webhook.Hint);   // the token is never echoed
        Assert.Null(view.Bot);                                      // one-way webhook only (honest)
        Assert.Equal("#krystal-ops", view.ChannelLabel);
        Assert.True(view.Enabled);

        Assert.Equal(NotificationCatalog.Events.Count, view.Events.Count);
        Assert.Contains(view.Events, e => e.Id == "online");
        Assert.Contains(view.Events, e => e.Id == "crash");
        Assert.DoesNotContain(view.Events, e => e.Id is "resource" or "join");  // no honest source → omitted
        // Default overlay for an unconfigured event: enabled, every, no ping.
        IntegrationEventView online = view.Events.Single(e => e.Id == "online");
        Assert.True(online.Enabled);
        Assert.Equal("every", online.Cadence);
        Assert.False(online.Ping);
    }

    [Fact]
    public void Describe_Unconfigured_WebhookNotConfigured_NoHint()
    {
        var view = (DiscordIntegrationView)Provider(HttpStatusCode.NoContent).Describe(IntegrationRecord.Empty("discord"));
        Assert.False(view.Webhook.Configured);
        Assert.Null(view.Webhook.Hint);
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/tok", true)]
    [InlineData("https://discordapp.com/api/webhooks/1/tok", true)]
    [InlineData("http://discord.com/api/webhooks/1/tok", false)]   // not https
    [InlineData("https://evil.example.com/api/webhooks/1/tok", false)] // wrong host
    [InlineData("https://discord.com/channels/1/2", false)]        // not a webhook path
    [InlineData("not a url", false)]
    public void TryNormalizeSecret_ValidatesWebhookUrls(string raw, bool valid)
    {
        bool ok = Provider(HttpStatusCode.NoContent).TryNormalizeSecret(raw, out string? normalized, out string? error);
        Assert.Equal(valid, ok);
        if (valid) { Assert.Equal(raw, normalized); Assert.Null(error); }
        else { Assert.Null(normalized); Assert.NotNull(error); }
    }

    [Fact]
    public async Task TestAsync_Success_PostsAndReportsOk()
    {
        NotificationTestResult r = await Provider(HttpStatusCode.NoContent).TestAsync(Configured(), default);
        Assert.True(r.Ok);
        Assert.Equal("test", r.Posted);
        Assert.Equal("#krystal-ops", r.ChannelLabel);
    }

    [Fact]
    public async Task TestAsync_DiscordRejects_HonestFailure()
    {
        NotificationTestResult r = await Provider(HttpStatusCode.InternalServerError).TestAsync(Configured(), default);
        Assert.False(r.Ok);          // never a fabricated ok
        Assert.NotNull(r.Error);
        Assert.Null(r.Posted);
    }

    [Fact]
    public async Task TestAsync_NoSecret_HonestFailure()
    {
        NotificationTestResult r = await Provider(HttpStatusCode.NoContent).TestAsync(IntegrationRecord.Empty("discord"), default);
        Assert.False(r.Ok);
        Assert.Equal("no webhook configured", r.Error);
    }
}

/// <summary>The `/integrations` HTTP surface through the real pipeline (admin gate, store, envelope),
/// with the Discord provider's outbound HTTP faked (a 204 webhook) so no real Discord is hit.</summary>
public sealed class IntegrationsApiTests
{
    private const string Webhook = "https://discord.com/api/webhooks/987654321/realsecrettoken";

    private static IntegrationsTestFactory NewFactory() => new();

    private static HttpClient Client(IntegrationsTestFactory f, AuthTier? tier)
    {
        HttpClient c = f.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(t));
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task NoToken_401()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, null).GetAsync("/api/v1/integrations/discord");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(AuthTier.Viewer)]
    [InlineData(AuthTier.Operator)]
    public async Task BelowAdmin_403(AuthTier tier)
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, tier).GetAsync("/api/v1/integrations/discord");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Admin_List_200_DiscordPresent_Unconfigured()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, AuthTier.Admin).GetAsync("/api/v1/integrations");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        JsonElement[] rows = (await Json(r)).EnumerateArray().ToArray();
        JsonElement discord = rows.Single(e => e.GetProperty("provider").GetString() == "discord");
        Assert.False(discord.GetProperty("configured").GetBoolean());
        Assert.False(discord.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task UnknownProvider_404_Envelope()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, AuthTier.Admin).GetAsync("/api/v1/integrations/telegram");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_then_Get_RoundTrips_SecretMaskedNeverEchoed()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpClient c = Client(f, AuthTier.Admin);

        HttpResponseMessage patch = await c.PatchAsJsonAsync("/api/v1/integrations/discord", new
        {
            webhook = Webhook,
            channelLabel = "#krystal-ops",
            enabled = true,
            events = new[] { new { id = "backup", enabled = false, cadence = "digest" } },
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        // The PATCH response itself must not leak the raw secret.
        Assert.DoesNotContain("realsecrettoken", await patch.Content.ReadAsStringAsync());

        JsonElement body = await Json(await c.GetAsync("/api/v1/integrations/discord"));
        Assert.True(body.GetProperty("webhook").GetProperty("configured").GetBoolean());
        string hint = body.GetProperty("webhook").GetProperty("hint").GetString()!;
        Assert.StartsWith("…/webhooks/987654321/", hint);
        Assert.DoesNotContain("realsecrettoken", hint);            // never echoed
        Assert.Equal("#krystal-ops", body.GetProperty("channelLabel").GetString());
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.True(body.TryGetProperty("bot", out JsonElement bot) && bot.ValueKind == JsonValueKind.Null);

        JsonElement backup = body.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("id").GetString() == "backup");
        Assert.False(backup.GetProperty("enabled").GetBoolean());   // the sparse change stuck
        Assert.Equal("digest", backup.GetProperty("cadence").GetString());
    }

    [Fact]
    public async Task Patch_BadWebhook_400_Envelope()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, AuthTier.Admin)
            .PatchAsJsonAsync("/api/v1/integrations/discord", new { webhook = "https://evil.example.com/x" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{\"events\":[{\"id\":\"not-an-event\"}]}")]
    [InlineData("{\"events\":[{\"id\":\"crash\",\"cadence\":\"hourly\"}]}")]
    public async Task Patch_UnknownEventOrCadence_400(string json)
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, AuthTier.Admin).PatchAsync("/api/v1/integrations/discord",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Test_Unconfigured_409()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, AuthTier.Admin).PostAsync("/api/v1/integrations/discord/test", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("\"code\":\"not_configured\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Test_Configured_202_RealSendFaked()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpClient c = Client(f, AuthTier.Admin);
        await c.PatchAsJsonAsync("/api/v1/integrations/discord", new { webhook = Webhook, channelLabel = "#krystal-ops" });

        HttpResponseMessage r = await c.PostAsync("/api/v1/integrations/discord/test", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);   // 202 (the faked webhook returned 204)
        JsonElement body = await Json(r);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("test", body.GetProperty("posted").GetString());
        Assert.Equal("#krystal-ops", body.GetProperty("channelLabel").GetString());
    }

    // A Discord webhook URL *is* the secret (.../webhooks/{id}/{token}). The provider POSTs to it through
    // the IHttpClientFactory client, whose DEFAULT logging handler logs "POST {uri}" at Information — i.e.
    // it would leak the token to the app log. Production strips those loggers (Startup .RemoveAllLoggers()).
    // This pins the invariant on the channel the body-asserting tests can't see: run the real production
    // client pipeline (only the outbound HTTP is stubbed) at Information level and assert the token never
    // appears in the captured logs. (Drop RemoveAllLoggers and this fails — the regression guard.)
    [Fact]
    public async Task Test_Send_DoesNotLeakWebhookSecretToLogs()
    {
        using var f = new IntegrationsLoggingFactory();
        HttpClient c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(AuthTier.Admin));

        const string secretToken = "TOPSECRETtoken99999";
        await c.PatchAsJsonAsync("/api/v1/integrations/discord",
            new { webhook = $"https://discord.com/api/webhooks/424242/{secretToken}" });

        HttpResponseMessage r = await c.PostAsync("/api/v1/integrations/discord/test", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);   // proves the primary-handler stub took (no real Discord)
        Assert.DoesNotContain(f.Capture.Messages, m => m.Contains(secretToken, StringComparison.Ordinal));
    }
}

/// <summary>A boot of the real app with the Discord provider's OUTBOUND HTTP swapped for a fixed-status
/// stub (no real Discord), so the full store+controller path is exercised. Its provider keeps the real
/// Describe/validate logic — only the webhook POST is faked. Fresh DB per instance (per-test isolation).</summary>
public sealed class IntegrationsTestFactory : AuthTestFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<INotificationProvider>();
            services.AddSingleton<INotificationProvider>(new DiscordNotificationProvider(
                new HttpClient(new StubHandler(HttpStatusCode.NoContent)),
                NullLogger<DiscordNotificationProvider>.Instance));
        });
    }
}

/// <summary>An HttpMessageHandler that returns a fixed status for every request — the webhook send stub.</summary>
internal sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status));
}

/// <summary>Boots the app keeping the REAL production notification HttpClient (so its
/// <c>.RemoveAllLoggers()</c> is under test), swapping ONLY the primary handler so no real Discord call
/// is made (the named client is "INotificationProvider" — the AddHttpClient&lt;INotificationProvider,…&gt;
/// type name). Captures all logs at Information so a test can assert the webhook token never appears.</summary>
public sealed class IntegrationsLoggingFactory : AuthTestFactory
{
    public readonly CaptureLoggerProvider Capture = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.Configure<HttpClientFactoryOptions>("INotificationProvider", o =>
                o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new StubHandler(HttpStatusCode.NoContent))));
        builder.ConfigureLogging(lb =>
        {
            lb.AddProvider(Capture);
            lb.SetMinimumLevel(LogLevel.Information);
        });
    }
}

/// <summary>An ILoggerProvider that captures every formatted message into a queue for assertions.</summary>
public sealed class CaptureLoggerProvider : ILoggerProvider
{
    public readonly ConcurrentQueue<string> Messages = new();
    public ILogger CreateLogger(string categoryName) => new Capturing(Messages);
    public void Dispose() { }

    private sealed class Capturing(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Enqueue(formatter(state, exception));
    }
}
