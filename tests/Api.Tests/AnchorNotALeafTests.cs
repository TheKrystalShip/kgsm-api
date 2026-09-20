using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// A component this host describes as an <b>anchor</b> is not one of this node's services.
/// </summary>
/// <remarks>
/// Leaf-or-anchor is a deployment choice, so the same component is a node's leaf on one machine and the
/// cluster's anchor on another, and the descriptor it installed here is the only thing that says which.
/// An anchor owns its own configuration and journal and is reached as the cluster member it is — so the
/// node must neither list it on the Services board nor accept it as an addressable leaf, whichever
/// machine it happens to share.
/// </remarks>
public sealed class AnchorNotALeafTests
{
    private const string Host = AuthTestFactory.HostId;

    // A descriptor carries far more than this; the id is the whole of what the node reads out of the
    // anchors directory, so the fixture ships exactly that.
    private static string AnchorDescriptor(string id, string unit) =>
        $$"""{"schemaVersion":1,"id":"{{id}}","unit":"{{unit}}","displayName":"{{id}}","role":"a capability","fields":[]}""";

    [Fact]
    public async Task AnchoredComponent_IsAbsentFromTheServicesBoard()
    {
        using var factory = new LeafTestFactory();
        factory.InstallAnchorDescriptor("assistant", AnchorDescriptor("assistant", "kgsm-assistant-service.service"));
        HttpClient admin = Client(factory, KgsmTier.Admin);

        IReadOnlyList<string> ids = await ServiceIds(admin);

        Assert.DoesNotContain("assistant", ids);
        // Every other leaf is untouched — this subtracts one component, it does not narrow the board.
        Assert.Contains("monitor", ids);
        Assert.Contains("watchdog", ids);
    }

    [Fact]
    public async Task AnchoredComponent_IsNotAddressableAsALeaf()
    {
        using var factory = new LeafTestFactory();
        factory.InstallAnchorDescriptor("assistant", AnchorDescriptor("assistant", "kgsm-assistant-service.service"));
        HttpClient admin = Client(factory, KgsmTier.Admin);

        HttpResponseMessage resp = await admin.GetAsync($"/api/v1/hosts/{Host}/services/assistant/config");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task WithNoAnchorsDescribed_TheBoardIsTheWholeCatalog()
    {
        using var factory = new LeafTestFactory();
        HttpClient admin = Client(factory, KgsmTier.Admin);

        IReadOnlyList<string> ids = await ServiceIds(admin);

        Assert.Contains("assistant", ids);
        Assert.Contains("bot", ids);
    }

    // A component described as an anchor here AND shipping a leaf descriptor is a half-finished deploy:
    // the node believes the anchors directory, because that is the one a peer-of-this-node declares
    // itself in, and claiming it both ways is the reading that cannot be right.
    [Fact]
    public async Task AnchorDescriptor_WinsOverALeafDescriptorForTheSameComponent()
    {
        using var factory = new LeafTestFactory();
        factory.InstallDescriptor("assistant", AnchorDescriptor("assistant", "kgsm-assistant-service.service"));
        factory.InstallAnchorDescriptor("assistant", AnchorDescriptor("assistant", "kgsm-assistant-service.service"));
        HttpClient admin = Client(factory, KgsmTier.Admin);

        Assert.DoesNotContain("assistant", await ServiceIds(admin));
    }

    private static async Task<IReadOnlyList<string>> ServiceIds(HttpClient c)
    {
        HttpResponseMessage resp = await c.GetAsync($"/api/v1/hosts/{Host}/services");
        JsonElement d = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return [.. d.GetProperty("data").EnumerateArray().Select(s => s.GetProperty("id").GetString() ?? "")];
    }

    private static HttpClient Client(LeafTestFactory factory, KgsmTier tier)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.AccessToken(tier));
        return c;
    }
}
