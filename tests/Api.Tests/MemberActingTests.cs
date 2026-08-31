using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Cluster.Identity;

using Xunit;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Another member of the cluster acting for somebody who never signed in to this node.
/// </summary>
/// <remarks>
/// The cluster's assistant answers about servers on machines it does not run, and somebody asking it
/// something in Discord holds no session anywhere. What is under test is the boundary that makes that
/// safe: the caller says <em>who</em>, this node decides <em>what</em>, and neither half is taken from
/// the other's word.
/// </remarks>
public sealed class MemberActingTests : IClassFixture<AuthTestFactory>
{
    private const string ClusterSecret = "member-acting-test-secret";

    private readonly AuthTestFactory _base;

    public MemberActingTests(AuthTestFactory factory) => _base = factory;

    private WebApplicationFactory<Program> Node() =>
        _base.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cluster:Secret"] = ClusterSecret,
                ["Api:NodeId"] = "member-acting-node",
            })));

    private static string MemberToken(WebApplicationFactory<Program> app) =>
        app.Services.GetRequiredService<IClusterTokenService>().Mint().Token;

    private static KgsmIdentity Somebody(string subject) =>
        new(KgsmActorProvider.Discord, subject, subject, subject, null, []);

    private static HttpRequestMessage Acting(string path, string memberToken, string handle)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        request.Headers.TryAddWithoutValidation(MemberActing.ActingHandleHeader, handle);
        return request;
    }

    [Fact]
    public async Task A_member_acts_for_a_person_and_this_node_decides_what_they_may_do()
    {
        using WebApplicationFactory<Program> node = Node();
        KgsmIdentity person = Somebody("245717107596197888");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Operator);

        using HttpClient client = node.CreateClient();
        using HttpResponseMessage response =
            await client.SendAsync(Acting("/api/v1/me", MemberToken(node), person.Handle));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The tier came from this node's own replica. Nothing in the request said what it should be,
        // which is the whole difference between this and forwarding an authority.
        JsonElement me = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("operator", me.GetProperty("tier").GetString());
        Assert.Equal(person.Handle, me.GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_member_token_alone_is_not_a_person()
    {
        // It authenticates a machine. Without somebody named, there is nobody to act as, and treating
        // the member itself as a caller would give every member a person's authority on every node.
        using WebApplicationFactory<Program> node = Node();
        using HttpClient client = node.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MemberToken(node));

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Naming_somebody_without_being_a_member_is_nothing_at_all()
    {
        using WebApplicationFactory<Program> node = Node();
        KgsmIdentity person = Somebody("245717107596197889");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Admin);

        using HttpClient client = node.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.TryAddWithoutValidation(MemberActing.ActingHandleHeader, person.Handle);

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_person_this_node_has_no_account_for_is_refused_rather_than_invented()
    {
        // What a username collision looks like from the far end: somebody the cluster knows, resolving
        // to nobody here. Provisioning an account from an assertion would let any member create one.
        using WebApplicationFactory<Program> node = Node();
        using HttpClient client = node.CreateClient();

        using HttpResponseMessage response = await client.SendAsync(
            Acting("/api/v1/me", MemberToken(node), "discord:000000000000000000"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_cannot_be_acted_for()
    {
        using WebApplicationFactory<Program> node = Node();
        KgsmIdentity person = Somebody("245717107596197890");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Admin, UserStatus.Disabled);

        using HttpClient client = node.CreateClient();
        using HttpResponseMessage response =
            await client.SendAsync(Acting("/api/v1/me", MemberToken(node), person.Handle));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_forged_member_token_is_refused()
    {
        // The secret is what makes a caller a member. A token signed with anything else is not one,
        // however well-formed the identity it names.
        using WebApplicationFactory<Program> node = Node();
        KgsmIdentity person = Somebody("245717107596197891");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Admin);

        using WebApplicationFactory<Program> stranger = _base.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cluster:Secret"] = "a-different-cluster-entirely",
                ["Api:NodeId"] = "not-in-this-cluster",
            })));

        using HttpClient client = node.CreateClient();
        using HttpResponseMessage response =
            await client.SendAsync(Acting("/api/v1/me", MemberToken(stranger), person.Handle));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
