using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Identity;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>MembersController</c> HTTP-contract tests (P0 §9 self-validation checklist items 1 and 4) — the
/// admin "paste a URL" join-via-seed action's status-code mapping (<c>502</c>/<c>422</c>/<c>409</c>, on
/// top of the <see cref="MemberAddOutcome"/> mapping unit-tested in <see cref="MemberHandshakeServiceTests"/>)
/// plus the <c>201</c> happy path, and the <c>GET /peers/identity</c> cluster-token auth gate. Boots the
/// real app (<see cref="AuthTestFactory"/>) with the cluster message bus turned on and
/// <see cref="MemberHandshakeService.HttpClientName"/>'s primary handler swapped for a scripted one (the
/// <see cref="OutboxDrainerTests"/>/<see cref="ClusterTwoNodeTests"/> seam) so the candidate node's
/// <c>/identity</c> response is fully controlled without a real peer. The genuine cross-node join (two
/// real <c>WebApplicationFactory</c> instances talking over the real handshake HTTP path) lives in
/// <see cref="ClusterTwoNodeTests"/>.
/// </summary>
public sealed class MembersControllerTests : IClassFixture<AuthTestFactory>
{
    private const string ClusterSecret = "peers-controller-test-secret";
    private const string NodeId = "peers-controller-node-a";

    private readonly AuthTestFactory _base;

    public MembersControllerTests(AuthTestFactory factory) => _base = factory;

    /// <summary>A fresh derived factory (own random DB, own host instance — the
    /// <see cref="ClusterInboxTests"/> "layer config onto the shared base factory" pattern) with the
    /// cluster bus turned on, optionally with the handshake's named client rewired to
    /// <paramref name="handshakeHandler"/> so <c>POST /peers</c> talks to a scripted candidate instead of
    /// the real network.</summary>
    private WebApplicationFactory<Program> BuildApp(HttpMessageHandler? handshakeHandler = null) =>
        _base.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cluster:Secret"] = ClusterSecret,
                ["Api:NodeId"] = NodeId,
            }));
            if (handshakeHandler is not null)
            {
                b.ConfigureTestServices(services =>
                    services.AddHttpClient(MemberHandshakeService.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => handshakeHandler));
            }
        });

    private static string AdminToken(WebApplicationFactory<Program> app) =>
        AuthTestFactory.MintTokenWithRow(app.Services, KgsmTier.Admin, access: true);

    private static HttpRequestMessage Bearer(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    /// <summary>
    /// A candidate member's answer to the introduce exchange — the same record this node sent it. The node
    /// block is what only a node has; <paramref name="clustered"/> is the member's own statement that it
    /// takes part in a cluster at all, which is the check a member that is not a node still answers.
    /// </summary>
    private static string Identity(
        string memberId, string apiVersion, string[] capabilities, bool clustered = true,
        string kind = MemberKind.Node) =>
        JsonSerializer.Serialize(new
        {
            self = new
            {
                memberId,
                kind,
                clustered,
                candidates = new[] { new { url = $"https://{memberId}.test", client = true } },
                incarnation = 0,
                protocol = ClusterProtocol.Current,
                node = new { apiVersion, build = "0.0.0+test", capabilities },
            },
            youAre = (object?)null,
            panelOrigins = Array.Empty<string>(),
        });

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    // ── 1. POST /peers status-code mapping (checklist item 1) ──────────────────────────────────────

    [Fact]
    public async Task Add_CandidateUnreachable_Returns502()
    {
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, "");
        using WebApplicationFactory<Program> app = BuildApp(handler);
        using HttpClient client = app.CreateClient();
        string token = AdminToken(app);

        HttpResponseMessage resp = await client.SendAsync(
            Bearer(HttpMethod.Post, "/api/v1/members", token, new { url = "https://node-b.test" }));

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal("member_unreachable", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Add_CandidateNotCluster_Returns422()
    {
        // A member that answers honestly that it takes no part in a cluster.
        string identity = Identity("node-b", "v1", ["monitor"], clustered: false);
        var handler = new ScriptedHandler(HttpStatusCode.OK, identity);
        using WebApplicationFactory<Program> app = BuildApp(handler);
        using HttpClient client = app.CreateClient();
        string token = AdminToken(app);

        HttpResponseMessage resp = await client.SendAsync(
            Bearer(HttpMethod.Post, "/api/v1/members", token, new { url = "https://node-b.test" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("member_not_cluster", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Add_ApiVersionMismatch_Returns409_WithRemoteAndLocalDetails()
    {
        string identity = Identity("node-b", "v2", ["cluster"]);
        var handler = new ScriptedHandler(HttpStatusCode.OK, identity);
        using WebApplicationFactory<Program> app = BuildApp(handler);
        using HttpClient client = app.CreateClient();
        string token = AdminToken(app);

        HttpResponseMessage resp = await client.SendAsync(
            Bearer(HttpMethod.Post, "/api/v1/members", token, new { url = "https://node-b.test" }));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        JsonElement error = (await Json(resp)).GetProperty("error");
        Assert.Equal("version_mismatch", error.GetProperty("code").GetString());
        JsonElement details = error.GetProperty("details");
        Assert.Equal("v2", details.GetProperty("remote").GetString());
        Assert.Equal("v1", details.GetProperty("local").GetString());
    }

    [Fact]
    public async Task Add_ReachableClusterMemberMatchingVersion_Returns201_AndListedInRoster()
    {
        string identity = Identity("node-b", "v1", ["cluster"]);
        var handler = new ScriptedHandler(HttpStatusCode.OK, identity);
        using WebApplicationFactory<Program> app = BuildApp(handler);
        using HttpClient client = app.CreateClient();
        string token = AdminToken(app);

        HttpResponseMessage addResp = await client.SendAsync(Bearer(
            HttpMethod.Post, "/api/v1/members", token, new { url = "https://node-b.test", nickname = "Gaming Box" }));

        Assert.Equal(HttpStatusCode.Created, addResp.StatusCode);
        JsonElement added = await Json(addResp);
        Assert.Equal("node-b", added.GetProperty("memberId").GetString());
        Assert.True(added.GetProperty("enabled").GetBoolean());

        HttpResponseMessage listResp = await client.SendAsync(Bearer(HttpMethod.Get, "/api/v1/members", token));
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        JsonElement peers = (await Json(listResp)).GetProperty("members");
        Assert.Contains(peers.EnumerateArray(), p => p.GetProperty("memberId").GetString() == "node-b");
    }

    [Fact]
    public async Task Add_ViewerToken_Returns403()
    {
        using WebApplicationFactory<Program> app = BuildApp();
        using HttpClient client = app.CreateClient();
        string viewerToken = AuthTestFactory.MintTokenWithRow(app.Services, KgsmTier.Viewer, access: true);

        HttpResponseMessage resp = await client.SendAsync(
            Bearer(HttpMethod.Post, "/api/v1/members", viewerToken, new { url = "https://node-b.test" }));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── 2. GET /peers/identity cluster-token auth (checklist item 4) ───────────────────────────────

    [Fact]
    public async Task GetIdentity_NoToken_Returns401()
    {
        using WebApplicationFactory<Program> app = BuildApp();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage resp = await client.GetAsync("/api/v1/members/identity");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("invalid_cluster_token", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetIdentity_ValidClusterToken_Returns200_WithClusterCapability()
    {
        using WebApplicationFactory<Program> app = BuildApp();
        IClusterTokenService tokens = app.Services.GetRequiredService<IClusterTokenService>();
        MintedClusterToken minted = tokens.Mint();
        using HttpClient client = app.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/members/identity");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minted.Token);

        HttpResponseMessage resp = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonElement body = await Json(resp);
        Assert.Equal(NodeId, body.GetProperty("memberId").GetString());
        Assert.Equal(MemberKind.Node, body.GetProperty("kind").GetString());
        Assert.True(body.GetProperty("clustered").GetBoolean());
        // The route version and the leaves are the node block: an anchor answering this endpoint carries
        // no such block at all.
        JsonElement node = body.GetProperty("node");
        Assert.Equal("v1", node.GetProperty("apiVersion").GetString());
        Assert.Contains(node.GetProperty("capabilities").EnumerateArray(), c => c.GetString() == "cluster");
    }

    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
