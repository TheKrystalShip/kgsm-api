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

        // The holder's NAME, so a client can say which member answers rather than reading English.
        Assert.Equal("hotrod-auth", response.Headers.GetValues("X-Kgsm-Auth-Holder").Single());

        // And never its address, on the header or in the message. A member of a cluster does not tell
        // a caller where that cluster's accounts are: a browser reaches the anchor because somebody
        // gave it the anchor's address, not because a node it happened to find offered one.
        Assert.False(response.Headers.Contains("X-Kgsm-Auth-Url"));
        Assert.DoesNotContain("auth.example", await response.Content.ReadAsStringAsync());
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

    /// <summary>The rest of the surface — reads, and everything that ends a session.</summary>
    /// <remarks>
    /// A member of a cluster answers for no part of an account, and that includes ending a session.
    /// Ending one is not authenticating and this node can still do it usefully, but a node that
    /// answered here would be a machine in the cluster with an <c>/auth</c> surface — and the rule is
    /// about the surface existing at all, not about which half of it is dangerous. A sign-out reaches
    /// every member over the durable bus instead, which is where it already reached the ones the
    /// panel was not driving.
    /// </remarks>
    [Theory]
    [InlineData("GET", "/auth/session")]
    [InlineData("GET", "/auth/sessions")]
    [InlineData("GET", "/auth/identities")]
    [InlineData("GET", "/auth/users")]
    [InlineData("POST", "/auth/logout")]
    [InlineData("POST", "/auth/session/revoke")]
    public async Task A_clustered_node_answers_for_no_part_of_an_account(string method, string path)
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await AnchorIn(node, "hotrod-auth", "https://auth.example");
        using HttpClient client = node.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AuthTestFactory.MintTokenWithRow(node.Services, KgsmTier.Admin, access: true));

        HttpResponseMessage response = await client.SendAsync(Request(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// The same surface on a machine that holds its own accounts.
    /// </summary>
    /// <remarks>
    /// <b>The invariant the whole gate exists to protect.</b> Most installs are one machine: the
    /// accounts are its own, it is the only door there is, and every one of these has to keep working
    /// exactly as it always has. A change that closed them here would lock somebody out of their own
    /// server, and would do it on the deployment nobody is watching a cluster page for.
    /// </remarks>
    [Theory]
    [InlineData("GET", "/auth/session")]
    [InlineData("GET", "/auth/sessions")]
    [InlineData("GET", "/auth/identities")]
    [InlineData("GET", "/auth/users")]
    [InlineData("GET", "/auth/providers")]
    [InlineData("POST", "/auth/logout")]
    [InlineData("POST", "/auth/session/revoke")]
    [InlineData("POST", "/auth/login")]
    [InlineData("POST", "/auth/register")]
    [InlineData("POST", "/auth/password")]
    [InlineData("POST", "/auth/session/refresh")]
    [InlineData("POST", "/auth/users")]
    public async Task A_standalone_node_still_answers_for_all_of_it(string method, string path)
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        using HttpClient client = node.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AuthTestFactory.MintTokenWithRow(node.Services, KgsmTier.Admin, access: true));

        HttpResponseMessage response = await client.SendAsync(Request(new HttpMethod(method), path));

        // Whatever each answers on its own terms, none of them is refused by the gate.
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task A_node_in_an_anchored_cluster_mints_nothing_on_a_peer_s_word()
    {
        // Its own test because the vouch is the one door here that takes a body it validates before
        // any filter runs, so an empty one is refused for the wrong reason and proves nothing.
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await AnchorIn(node, "hotrod-auth", "https://auth.example");
        using HttpClient client = node.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/cluster-session")
        {
            Content = new StringContent(
                """{"discordId":"1","username":"somebody","displayName":"Somebody","tier":"viewer"}""",
                Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response = await client.SendAsync(request);

        // A node minting its own session because a peer asserted somebody is the thing an anchor
        // exists to replace. Where one holds the accounts there is nothing for a peer to assert that
        // its signature does not already say, and a second minting path is a second way to become
        // anybody.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task A_clustered_node_serves_no_route_that_names_its_cluster_s_anchor()
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await AnchorIn(node, "hotrod-auth", "https://auth.example");
        using HttpClient client = node.CreateClient();

        // There is no unauthenticated route here that hands back an anchor's address. Announcing what
        // a cluster contains is the anchor's own job, precisely because a client that has reached one
        // has already been given the right address — where a node offering the same answer would be a
        // way to find a cluster's authority from any machine in it.
        HttpResponseMessage response = await client.GetAsync("/api/v1/cluster/auth");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("auth.example", await response.Content.ReadAsStringAsync());
    }
}
