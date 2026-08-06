using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The assistant capability's <c>info.url</c> — the public origin a browser reaches the assistant leaf on.
/// The Control Panel's chat talks to the leaf directly, so this is the whole of how it learns where; the
/// API's own <c>AssistantBaseUrl</c> is a loopback route and is never handed to a browser.
/// </summary>
public sealed class AssistantPublicUrlTests
{
    private const string Host = AuthTestFactory.HostId;
    private const string PublicUrl = "https://assistant.example.com";

    [Fact]
    public async Task Configured_PublicUrl_IsReportedAsCapabilityInfo()
    {
        using var factory = new PublicUrlFactory(PublicUrl);
        JsonElement assistant = await AssistantCapability(factory);

        Assert.True(assistant.GetProperty("provisioned").GetBoolean());
        Assert.Equal(PublicUrl, assistant.GetProperty("info").GetProperty("url").GetString());
    }

    // No configured origin means no browser route. The capability must say nothing rather than fall back to
    // the loopback base URL, which no browser can reach and which would read as a route that exists.
    [Fact]
    public async Task Unconfigured_PublicUrl_ReportsNoInfo()
    {
        using var factory = new PublicUrlFactory(null);
        JsonElement assistant = await AssistantCapability(factory);

        Assert.True(assistant.GetProperty("provisioned").GetBoolean());
        Assert.False(assistant.TryGetProperty("info", out _));
    }

    private static async Task<JsonElement> AssistantCapability(PublicUrlFactory factory)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(KgsmTier.Viewer));
        HttpResponseMessage resp = await c.GetAsync($"/api/v1/hosts/{Host}");
        JsonElement host = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        return host.GetProperty("capabilities").GetProperty("assistant");
    }

    /// <summary>An API with the assistant provisioned (a base URL is what provisions it) and the public
    /// origin set or left blank.</summary>
    private sealed class PublicUrlFactory(string? publicUrl) : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:AssistantBaseUrl"] = "http://127.0.0.1:65535",   // provisions it; never dialled here
                    ["Api:AssistantPublicUrl"] = publicUrl,
                }));
        }
    }
}
