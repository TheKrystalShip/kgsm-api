using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Cluster.Identity;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Another member of the cluster calling this node: as itself, with the service token its cluster secret
/// signs, or for a person, with that person's own access token.
/// </summary>
public sealed class MemberAuthenticationTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private const string ClusterSecret = "member-authentication-test-secret";

    private WebApplicationFactory<Program> Node(string secret = ClusterSecret) =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cluster:Secret"] = secret,
                ["Api:NodeId"] = "member-authentication-node",
            })));

    private static HttpRequestMessage Get(string path, string token)
    {
        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task A_member_acting_as_itself_is_trusted_with_every_action()
    {
        using WebApplicationFactory<Program> node = Node();
        string token = node.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
        using HttpClient client = node.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/servers", token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/integrations", token))).StatusCode);
    }

    [Fact]
    public async Task A_member_is_named_by_its_id_in_the_audit_actor()
    {
        using WebApplicationFactory<Program> node = Node();
        string token = node.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
        using HttpClient client = node.CreateClient();

        using HttpResponseMessage response = await client.SendAsync(Get("/api/v1/me", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement me = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.StartsWith("member:", me.GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_service_token_signed_with_another_secret_is_refused()
    {
        using WebApplicationFactory<Program> node = Node();
        using WebApplicationFactory<Program> stranger = Node("a-different-cluster-secret");
        string forged = stranger.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
        using HttpClient client = node.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/servers", forged))).StatusCode);
    }

    [Fact]
    public async Task A_member_acting_for_a_person_sends_the_persons_token_and_is_held_to_it()
    {
        using WebApplicationFactory<Program> node = Node();
        using HttpClient client = node.CreateClient();
        string persons = factory.AccessToken(Persona.Reader);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/servers", persons))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get("/api/v1/integrations", persons))).StatusCode);
    }
}
