using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// A node whose cluster has an auth anchor stops answering for accounts.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of endpoint close and they fail for the same reason from opposite directions. A door
/// somebody signs in through would mint a session scoped to this node alone, leaving them signed in
/// on one machine and a stranger on every other — the state one sign-in for the cluster exists to
/// end. A write to an account would land in this node's replica, unversioned by the anchor, and be
/// overwritten by the next thing the anchor publishes: it would appear to work and then quietly stop
/// having happened.
/// </para>
/// <para>
/// Everything that reads, and everything that ends a session, stays open. Revoking takes authority
/// away rather than granting it.
/// </para>
/// </remarks>
public sealed class AnchorHoldsAuthTests
{
    private const string Secret = "anchor-holds-auth-secret";

    private static async Task AnchorIn(ClusterNodeFactory factory, string memberId, string? signInUrl)
    {
        await factory.Services.GetRequiredService<ClusterStateStore>()
            .AssignAsync(ClusterCapability.Auth, memberId, default);

        await factory.Services.GetRequiredService<MembersStore>().UpsertAsync(
            MemberRow.New(memberId, MemberKind.Anchor) with
            {
                Id = "member_" + memberId,
                Url = "https://members-reach-it-here.example",
                Status = "reachable",
                Published = signInUrl is null
                    ? ""
                    : PublishedFacts.Encode(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["auth.url"] = signInUrl,
                    }),
            },
            default);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path) =>
        new(method, path) { Content = method == HttpMethod.Get ? null : Json() };

    private static StringContent Json() => new("{}", Encoding.UTF8, "application/json");

    /// <summary>Every door a person could be let in through, and every write to an account.</summary>
    public static TheoryData<string, string> Closed => new()
    {
        { "GET", "/auth/providers" },
        { "GET", "/auth/discord/start" },
        { "GET", "/auth/discord/callback" },
        { "POST", "/auth/login" },
        { "POST", "/auth/register" },
        { "POST", "/auth/session/refresh" },
        { "POST", "/auth/users" },
        { "PATCH", "/auth/users/usr_someone" },
        { "DELETE", "/auth/users/usr_someone" },
        { "POST", "/auth/users/usr_someone/password" },
        { "POST", "/auth/password" },
        { "POST", "/auth/reauth" },
        { "POST", "/auth/identities/discord/start" },
        { "GET", "/auth/identities/discord/callback" },
        { "DELETE", "/auth/identities/cred_something" },
    };

    [Theory]
    [MemberData(nameof(Closed))]
    public async Task A_node_whose_cluster_has_an_anchor_answers_for_no_account(string method, string path)
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await AnchorIn(node, "hotrod-auth", "https://auth.example");
        using HttpClient client = node.CreateClient();

        // An admin bearer, because authorization runs before an action filter: without one the
        // gated admin endpoints answer 401 and the gate is never reached. The point under test is
        // that somebody who IS allowed still cannot use this door.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AuthTestFactory.MintTokenWithRow(node.Services, KgsmTier.Admin, access: true));

        HttpResponseMessage response = await client.SendAsync(Request(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("auth_held_by_anchor", body.GetProperty("error").GetProperty("code").GetString());

        // A client can route on the headers rather than reading English to find out where to go.
        Assert.Equal("hotrod-auth", response.Headers.GetValues("X-Kgsm-Auth-Holder").Single());
        Assert.Equal("https://auth.example", response.Headers.GetValues("X-Kgsm-Auth-Url").Single());
    }

    [Fact]
    public async Task A_standalone_host_is_untouched()
    {
        // Most installs are one machine. The accounts are its own, it is the only door there is, and
        // closing it would lock somebody out of their own server.
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        using HttpClient client = node.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/auth/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_cluster_with_no_anchor_is_untouched()
    {
        // Clustered and nobody holds the accounts: there is nowhere else to send anybody, which is
        // the same fact as being standalone from this node's point of view.
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await node.Services.GetRequiredService<ClusterStateStore>()
            .AssignAsync(ClusterCapability.Auth, "", default);
        using HttpClient client = node.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/providers")).StatusCode);
    }

    [Fact]
    public async Task An_anchor_this_node_cannot_currently_see_still_holds_the_accounts()
    {
        // The assignment names a holder and no roster row for it exists. The accounts are not this
        // node's to answer for merely because it cannot see who does — fail toward the anchor, not
        // toward a second door opening whenever the mesh is unsettled.
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await node.Services.GetRequiredService<ClusterStateStore>()
            .AssignAsync(ClusterCapability.Auth, "gone-auth", default);
        using HttpClient client = node.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/auth/providers");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("gone-auth", response.Headers.GetValues("X-Kgsm-Auth-Holder").Single());
        Assert.False(response.Headers.Contains("X-Kgsm-Auth-Url"));
    }

    [Theory]
    [InlineData("GET", "/auth/session")]
    [InlineData("GET", "/auth/sessions")]
    [InlineData("GET", "/auth/identities")]
    [InlineData("GET", "/auth/users")]
    [InlineData("POST", "/auth/logout")]
    [InlineData("POST", "/auth/session/revoke")]
    public async Task Reading_who_you_are_and_ending_a_session_stay_open(string method, string path)
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await AnchorIn(node, "hotrod-auth", "https://auth.example");
        using HttpClient client = node.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AuthTestFactory.MintTokenWithRow(node.Services, KgsmTier.Admin, access: true));

        HttpResponseMessage response = await client.SendAsync(Request(new HttpMethod(method), path));

        // Whatever else they answer, they are not refused by the gate: revoking takes authority away
        // rather than granting it, and a read of your own session is not a door.
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
