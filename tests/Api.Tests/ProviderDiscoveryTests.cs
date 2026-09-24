using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// How a browser surface this node served finds its sign-in provider, and how the panel this node
/// serves becomes a client of it.
/// </summary>
public sealed class ProviderDiscoveryTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private const string Issuer = "https://auth.anchors.test";

    private WebApplicationFactory<Program> KnowingIssuer(string? issuer) => factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClusterSessionKeys>();
            services.AddSingleton<IClusterSessionKeys>(
                AuthTestFactory.PublishedAnchor.Default with { Issuer = issuer });
        }));

    [Fact]
    public async Task The_node_names_the_issuer_it_verifies_against_to_anybody_who_asks()
    {
        using WebApplicationFactory<Program> node = KnowingIssuer(Issuer);
        using HttpClient client = node.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedResourceMetadata.Path);
        request.Headers.Add("Origin", "https://panel.somewhere-else.test");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("http://localhost", document.RootElement.GetProperty("resource").GetString());
        Assert.Equal(Issuer,
            Assert.Single(document.RootElement.GetProperty("authorization_servers").EnumerateArray()).GetString());

        // Readable from any origin — a panel served with no member behind it asks whichever member a
        // person names — and never with credentials, because nothing here depends on who is asking.
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AuthTestFactory.AnchorIssuer)]
    public async Task A_node_knowing_no_provider_a_browser_can_reach_says_so(string? issuer)
    {
        using WebApplicationFactory<Program> node = KnowingIssuer(issuer);
        using HttpClient client = node.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(ProtectedResourceMetadata.Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("no_issuer", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_node_serving_the_panel_announces_it_as_a_client()
    {
        string webRoot = Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-wwwroot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>panel</title>");
        try
        {
            using WebApplicationFactory<Program> node = factory.WithWebHostBuilder(builder => builder.UseWebRoot(webRoot));
            using HttpClient client = node.CreateClient();

            ClusterClientAnnouncement? announced = ClusterClientAnnouncement.Read(
                node.Services.GetRequiredService<SelfPublications>().Current
                    .GetValueOrDefault(ClusterClientAnnouncement.FactKey));

            ClusterClientAnnouncement panel = ClusterClientAnnouncement.ControlPanel;
            Assert.NotNull(announced);
            Assert.Equal(panel.Name, announced.Name);
            Assert.Equal(panel.RedirectPaths, announced.RedirectPaths);
            Assert.Equal(panel.PostLogoutRedirectPaths, announced.PostLogoutRedirectPaths);

            // And the path the provider sends a code to is the panel, not a 404.
            using HttpResponseMessage landing = await client.GetAsync(
                $"{ClusterClientAnnouncement.ControlPanel.RedirectPaths[0]}?code=x&state=y");
            Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
            Assert.Contains("panel", await landing.Content.ReadAsStringAsync());
        }
        finally
        {
            try { Directory.Delete(webRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_node_serving_no_panel_announces_nothing()
    {
        using HttpClient client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<SelfPublications>().Current
            .ContainsKey(ClusterClientAnnouncement.FactKey));
    }
}
