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
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Integrations;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The `/integrations/{provider}` HTTP surface through the real pipeline — admin gate, store, envelope —
/// with the provider's outbound webhook POST faked so no real service is called. Everything asserted here
/// is <b>provider-agnostic</b> (the gate, the envelope, the sparse-PATCH semantics, the catalog validation,
/// the never-echoed secret); a provider's own specifics live beside it, in <see cref="SlackProviderTests"/>.
/// Slack is simply the provider these run through.
/// </summary>
/// <remarks>
/// There is no Discord provider to test here. Discord is kgsm-bot's channel — it holds the connection, the
/// per-server channels and the announcement switches — so a second path to it from this API would post
/// every event twice and split one integration's configuration across two components.
/// </remarks>
public sealed class IntegrationsApiTests
{
    private const string Webhook = "https://hooks.slack.com/services/T98765432/B98765432/realsecrettoken";

    private static IntegrationsTestFactory NewFactory() => new();

    private static HttpClient Client(IntegrationsTestFactory f, KgsmTier? tier)
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
        HttpResponseMessage r = await Client(f, null).GetAsync("/api/v1/integrations/slack");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(KgsmTier.Viewer)]
    [InlineData(KgsmTier.Operator)]
    public async Task BelowAdmin_403(KgsmTier tier)
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, tier).GetAsync("/api/v1/integrations/slack");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Admin_List_200_ProviderPresent_Unconfigured()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, KgsmTier.Admin).GetAsync("/api/v1/integrations");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        JsonElement[] rows = (await Json(r)).EnumerateArray().ToArray();
        JsonElement slack = rows.Single(e => e.GetProperty("provider").GetString() == "slack");
        Assert.False(slack.GetProperty("configured").GetBoolean());
        Assert.False(slack.GetProperty("enabled").GetBoolean());
    }

    /// <summary>
    /// A provider id nothing is registered under is a 404 in the frozen envelope. <c>discord</c> is
    /// deliberately one of those: the bot owns that channel, and this asserts the API really has stopped
    /// offering a second route to it rather than merely leaving it unconfigured.
    /// </summary>
    [Theory]
    [InlineData("telegram")]
    [InlineData("discord")]
    public async Task UnknownProvider_404_Envelope(string provider)
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, KgsmTier.Admin).GetAsync($"/api/v1/integrations/{provider}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_then_Get_RoundTrips_SecretMaskedNeverEchoed()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpClient c = Client(f, KgsmTier.Admin);

        HttpResponseMessage patch = await c.PatchAsJsonAsync("/api/v1/integrations/slack", new
        {
            webhook = Webhook,
            channelLabel = "#krystal-ops",
            enabled = true,
            events = new[] { new { id = "backup", enabled = false, cadence = "digest" } },
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        // The PATCH response itself must not leak the raw secret.
        Assert.DoesNotContain("realsecrettoken", await patch.Content.ReadAsStringAsync());

        JsonElement body = await Json(await c.GetAsync("/api/v1/integrations/slack"));
        Assert.True(body.GetProperty("webhook").GetProperty("configured").GetBoolean());
        string hint = body.GetProperty("webhook").GetProperty("hint").GetString()!;
        Assert.StartsWith("…/services/T98765432/B98765432/", hint);
        Assert.DoesNotContain("realsecrettoken", hint);            // never echoed
        Assert.Equal("#krystal-ops", body.GetProperty("channelLabel").GetString());
        Assert.True(body.GetProperty("enabled").GetBoolean());

        JsonElement backup = body.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("id").GetString() == "backup");
        Assert.False(backup.GetProperty("enabled").GetBoolean());   // the sparse change stuck
        Assert.Equal("digest", backup.GetProperty("cadence").GetString());
    }

    [Fact]
    public async Task Patch_BadWebhook_400_Envelope()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, KgsmTier.Admin)
            .PatchAsJsonAsync("/api/v1/integrations/slack", new { webhook = "https://evil.example.com/x" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{\"events\":[{\"id\":\"not-an-event\"}]}")]
    [InlineData("{\"events\":[{\"id\":\"crash\",\"cadence\":\"hourly\"}]}")]
    public async Task Patch_UnknownEventOrCadence_400(string json)
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, KgsmTier.Admin).PatchAsync("/api/v1/integrations/slack",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Test_Unconfigured_409()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpResponseMessage r = await Client(f, KgsmTier.Admin).PostAsync("/api/v1/integrations/slack/test", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("\"code\":\"not_configured\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Test_Configured_202_RealSendFaked()
    {
        using IntegrationsTestFactory f = NewFactory();
        HttpClient c = Client(f, KgsmTier.Admin);
        await c.PatchAsJsonAsync("/api/v1/integrations/slack", new { webhook = Webhook, channelLabel = "#krystal-ops" });

        HttpResponseMessage r = await c.PostAsync("/api/v1/integrations/slack/test", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);   // 202 (the faked webhook returned 200)
        JsonElement body = await Json(r);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("test", body.GetProperty("posted").GetString());
        Assert.Equal("#krystal-ops", body.GetProperty("channelLabel").GetString());
    }

    // An incoming-webhook URL *is* the secret. The provider POSTs to it through the IHttpClientFactory
    // client, whose DEFAULT logging handler logs "POST {uri}" at Information — i.e. it would leak the token
    // to the app log. Production strips those loggers (Startup .RemoveAllLoggers()). This pins the invariant
    // on the channel the body-asserting tests can't see: run the real production client pipeline (only the
    // outbound HTTP is stubbed) at Information level and assert the token never appears in the captured
    // logs. (Drop RemoveAllLoggers and this fails — the regression guard.)
    [Fact]
    public async Task Test_Send_DoesNotLeakWebhookSecretToLogs()
    {
        using var f = new IntegrationsLoggingFactory();
        HttpClient c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(KgsmTier.Admin));

        const string secretToken = "TOPSECRETtoken99999";
        await c.PatchAsJsonAsync("/api/v1/integrations/slack",
            new { webhook = $"https://hooks.slack.com/services/T424242/B424242/{secretToken}" });

        HttpResponseMessage r = await c.PostAsync("/api/v1/integrations/slack/test", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);   // proves the primary-handler stub took (no real send)
        Assert.DoesNotContain(f.Capture.Messages, m => m.Contains(secretToken, StringComparison.Ordinal));
    }
}

/// <summary>A boot of the real app with the provider's OUTBOUND HTTP swapped for a fixed-status stub, so
/// the full store+controller path is exercised with nothing leaving the process. The provider keeps its
/// real Describe/validate logic — only the webhook POST is faked. Fresh DB per instance (per-test
/// isolation).</summary>
public sealed class IntegrationsTestFactory : AuthTestFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<INotificationProvider>();
            services.AddSingleton<INotificationProvider>(new SlackNotificationProvider(
                new HttpClient(new StubHandler(HttpStatusCode.OK)),
                NullLogger<SlackNotificationProvider>.Instance));
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
/// <c>.RemoveAllLoggers()</c> is under test), swapping ONLY the primary handler so no real outbound call
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
                o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new StubHandler(HttpStatusCode.OK))));
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
