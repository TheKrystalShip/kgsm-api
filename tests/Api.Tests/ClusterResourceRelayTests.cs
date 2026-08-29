using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Identity;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// P2 self-validation (<c>PLAN-peers.md §9</c>) — cluster resource visibility. Two distinct reads:
/// <list type="bullet">
///   <item><c>GET /peers/self/{resources|capabilities|library}</c> — what a node exposes to a cluster
///   peer (cluster-token authed + disable-gated), a lean honest projection of its own §4·a/§4·b/§8·a
///   surfaces.</item>
///   <item><c>GET /peers/{id}/{resources|capabilities|library}</c> — the server-side node-proxy relay
///   (admin-gated), which mints a service token and fans the read out to the peer's <c>self/*</c>.</item>
/// </list>
/// Reuses the <see cref="ClusterNodeFactory"/> harness + routing trick from <see cref="ClusterTwoNodeTests"/>
/// so the two-node happy path crosses the real mint/validate/gate/project code on both sides.
/// </summary>
public sealed class ClusterResourceRelayTests
{
    private const string Secret = "p2-resource-visibility-secret";

    private static string NewDb(string tag) =>
        Path.Combine(Path.GetTempPath(), $"kgsm-api-p2-{tag}-{Guid.NewGuid():N}.db");

    private static void DeleteBestEffort(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort test cleanup */ }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    // ── 1. self/* auth (cluster-token authed) ─────────────────────────────────────────────────────

    [Fact]
    public async Task SelfResources_NoBearer_Returns401()
    {
        string db = NewDb("self-noauth");
        try
        {
            await using var node = new ClusterNodeFactory("node-a", "host-a", Secret, dbPath: db);
            using HttpClient client = node.CreateClient();

            HttpResponseMessage resp = await client.GetAsync("/api/v1/members/self/resources");

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            JsonElement body = await Json(resp);
            Assert.Equal("invalid_cluster_token", body.GetProperty("error").GetProperty("code").GetString());
        }
        finally { DeleteBestEffort(db); }
    }

    [Fact]
    public async Task SelfResources_ValidClusterToken_ReturnsHonestNullCapacity()
    {
        string db = NewDb("self-resources");
        try
        {
            await using var node = new ClusterNodeFactory("node-a", "host-a", Secret, dbPath: db);
            string token = node.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
            using HttpClient client = node.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/members/self/resources");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            JsonElement body = await Json(resp);
            // Identity is always present (reaching the response means this node is up → "online").
            Assert.Equal("host-a", body.GetProperty("id").GetString());
            Assert.Equal("online", body.GetProperty("status").GetString());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("label").GetString()));
            // No monitor is wired (AuthTestFactory points at a dead socket) → capacity is honest null,
            // never a fabricated figure. The fields are PRESENT (emitted as explicit null), not omitted.
            Assert.Equal(JsonValueKind.Null, body.GetProperty("cpuPct").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("mem").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("disks").ValueKind);
        }
        finally { DeleteBestEffort(db); }
    }

    [Fact]
    public async Task SelfCapabilities_ValidClusterToken_ReturnsCapabilityBlock()
    {
        string db = NewDb("self-caps");
        try
        {
            await using var node = new ClusterNodeFactory("node-a", "host-a", Secret, dbPath: db);
            string token = node.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
            using HttpClient client = node.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/members/self/capabilities");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            JsonElement body = await Json(resp);
            // The §4·b capability block — each capability carries the two-axis provisioned/status shape.
            Assert.True(body.TryGetProperty("metrics", out JsonElement metrics));
            Assert.True(metrics.TryGetProperty("provisioned", out _));
            Assert.True(metrics.TryGetProperty("status", out _));
        }
        finally { DeleteBestEffort(db); }
    }

    [Fact]
    public async Task SelfLibrary_ValidClusterToken_Returns200Array()
    {
        string db = NewDb("self-library");
        try
        {
            await using var node = new ClusterNodeFactory("node-a", "host-a", Secret, dbPath: db);
            string token = node.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
            using HttpClient client = node.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/members/self/library");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            JsonElement body = await Json(resp);
            // Engine unprovisioned → an honest empty catalog (the ServerAggregator degrade), not a 500.
            Assert.Equal(JsonValueKind.Array, body.ValueKind);
        }
        finally { DeleteBestEffort(db); }
    }

    // ── 2. {id}/* relay (server-side node-proxy, admin-gated) ─────────────────────────────────────

    [Fact]
    public async Task PeerResources_UnknownId_Returns404()
    {
        string db = NewDb("relay-unknown");
        try
        {
            await using var node = new ClusterNodeFactory("node-a", "host-a", Secret, dbPath: db);
            using HttpClient client = node.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/members/does-not-exist/resources");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", node.AccessToken(KgsmTier.Admin));

            HttpResponseMessage resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { DeleteBestEffort(db); }
    }

    [Fact]
    public async Task PeerResources_DisabledPeer_Returns403()
    {
        string db = NewDb("relay-disabled");
        try
        {
            await using var node = new ClusterNodeFactory("node-a", "host-a", Secret, dbPath: db);
            MembersStore peers = node.Services.GetRequiredService<MembersStore>();
            await peers.UpsertAsync(MemberRow.New("node-b", MemberKind.Node) with
            {
                Id = "peer-b", Url = "http://node-b", Status = "unknown",
                MembershipState = GossipState.Alive, ApiVersion = "v1", Enabled = false,
            }, default);

            using HttpClient client = node.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/members/peer-b/resources");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", node.AccessToken(KgsmTier.Admin));
            HttpResponseMessage resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            JsonElement body = await Json(resp);
            Assert.Equal("member_disabled", body.GetProperty("error").GetProperty("code").GetString());
        }
        finally { DeleteBestEffort(db); }
    }

    [Fact]
    public async Task PeerResources_UnreachablePeer_Returns502_NotA500()
    {
        string db = NewDb("relay-unreachable");
        try
        {
            // No drainerHandlerFactory → the relay's real HttpClient tries to dial the (bogus) peer URL and
            // fails. The honesty rule: a down peer degrades to 502 peer_unreachable, never a 500.
            await using var node = new ClusterNodeFactory("node-a", "host-a", Secret, dbPath: db);
            MembersStore peers = node.Services.GetRequiredService<MembersStore>();
            await peers.UpsertAsync(MemberRow.New("node-b", MemberKind.Node) with
            {
                Id = "peer-b", Url = "http://127.0.0.1:1/unreachable", Status = "unreachable",
                MembershipState = GossipState.Alive, ApiVersion = "v1", Enabled = true,
            }, default);

            using HttpClient client = node.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/members/peer-b/resources");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", node.AccessToken(KgsmTier.Admin));
            HttpResponseMessage resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
            JsonElement body = await Json(resp);
            Assert.Equal("member_unreachable", body.GetProperty("error").GetProperty("code").GetString());
        }
        finally { DeleteBestEffort(db); }
    }

    [Fact]
    public async Task PeerResources_ViewerToken_Returns403_RelayIsAdminGated()
    {
        string db = NewDb("relay-viewer");
        try
        {
            await using var node = new ClusterNodeFactory("node-a", "host-a", Secret, dbPath: db);
            using HttpClient client = node.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/members/peer-b/resources");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", node.AccessToken(KgsmTier.Viewer));

            HttpResponseMessage resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { DeleteBestEffort(db); }
    }

    // ── 3. Two-node happy path: A relays to B's self/resources ────────────────────────────────────

    [Fact]
    public async Task PeerResources_TwoNode_RelaysToPeerSelfResources()
    {
        string dbA = NewDb("relay-2node-a"), dbB = NewDb("relay-2node-b");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", Secret, dbPath: dbB);
            // Build B's in-memory handler first, then wire A's relay client (the reused OutboxDrainer
            // named client) to route into B's real pipeline — the ClusterTwoNodeTests routing trick.
            HttpMessageHandler handlerToB = factoryB.Server.CreateHandler();
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", Secret, dbPath: dbA, drainerHandlerFactory: () => handlerToB);

            // B is a first-hand-alive, enabled peer in A's roster.
            MembersStore peersOnA = factoryA.Services.GetRequiredService<MembersStore>();
            await peersOnA.UpsertAsync(MemberRow.New("node-b", MemberKind.Node) with
            {
                Id = "peer-b", Url = "http://node-b", Status = MemberStatus.Reachable,
                MembershipState = GossipState.Alive, LastSeen = DateTimeOffset.UtcNow,
                ApiVersion = "v1", Enabled = true,
            }, default);

            using HttpClient clientA = factoryA.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/members/peer-b/resources");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factoryA.AccessToken(KgsmTier.Admin));
            HttpResponseMessage resp = await clientA.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            JsonElement body = await Json(resp);
            // The body is B's OWN projection, relayed verbatim — B's host id, not A's.
            Assert.Equal("host-b", body.GetProperty("id").GetString());
            Assert.Equal("online", body.GetProperty("status").GetString());
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }
}
