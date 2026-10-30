using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>How a browser surface this node served finds its sign-in provider, and which origins may call it.</summary>
public sealed class ProviderDiscoveryTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    [Fact]
    public async Task The_node_names_the_configured_issuer_to_anybody_who_asks()
    {
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedResourceDocument.Path);
        request.Headers.Add("Origin", "https://panel.somewhere-else.test");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("http://localhost", document.RootElement.GetProperty("resource").GetString());
        Assert.Equal(AuthTestFactory.Provider.Issuer,
            Assert.Single(document.RootElement.GetProperty("authorization_servers").EnumerateArray()).GetString());

        // Readable from any origin — a panel served with no member behind it asks whichever member a
        // person names — and never with credentials, because nothing here depends on who is asking.
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task A_node_with_no_issuer_says_so()
    {
        using WebApplicationFactory<Program> node = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Sessions:Issuer"] = "" })));
        using HttpClient client = node.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(ProtectedResourceDocument.Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("no_issuer", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_configured_origin_is_admitted_across_origins_and_nothing_else_is()
    {
        using WebApplicationFactory<Program> node = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Api:CorsOrigins"] = "https://kgsm.example.test, https://other.example.test" })));
        using HttpClient client = node.CreateClient();

        async Task<HttpResponseMessage> PreflightFrom(string origin)
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/servers");
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            preflight.Headers.Add("Access-Control-Request-Headers", "authorization");
            return await client.SendAsync(preflight);
        }

        using HttpResponseMessage admitted = await PreflightFrom("https://kgsm.example.test");
        Assert.Equal("https://kgsm.example.test", admitted.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(admitted.Headers.Contains("Access-Control-Allow-Credentials"));

        using HttpResponseMessage refused = await PreflightFrom("https://elsewhere.test");
        Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
