using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>GET /api/v1/me/access</c>: what the caller may do on this node, which is the actions their token
/// lists among the ones this node performs.
/// </summary>
public sealed class MeAccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-api-me-access-" + Guid.NewGuid().ToString("N"));

    public MeAccessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A node whose manifests declare two engine actions.</summary>
    private WebApplicationFactory<Program> Node(AuthTestFactory factory)
    {
        string manifests = Path.Combine(_dir, "actions");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, "kgsm.json"), """
            {"schemaVersion":1,"component":"kgsm","actions":[
              {"id":"library.read","title":"Read the library","effect":"read"},
              {"id":"server.start","title":"Start a server","effect":"write"}]}
            """);

        return factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ManifestSources>();
            services.AddSingleton(new ManifestSources([manifests]));
        }));
    }

    private static async Task<JsonElement> ReportAsync(HttpClient client, string token)
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/me/access");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task TheReport_ListsTheTokensActionsThisNodePerforms()
    {
        await using AuthTestFactory factory = new();
        using WebApplicationFactory<Program> node = Node(factory);
        string token = factory.AccessTokenWith(TestIdentity.Person, ["kgsm:library.read", "auth:roles.edit", "kgsm:unheard.of"]);

        JsonElement report = await ReportAsync(node.CreateClient(), token);

        Assert.Equal("kgsm:library.read", Assert.Single(report.GetProperty("cluster").EnumerateArray()).GetString());
        Assert.False(report.GetProperty("owner").GetBoolean());
        Assert.True(report.GetProperty("current").GetBoolean());
        Assert.Empty(report.GetProperty("nodes").EnumerateObject());
        Assert.Empty(report.GetProperty("instances").EnumerateObject());
    }

    [Fact]
    public async Task ATokenListingNothing_ReportsNothing()
    {
        await using AuthTestFactory factory = new();
        using WebApplicationFactory<Program> node = Node(factory);

        JsonElement report = await ReportAsync(node.CreateClient(), factory.AccessToken(Persona.None));

        Assert.Empty(report.GetProperty("cluster").EnumerateArray());
    }

    [Fact]
    public async Task TheReport_FollowsTheTokenItIsAskedWith()
    {
        await using AuthTestFactory factory = new();
        using WebApplicationFactory<Program> node = Node(factory);
        HttpClient client = node.CreateClient();

        JsonElement before = await ReportAsync(client, factory.AccessTokenWith(TestIdentity.Person, ["kgsm:library.read"]));
        JsonElement after = await ReportAsync(client, factory.AccessTokenWith(TestIdentity.Person, ["kgsm:library.read", "kgsm:server.start"]));

        Assert.Single(before.GetProperty("cluster").EnumerateArray());
        Assert.Equal(2, after.GetProperty("cluster").GetArrayLength());
    }
}
