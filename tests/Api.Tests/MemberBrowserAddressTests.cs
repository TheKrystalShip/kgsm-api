using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The address the panel is handed for a member.
/// </summary>
/// <remarks>
/// A member behind a public name has two, and they answer different questions: the one this node
/// proved it can reach across a switch, and the one a person's browser can reach from anywhere. The
/// roster carries both and only one of them belongs in a browser.
/// </remarks>
public sealed class MemberBrowserAddressTests
{
    private const string Secret = "browser-address-test-secret";

    private static async Task Learn(
        ClusterNodeFactory factory, string memberId, string provenUrl, params MemberCandidate[] advertised)
    {
        await factory.Services.GetRequiredService<MembersStore>().UpsertAsync(
            MemberRow.New(memberId, MemberKind.Node) with
            {
                Id = "member_" + memberId,
                Url = provenUrl,
                Candidates = MemberCandidates.Encode(advertised),
                Status = "reachable",
            },
            default);
    }

    private static async Task<string?> UrlFor(ClusterNodeFactory factory, string memberId)
    {
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AuthTestFactory.MintTokenWithRow(factory.Services, KgsmTier.Admin, access: true));

        JsonElement body = JsonDocument.Parse(
            await (await client.GetAsync("/api/v1/members")).Content.ReadAsStringAsync()).RootElement;

        return body.GetProperty("members").EnumerateArray()
            .First(m => m.GetProperty("memberId").GetString() == memberId)
            .GetProperty("url").GetString();
    }

    [Fact]
    public async Task A_member_behind_a_public_name_is_given_by_that_name()
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);

        // What this node proved is the LAN address, because that is the path between them. What the
        // member says a browser should use is the public name, and it says it first.
        await Learn(node, "hotbox", provenUrl: "http://192.168.1.129:8080",
            new MemberCandidate("https://hotbox.example", true),
            new MemberCandidate("http://192.168.1.129:8080", true));

        Assert.Equal("https://hotbox.example", await UrlFor(node, "hotbox"));
    }

    [Fact]
    public async Task An_address_only_members_use_is_never_given_to_a_browser()
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);

        await Learn(node, "hotbox", provenUrl: "http://10.0.0.9:8080",
            new MemberCandidate("http://10.0.0.9:8080", false),
            new MemberCandidate("https://hotbox.example", true));

        Assert.Equal("https://hotbox.example", await UrlFor(node, "hotbox"));
    }

    [Fact]
    public async Task A_member_that_advertises_no_browser_address_is_given_by_what_was_proven()
    {
        await using var node = new ClusterNodeFactory("node-a", "host-a", Secret);

        // The only address there is. A blank would break a panel driving it, and on a cluster that is
        // entirely on one network this is the honest answer rather than a compromise.
        await Learn(node, "plain", provenUrl: "http://192.168.1.50:8080");

        Assert.Equal("http://192.168.1.50:8080", await UrlFor(node, "plain"));
    }
}
