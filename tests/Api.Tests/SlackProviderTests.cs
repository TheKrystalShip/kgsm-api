using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M8·c Increment C — Slack, the second provider, validating the webhook-family abstraction
/// (<see cref="WebhookNotificationProvider"/>). <see cref="SlackProviderTests"/> proves the Slack specifics
/// in isolation (mask/validate/format/send, the mrkdwn + escaping care, the subteam ping) with a recording
/// HTTP handler; <see cref="SlackApiTests"/> proves it through the real pipeline (the provider list now
/// shows both, the §-shaped view with no `bot` block, the admin gate, the masked round-trip).
/// </summary>
public sealed class SlackProviderTests
{
    private const string Webhook = "https://hooks.slack.com/services/T00000000/B00000000/abcXYZsecrettoken";

    private static SlackNotificationProvider Provider(RecordingHandler h) =>
        new(new HttpClient(h), NullLogger<SlackNotificationProvider>.Instance);

    private static IntegrationRecord Configured(IReadOnlyDictionary<string, string>? settings = null) =>
        IntegrationRecord.Empty("slack") with
        {
            Secret = Webhook,
            ChannelLabel = "#krystal-ops",
            Enabled = true,
            Settings = settings ?? new Dictionary<string, string>(),
        };

    private static NotificationEvent Online(string server) =>
        new("online", AuditAction.ServerStart, server, AuditSeverity.Info, $"started {server}", DateTimeOffset.UtcNow, "evt_s");

    private static NotificationRule Rule(bool ping = false) => new("online", Enabled: true, NotificationCadence.Every, ping);

    [Fact]
    public void Describe_MasksTheSecret_NoBotField_DeliverableCatalog()
    {
        var view = (SlackIntegrationView)Provider(new RecordingHandler(HttpStatusCode.OK)).Describe(Configured());
        Assert.True(view.Webhook.Configured);
        Assert.Equal("…/services/T00000000/B00000000/abc***", view.Webhook.Hint); // same masking shape as Discord
        Assert.DoesNotContain("secrettoken", view.Webhook.Hint);
        Assert.Equal("#krystal-ops", view.ChannelLabel);
        Assert.Equal(NotificationCatalog.Events.Count, view.Events.Count);
        Assert.Contains(view.Events, e => e.Id == "online");
        Assert.DoesNotContain(view.Events, e => e.Id is "resource" or "join"); // honest catalog
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T/B/x", true)]
    [InlineData("http://hooks.slack.com/services/T/B/x", false)]      // not https
    [InlineData("https://hooks.notslack.com/services/T/B/x", false)]  // wrong host (EndsWith would wrongly accept)
    [InlineData("https://hooks.slack.com/commands/1", false)]         // not a services path
    [InlineData("https://discord.com/api/webhooks/1/tok", false)]     // a Discord webhook is not a Slack one
    [InlineData("not a url", false)]
    public void TryNormalizeSecret_ValidatesSlackWebhooks(string raw, bool valid)
    {
        bool ok = Provider(new RecordingHandler(HttpStatusCode.OK)).TryNormalizeSecret(raw, out string? normalized, out string? error);
        Assert.Equal(valid, ok);
        if (valid) { Assert.Equal(raw, normalized); Assert.Null(error); }
        else { Assert.Null(normalized); Assert.NotNull(error); }
    }

    [Fact]
    public async Task SendAsync_PostsSlackText_MrkdwnBold_On200()
    {
        var h = new RecordingHandler(HttpStatusCode.OK); // Slack returns 200 "ok", not Discord's 204
        NotificationDeliveryResult r = await Provider(h).SendAsync(Online("factorio-01"), Rule(), Configured(), default);

        Assert.True(r.Ok);
        Assert.True(h.Requests.TryDequeue(out RecordedRequest? req));
        Assert.Equal(Webhook, req!.Uri);
        string text = JsonDocument.Parse(req.Body).RootElement.GetProperty("text").GetString()!;
        Assert.Contains("*factorio-01*", text); // single-asterisk mrkdwn (not Discord's **)
        Assert.Contains("is online", text);
    }

    [Fact]
    public async Task SendAsync_EscapesSlackSpecials_InServerName()
    {
        var h = new RecordingHandler(HttpStatusCode.OK);
        await Provider(h).SendAsync(Online("a<b>&c"), Rule(), Configured(), default);

        Assert.True(h.Requests.TryDequeue(out RecordedRequest? req));
        string text = JsonDocument.Parse(req!.Body).RootElement.GetProperty("text").GetString()!;
        Assert.Contains("a&lt;b&gt;&amp;c", text); // &, <, > escaped (Slack parses <…> specially)
        Assert.DoesNotContain("a<b>", text);
    }

    [Fact]
    public async Task SendAsync_Ping_MentionsConfiguredSubteam()
    {
        var h = new RecordingHandler(HttpStatusCode.OK);
        IntegrationRecord rec = Configured(new Dictionary<string, string> { [SlackNotificationProvider.PingSubteamSetting] = "S123" });
        await Provider(h).SendAsync(Online("factorio-01"), Rule(ping: true), rec, default);

        Assert.True(h.Requests.TryDequeue(out RecordedRequest? req));
        string text = JsonDocument.Parse(req!.Body).RootElement.GetProperty("text").GetString()!;
        Assert.StartsWith("<!subteam^S123>", text);
    }

    [Fact]
    public async Task SendAsync_SlackRejects_HonestFailure()
    {
        NotificationDeliveryResult r = await Provider(new RecordingHandler(HttpStatusCode.NotFound))
            .SendAsync(Online("x"), Rule(), Configured(), default);
        Assert.False(r.Ok);          // never a fabricated ok
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task SendAsync_NoSecret_HonestFailure()
    {
        NotificationDeliveryResult r = await Provider(new RecordingHandler(HttpStatusCode.OK))
            .SendAsync(Online("x"), Rule(), IntegrationRecord.Empty("slack"), default);
        Assert.False(r.Ok);
        Assert.Equal("no webhook configured", r.Error);
    }
}

/// <summary>The Slack provider through the real pipeline — proving the abstraction is wired (the provider
/// list now shows both Discord and Slack from the real Startup) and the §-shaped Slack view + admin gate.
/// Uses <see cref="AuthTestFactory"/> directly (no provider/HTTP swap): every assertion here is reachable
/// without an outbound call (list/describe/patch + the unconfigured-`/test` 409 short-circuits before HTTP).</summary>
public sealed class SlackApiTests
{
    private static HttpClient Admin(AuthTestFactory f)
    {
        HttpClient c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(AuthTier.Admin));
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task List_IncludesBothDiscordAndSlack()
    {
        using var f = new AuthTestFactory();
        JsonElement rows = await Json(await Admin(f).GetAsync("/api/v1/integrations"));
        List<string?> ids = rows.EnumerateArray().Select(e => e.GetProperty("provider").GetString()).ToList();
        Assert.Contains("discord", ids); // the abstraction is wired: both real providers are registered & listed
        Assert.Contains("slack", ids);
    }

    [Fact]
    public async Task Get_Slack_NoBotField_Unconfigured()
    {
        using var f = new AuthTestFactory();
        HttpResponseMessage r = await Admin(f).GetAsync("/api/v1/integrations/slack");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        JsonElement body = await Json(r);
        Assert.Equal("slack", body.GetProperty("provider").GetString());
        Assert.False(body.GetProperty("webhook").GetProperty("configured").GetBoolean());
        Assert.False(body.TryGetProperty("bot", out _)); // Slack has no bot block — honest, not a fabricated null
    }

    [Fact]
    public async Task Patch_then_Get_Slack_RoundTrips_SecretMaskedNeverEchoed()
    {
        using var f = new AuthTestFactory();
        HttpClient c = Admin(f);
        const string webhook = "https://hooks.slack.com/services/T11/B22/realslacksecrettoken";

        HttpResponseMessage patch = await c.PatchAsJsonAsync("/api/v1/integrations/slack",
            new { webhook, enabled = true, channelLabel = "#ops" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.DoesNotContain("realslacksecrettoken", await patch.Content.ReadAsStringAsync());

        JsonElement body = await Json(await c.GetAsync("/api/v1/integrations/slack"));
        Assert.True(body.GetProperty("webhook").GetProperty("configured").GetBoolean());
        string hint = body.GetProperty("webhook").GetProperty("hint").GetString()!;
        Assert.StartsWith("…/services/T11/B22/", hint);
        Assert.DoesNotContain("realslacksecrettoken", hint);
        Assert.True(body.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Patch_Slack_BadWebhook_400()
    {
        using var f = new AuthTestFactory();
        HttpResponseMessage r = await Admin(f).PatchAsJsonAsync("/api/v1/integrations/slack",
            new { webhook = "https://discord.com/api/webhooks/1/tok" }); // a Discord URL is not a Slack webhook
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Test_Slack_Unconfigured_409()
    {
        using var f = new AuthTestFactory();
        HttpResponseMessage r = await Admin(f).PostAsync("/api/v1/integrations/slack/test", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("\"code\":\"not_configured\"", await r.Content.ReadAsStringAsync());
    }
}
