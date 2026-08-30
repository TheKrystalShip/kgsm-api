using System.Net;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>GET /api/v1/cluster/auth</c> — where this cluster signs people in.
/// </summary>
/// <remarks>
/// Unauthenticated, because the caller has no session and that is why it is asking. What it learns
/// is that this cluster has an auth anchor and where to knock, which is what the sign-in page it is
/// about to be sent to would tell it anyway.
/// <para>
/// The three ways of not having an answer are kept apart on purpose, because a person acts on each
/// differently and they are indistinguishable at a browser: no anchor, a holder that has left the
/// cluster, and a holder that states no address a browser can reach.
/// </para>
/// </remarks>
public sealed class ClusterAuthDiscoveryTests
{
    private const string Secret = "discovery-test-secret";

    private static async Task<JsonElement> AnchorAsync(ClusterNodeFactory factory)
    {
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/cluster/auth");

        // No bearer was sent. A caller that has to authenticate to find out where to authenticate
        // cannot get in at all.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task Hold(ClusterNodeFactory factory, string memberId)
    {
        await factory.Services.GetRequiredService<ClusterStateStore>()
            .AssignAsync(ClusterCapability.Auth, memberId, default);
    }

    private static async Task Roster(
        ClusterNodeFactory factory, string memberId, string url, string? signInUrl = null)
    {
        await factory.Services.GetRequiredService<MembersStore>().UpsertAsync(
            MemberRow.New(memberId, MemberKind.Anchor) with
            {
                Id = "member_" + memberId,
                Url = url,
                Status = "reachable",
                Published = signInUrl is null
                    ? ""
                    : PublishedFacts.Encode(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ClusterAuthFacts.SignInUrl] = signInUrl,
                    }),
            },
            default);
    }

    [Fact]
    public async Task A_cluster_with_no_anchor_says_so()
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);

        JsonElement answer = await AnchorAsync(node);

        Assert.False(answer.GetProperty("held").GetBoolean());
        Assert.False(answer.GetProperty("orphaned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("url").ValueKind);
    }

    [Fact]
    public async Task The_holder_is_named_with_the_address_it_states_for_a_browser()
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await Hold(node, "hotrod-auth");
        await Roster(node, "hotrod-auth", "http://10.0.0.5:8098", signInUrl: "https://auth.example");

        JsonElement answer = await AnchorAsync(node);

        Assert.True(answer.GetProperty("held").GetBoolean());
        Assert.Equal("hotrod-auth", answer.GetProperty("memberId").GetString());
        Assert.False(answer.GetProperty("orphaned").GetBoolean());

        // The anchor's own statement, not the address members reach it at. Sending a browser to the
        // second sends it somewhere it cannot go.
        Assert.Equal("https://auth.example", answer.GetProperty("url").GetString());
    }

    [Fact]
    public async Task An_anchor_that_states_no_browser_address_falls_back_to_the_one_members_use()
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await Hold(node, "hotrod-auth");
        await Roster(node, "hotrod-auth", "https://auth.example");

        Assert.Equal("https://auth.example", (await AnchorAsync(node)).GetProperty("url").GetString());
    }

    [Fact]
    public async Task An_anchor_with_no_address_at_all_is_named_without_one()
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await Hold(node, "hotrod-auth");
        await Roster(node, "hotrod-auth", url: "");

        JsonElement answer = await AnchorAsync(node);

        // A different sentence from "the anchor is down": the holder is known and there is nowhere
        // to send anybody, which is a configuration somebody can fix.
        Assert.True(answer.GetProperty("held").GetBoolean());
        Assert.False(answer.GetProperty("orphaned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("url").ValueKind);
    }

    [Fact]
    public async Task A_holder_that_has_left_the_roster_is_reported_as_orphaned()
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await Hold(node, "gone-auth");

        JsonElement answer = await AnchorAsync(node);

        // The one cluster state where every member reads healthy and nothing works. Named rather
        // than repaired: reassigning is a decision, and promoting automatically is the election this
        // design rejects.
        Assert.True(answer.GetProperty("held").GetBoolean());
        Assert.True(answer.GetProperty("orphaned").GetBoolean());
        Assert.Equal("gone-auth", answer.GetProperty("memberId").GetString());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("url").ValueKind);
    }

    [Fact]
    public async Task A_capability_deliberately_held_by_nobody_is_not_an_orphan()
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);
        await Hold(node, "hotrod-auth");
        await Hold(node, "");

        JsonElement answer = await AnchorAsync(node);

        // "The cluster decided nobody" and "nobody has told me yet" read the same here, and both
        // mean: sign in against the member you are already pointed at.
        Assert.False(answer.GetProperty("held").GetBoolean());
        Assert.False(answer.GetProperty("orphaned").GetBoolean());
    }
}
