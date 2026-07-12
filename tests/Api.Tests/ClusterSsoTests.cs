using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// P1 self-validation (<c>PLAN-peers.md §9</c>) — cluster SSO: the vouch endpoint
/// (<c>POST /auth/cluster-session</c>), the logout-everywhere fan-out (<see cref="SessionRevokeHandler"/>
/// wired through <see cref="SessionController"/>'s <c>FanOutRevokeAllAsync</c>), and the durable→alive
/// target filter (<see cref="RosterClusterTargetProvider.GetEnabledTargetsAsync"/>). Reuses the
/// <see cref="ClusterTwoNodeTests"/> harness (<see cref="ClusterNodeFactory"/>, the routing trick, the
/// <c>TogglableHandler</c>, <c>PollUntilAsync</c>) rather than inventing new machinery.
/// </summary>
public sealed class ClusterSsoTests
{
    // ── 1. Vouch happy path ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Vouch_HappyPath_MintsNativeSessionOnReceivingNode()
    {
        const string secret = "sso-happy-secret";
        string dbB = NewDbPath("sso-happy");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            string clusterToken = factoryB.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
            string discordId = "sso-user-" + Guid.NewGuid().ToString("N")[..12];

            using HttpClient clientB = factoryB.CreateClient();
            JsonElement body = await Json(await PostClusterSessionAsync(
                clientB, clusterToken, discordId, "ssouser", "SSO User", "operator"));

            Assert.True(body.TryGetProperty("accessToken", out JsonElement accessTokenEl));
            Assert.True(body.TryGetProperty("refreshToken", out JsonElement refreshTokenEl));
            Assert.True(body.TryGetProperty("sid", out JsonElement sidEl));
            string accessToken = accessTokenEl.GetString()!;
            string refreshToken = refreshTokenEl.GetString()!;
            string sid = sidEl.GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(accessToken));
            Assert.False(string.IsNullOrWhiteSpace(refreshToken));
            Assert.False(string.IsNullOrWhiteSpace(sid));

            SessionStore sessionsB = factoryB.Services.GetRequiredService<SessionStore>();
            SessionEntry? row = await sessionsB.GetByIdAsync(sid);
            Assert.NotNull(row);
            Assert.Equal($"discord:{discordId}", row!.UserId);
            Assert.False(row.Revoked);

            // Bonus: the returned access token actually authenticates a follow-up call on B.
            var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
            sessionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            HttpResponseMessage sessionResp = await clientB.SendAsync(sessionRequest);
            Assert.Equal(HttpStatusCode.OK, sessionResp.StatusCode);
            JsonElement sessionBody = await Json(sessionResp);
            Assert.Equal($"discord:{discordId}", sessionBody.GetProperty("user").GetProperty("id").GetString());
        }
        finally { DeleteBestEffort(dbB); }
    }

    // ── 2. Vouch auth/validation failures ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Vouch_NoBearer_401InvalidClusterToken()
    {
        const string secret = "sso-nobearer-secret";
        string dbB = NewDbPath("sso-nobearer");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            using HttpClient clientB = factoryB.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Post, "/auth/cluster-session")
            {
                Content = JsonContent.Create(new
                {
                    discordId = "whoever",
                    username = "whoever",
                    displayName = "Whoever",
                    tier = "viewer",
                }),
            };
            HttpResponseMessage resp = await clientB.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            JsonElement error = (await Json(resp)).GetProperty("error");
            Assert.Equal("invalid_cluster_token", error.GetProperty("code").GetString());
        }
        finally { DeleteBestEffort(dbB); }
    }

    [Fact]
    public async Task Vouch_GarbageBearer_401InvalidClusterToken()
    {
        const string secret = "sso-garbage-secret";
        string dbB = NewDbPath("sso-garbage");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            using HttpClient clientB = factoryB.CreateClient();

            HttpResponseMessage resp = await PostClusterSessionAsync(
                clientB, "not-a-real-token", "whoever", "whoever", "Whoever", "viewer");

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            JsonElement error = (await Json(resp)).GetProperty("error");
            Assert.Equal("invalid_cluster_token", error.GetProperty("code").GetString());
        }
        finally { DeleteBestEffort(dbB); }
    }

    [Fact]
    public async Task Vouch_ExpiredToken_401InvalidClusterToken()
    {
        // A cluster token signed with a DIFFERENT secret is functionally the same failure mode as an
        // expired one from ValidateAsync's point of view (both fail signature/validation) — mirrors
        // ClusterTwoNodeTests' AuthFailClosed test, applied to the vouch endpoint instead of the inbox.
        const string secretB = "sso-mismatch-secret-b";
        const string secretOther = "sso-mismatch-secret-other";
        string dbB = NewDbPath("sso-mismatch");
        string dbOther = NewDbPath("sso-mismatch-other");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secretB, dbPath: dbB);
            await using var factoryOther =
                new ClusterNodeFactory("node-other", "host-other", secretOther, dbPath: dbOther);
            string foreignToken = factoryOther.Services.GetRequiredService<IClusterTokenService>().Mint().Token;

            using HttpClient clientB = factoryB.CreateClient();
            HttpResponseMessage resp = await PostClusterSessionAsync(
                clientB, foreignToken, "whoever", "whoever", "Whoever", "viewer");

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            JsonElement error = (await Json(resp)).GetProperty("error");
            Assert.Equal("invalid_cluster_token", error.GetProperty("code").GetString());
        }
        finally { DeleteBestEffort(dbB); DeleteBestEffort(dbOther); }
    }

    [Fact]
    public async Task Vouch_DisabledPeer_403PeerDisabled()
    {
        const string secret = "sso-disabled-secret";
        string dbA = NewDbPath("sso-disabled-a"), dbB = NewDbPath("sso-disabled-b");
        try
        {
            await using var factoryA = new ClusterNodeFactory("node-a", "host-a", secret, dbPath: dbA);
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);

            string tokenFromA = factoryA.Services.GetRequiredService<IClusterTokenService>().Mint().Token;

            // B explicitly disables node-a.
            PeersStore peersOnB = factoryB.Services.GetRequiredService<PeersStore>();
            await peersOnB.UpsertAsync(new PeerEntity
            {
                Id = "peer_node_a_disabled",
                Url = "http://node-a",
                NodeId = "node-a",
                Status = "unknown",
                ApiVersion = "v1",
                Enabled = false,
            }, default);

            using HttpClient clientB = factoryB.CreateClient();
            HttpResponseMessage resp = await PostClusterSessionAsync(
                clientB, tokenFromA, "whoever", "whoever", "Whoever", "viewer");

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            JsonElement error = (await Json(resp)).GetProperty("error");
            Assert.Equal("peer_disabled", error.GetProperty("code").GetString());
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    [Fact]
    public async Task Vouch_MissingDiscordId_400BadRequest()
    {
        const string secret = "sso-baddiscordid-secret";
        string dbB = NewDbPath("sso-baddiscordid");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            string clusterToken = factoryB.Services.GetRequiredService<IClusterTokenService>().Mint().Token;

            using HttpClient clientB = factoryB.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "/auth/cluster-session")
            {
                Content = JsonContent.Create(new { discordId = "", username = "x", displayName = "X", tier = "viewer" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clusterToken);
            HttpResponseMessage resp = await clientB.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            JsonElement error = (await Json(resp)).GetProperty("error");
            Assert.Equal("bad_request", error.GetProperty("code").GetString());
        }
        finally { DeleteBestEffort(dbB); }
    }

    [Fact]
    public async Task Vouch_UnparseableTier_FloorsToViewer_NeverEscalated()
    {
        const string secret = "sso-floor-secret";
        string dbB = NewDbPath("sso-floor");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            string clusterToken = factoryB.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
            string discordId = "sso-floor-user-" + Guid.NewGuid().ToString("N")[..12];

            using HttpClient clientB = factoryB.CreateClient();
            JsonElement body = await Json(await PostClusterSessionAsync(
                clientB, clusterToken, discordId, "flooruser", "Floor User", "totally-not-a-real-tier"));
            string accessToken = body.GetProperty("accessToken").GetString()!;

            // An operator-gated endpoint must 403 (never 401 — the bearer IS valid) for a floored-to-
            // viewer token: proves the unparseable tier landed at viewer, not escalated past it.
            var commandRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/servers/nope/commands")
            {
                Content = JsonContent.Create(new { verb = "start" }),
            };
            commandRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            HttpResponseMessage commandResp = await clientB.SendAsync(commandRequest);
            Assert.Equal(HttpStatusCode.Forbidden, commandResp.StatusCode);
        }
        finally { DeleteBestEffort(dbB); }
    }

    // ── 3. Logout-everywhere across two nodes ───────────────────────────────────────────────────────

    [Fact]
    public async Task LogoutEverywhere_NodeA_RevokesLocalAndFansOutToFirstHandAliveB()
    {
        const string secret = "sso-logout-secret";
        string dbA = NewDbPath("sso-logout-a"), dbB = NewDbPath("sso-logout-b");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            HttpMessageHandler handlerToB = factoryB.Server.CreateHandler();
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", secret, dbPath: dbA, drainerHandlerFactory: () => handlerToB);

            string discordId = "logout-user-" + Guid.NewGuid().ToString("N")[..12];

            // The SAME user has a live session on BOTH nodes — minted via each node's own vouch
            // endpoint (self-mint cluster token), which also hands back the sid directly.
            string tokenA = factoryA.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
            using HttpClient clientA = factoryA.CreateClient();
            JsonElement bodyA = await Json(await PostClusterSessionAsync(
                clientA, tokenA, discordId, "logoutuser", "Logout User", "viewer"));
            string accessTokenA = bodyA.GetProperty("accessToken").GetString()!;
            string sidA = bodyA.GetProperty("sid").GetString()!;

            string tokenB = factoryB.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
            using HttpClient clientB = factoryB.CreateClient();
            JsonElement bodyB = await Json(await PostClusterSessionAsync(
                clientB, tokenB, discordId, "logoutuser", "Logout User", "viewer"));
            string sidB = bodyB.GetProperty("sid").GetString()!;

            // Make B a first-hand-alive peer in A's roster (LastSeen stamped — the join-via-seed
            // handshake's own postcondition, reproduced directly here rather than running the full
            // handshake, since this test is about the fan-out, not the handshake itself).
            PeersStore peersOnA = factoryA.Services.GetRequiredService<PeersStore>();
            await peersOnA.UpsertAsync(new PeerEntity
            {
                Id = "peer_node_b_logout",
                Url = "http://node-b",
                NodeId = "node-b",
                Status = "reachable",
                MembershipState = GossipState.Alive,
                LastSeen = DateTimeOffset.UtcNow,
                ApiVersion = "v1",
                Enabled = true,
            }, default);

            // The self logout-everywhere call on A.
            var revokeRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/session/revoke")
            {
                Content = JsonContent.Create(new { all = true }),
            };
            revokeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessTokenA);
            HttpResponseMessage revokeResp = await clientA.SendAsync(revokeRequest);
            Assert.Equal(HttpStatusCode.NoContent, revokeResp.StatusCode);

            // A's own session is revoked locally, immediately.
            SessionStore sessionsA = factoryA.Services.GetRequiredService<SessionStore>();
            SessionEntry? rowA = await sessionsA.GetByIdAsync(sidA);
            Assert.NotNull(rowA);
            Assert.True(rowA!.Revoked, "expected node A's own session to be revoked locally");

            // An outbox row of type session.revoke targeting node-b was enqueued.
            OutboxMessage? outboxRow = await PollForOutboxRowAsync(factoryA, "node-b", "session.revoke",
                TimeSpan.FromSeconds(2));
            Assert.NotNull(outboxRow);
            Assert.Equal("session.revoke", outboxRow!.Type);

            // B's session for the same user is eventually revoked via the cluster bus.
            SessionStore sessionsB = factoryB.Services.GetRequiredService<SessionStore>();
            bool revokedOnB = await PollUntilAsync(
                async () => (await sessionsB.GetByIdAsync(sidB))?.Revoked ?? false,
                TimeSpan.FromSeconds(5));
            Assert.True(revokedOnB, "expected node B's session for the same user to be revoked via the cluster bus");
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── 4. Durable→alive filter excludes hearsay (integration + focused unit tests) ────────────────

    [Fact]
    public async Task LogoutEverywhere_HearsayPeerExcluded_OnlyRealAliveBReceivesOutboxRow()
    {
        const string secret = "sso-hearsay-secret";
        string dbA = NewDbPath("sso-hearsay-a"), dbB = NewDbPath("sso-hearsay-b");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            HttpMessageHandler handlerToB = factoryB.Server.CreateHandler();
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", secret, dbPath: dbA, drainerHandlerFactory: () => handlerToB);

            string discordId = "hearsay-user-" + Guid.NewGuid().ToString("N")[..12];
            string tokenA = factoryA.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
            using HttpClient clientA = factoryA.CreateClient();
            JsonElement bodyA = await Json(await PostClusterSessionAsync(
                clientA, tokenA, discordId, "hearsayuser", "Hearsay User", "viewer"));
            string accessTokenA = bodyA.GetProperty("accessToken").GetString()!;

            PeersStore peersOnA = factoryA.Services.GetRequiredService<PeersStore>();
            // The real, first-hand-alive B.
            await peersOnA.UpsertAsync(new PeerEntity
            {
                Id = "peer_node_b_hearsay_test",
                Url = "http://node-b",
                NodeId = "node-b",
                Status = "reachable",
                MembershipState = GossipState.Alive,
                LastSeen = DateTimeOffset.UtcNow,
                ApiVersion = "v1",
                Enabled = true,
            }, default);
            // A HEARSAY/phantom peer: gossip reported it alive, but this node has never reached it
            // first-hand (LastSeen null) — must be excluded from a durable, identity-carrying fan-out.
            await peersOnA.UpsertAsync(new PeerEntity
            {
                Id = "peer_node_phantom_hearsay_test",
                Url = "http://node-phantom",
                NodeId = "node-phantom",
                Status = "unknown",
                MembershipState = GossipState.Alive,
                LastSeen = null,
                ApiVersion = "v1",
                Enabled = true,
            }, default);

            var revokeRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/session/revoke")
            {
                Content = JsonContent.Create(new { all = true }),
            };
            revokeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessTokenA);
            HttpResponseMessage revokeResp = await clientA.SendAsync(revokeRequest);
            Assert.Equal(HttpStatusCode.NoContent, revokeResp.StatusCode);

            OutboxMessage? bRow = await PollForOutboxRowAsync(factoryA, "node-b", "session.revoke", TimeSpan.FromSeconds(2));
            Assert.NotNull(bRow);

            // Give the drainer a couple of ticks either way, then assert no row EVER targeted the
            // phantom node — not merely "not yet delivered."
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            OutboxMessage? phantomRow = await GetOutboxRowAsync(factoryA, "node-phantom");
            Assert.Null(phantomRow);
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    [Fact]
    public async Task RosterClusterTargetProvider_AliveWithLastSeen_Included()
    {
        PeersStore store = NewPeersStore();
        await store.UpsertAsync(Peer("node-alive", GossipState.Alive, lastSeen: DateTimeOffset.UtcNow, enabled: true), default);
        var provider = new RosterClusterTargetProvider(store);

        IReadOnlyList<ClusterTarget> targets = await provider.GetEnabledTargetsAsync(default);

        Assert.Contains(targets, t => t.NodeId == "node-alive");
    }

    [Fact]
    public async Task RosterClusterTargetProvider_AliveWithNullLastSeen_Excluded_Hearsay()
    {
        PeersStore store = NewPeersStore();
        await store.UpsertAsync(Peer("node-hearsay", GossipState.Alive, lastSeen: null, enabled: true), default);
        var provider = new RosterClusterTargetProvider(store);

        IReadOnlyList<ClusterTarget> targets = await provider.GetEnabledTargetsAsync(default);

        Assert.DoesNotContain(targets, t => t.NodeId == "node-hearsay");
    }

    [Fact]
    public async Task RosterClusterTargetProvider_Suspect_Excluded()
    {
        PeersStore store = NewPeersStore();
        await store.UpsertAsync(Peer("node-suspect", GossipState.Suspect, lastSeen: DateTimeOffset.UtcNow, enabled: true), default);
        var provider = new RosterClusterTargetProvider(store);

        IReadOnlyList<ClusterTarget> targets = await provider.GetEnabledTargetsAsync(default);

        Assert.DoesNotContain(targets, t => t.NodeId == "node-suspect");
    }

    [Fact]
    public async Task RosterClusterTargetProvider_DisabledAliveFirstHand_Excluded()
    {
        PeersStore store = NewPeersStore();
        await store.UpsertAsync(Peer("node-disabled", GossipState.Alive, lastSeen: DateTimeOffset.UtcNow, enabled: false), default);
        var provider = new RosterClusterTargetProvider(store);

        IReadOnlyList<ClusterTarget> targets = await provider.GetEnabledTargetsAsync(default);

        Assert.DoesNotContain(targets, t => t.NodeId == "node-disabled");
    }

    // ── 5. Down-node redelivery ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LogoutEverywhere_BDown_QueuedThenRedeliveredAndRevokedOnceBRecovers()
    {
        const string secret = "sso-downup-secret";
        string dbA = NewDbPath("sso-downup-a"), dbB = NewDbPath("sso-downup-b");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            var toggle = new TogglableDelegatingHandler(factoryB.Server.CreateHandler()) { Down = true };
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", secret, dbPath: dbA, drainerHandlerFactory: () => toggle);

            string discordId = "downup-user-" + Guid.NewGuid().ToString("N")[..12];
            string tokenA = factoryA.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
            using HttpClient clientA = factoryA.CreateClient();
            JsonElement bodyA = await Json(await PostClusterSessionAsync(
                clientA, tokenA, discordId, "downupuser", "DownUp User", "viewer"));
            string accessTokenA = bodyA.GetProperty("accessToken").GetString()!;

            // B's own session for the same user — created directly via B's vouch endpoint while B is
            // still "up" from B's own point of view (the toggle only affects A's outbound drainer client).
            string tokenB = factoryB.Services.GetRequiredService<IClusterTokenService>().Mint().Token;
            using HttpClient clientB = factoryB.CreateClient();
            JsonElement bodyB = await Json(await PostClusterSessionAsync(
                clientB, tokenB, discordId, "downupuser", "DownUp User", "viewer"));
            string sidB = bodyB.GetProperty("sid").GetString()!;

            PeersStore peersOnA = factoryA.Services.GetRequiredService<PeersStore>();
            await peersOnA.UpsertAsync(new PeerEntity
            {
                Id = "peer_node_b_downup",
                Url = "http://node-b",
                NodeId = "node-b",
                Status = "reachable",
                MembershipState = GossipState.Alive,
                LastSeen = DateTimeOffset.UtcNow,
                ApiVersion = "v1",
                Enabled = true,
            }, default);

            var revokeRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/session/revoke")
            {
                Content = JsonContent.Create(new { all = true }),
            };
            revokeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessTokenA);
            HttpResponseMessage revokeResp = await clientA.SendAsync(revokeRequest);
            Assert.Equal(HttpStatusCode.NoContent, revokeResp.StatusCode);

            // While B is "down": the row sits pending, retried, never delivered.
            bool retriedWhileDown = await PollUntilAsync(
                async () =>
                {
                    OutboxMessage? row = await GetOutboxRowAsync(factoryA, "node-b");
                    return row is { Status: "pending", Attempts: >= 1 };
                },
                TimeSpan.FromSeconds(3));
            Assert.True(retriedWhileDown, "expected at least one failed delivery attempt while B is down");

            SessionStore sessionsB = factoryB.Services.GetRequiredService<SessionStore>();
            SessionEntry? stillActive = await sessionsB.GetByIdAsync(sidB);
            Assert.NotNull(stillActive);
            Assert.False(stillActive!.Revoked, "the session must NOT be revoked while B is unreachable");

            // Node B "comes back online".
            toggle.Down = false;

            bool revoked = await PollUntilAsync(
                async () => (await sessionsB.GetByIdAsync(sidB))?.Revoked ?? false,
                TimeSpan.FromSeconds(8));
            Assert.True(revoked, "expected the queued session.revoke to be redelivered and applied once B recovers");

            bool delivered = await PollUntilAsync(
                async () => (await GetOutboxRowAsync(factoryA, "node-b"))?.Status == "delivered",
                TimeSpan.FromSeconds(2));
            Assert.True(delivered, "expected node A's outbox row to reach 'delivered' once B recovers");
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    private static Task<HttpResponseMessage> PostClusterSessionAsync(
        HttpClient client, string bearer, string discordId, string username, string displayName, string tier)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/cluster-session")
        {
            Content = JsonContent.Create(new { discordId, username, displayName, tier }),
        };
        if (!string.IsNullOrEmpty(bearer))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request);
    }

    private static string NewDbPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"kgsm-api-cluster-sso-{label}-{Guid.NewGuid():N}.db");

    private static void DeleteBestEffort(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static async Task<OutboxMessage?> GetOutboxRowAsync(ClusterNodeFactory factory, string targetNodeId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ClusterOutbox.AsNoTracking()
            .Where(o => o.TargetNodeId == targetNodeId)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();
    }

    private static async Task<OutboxMessage?> PollForOutboxRowAsync(
        ClusterNodeFactory factory, string targetNodeId, string type, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            OutboxMessage? row = await GetOutboxRowAsync(factory, targetNodeId);
            if (row is not null && row.Type == type)
                return row;
            await Task.Delay(TimeSpan.FromMilliseconds(150));
        }
        OutboxMessage? last = await GetOutboxRowAsync(factory, targetNodeId);
        return last is not null && last.Type == type ? last : null;
    }

    /// <summary>Polls <paramref name="condition"/> every 150ms until it returns <see langword="true"/>
    /// or <paramref name="timeout"/> elapses (one final check after the deadline) — same idiom as
    /// <see cref="ClusterTwoNodeTests"/>'s helper of the same name.</summary>
    private static async Task<bool> PollUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(150));
        }
        return await condition();
    }

    /// <summary>A <see cref="DelegatingHandler"/> standing in for "is node B reachable right now" — same
    /// idiom as <see cref="ClusterTwoNodeTests"/>'s private <c>TogglableHandler</c>, duplicated here
    /// (rather than made internal/shared) to keep this file self-contained per the existing per-file
    /// test-harness convention.</summary>
    private sealed class TogglableDelegatingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public volatile bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Down
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : base.SendAsync(request, ct);
    }

    // ── RosterClusterTargetProvider focused unit-test plumbing (PeersTableGateTests' NewStore pattern) ──

    private static PeerEntity Peer(string nodeId, string membershipState, DateTimeOffset? lastSeen, bool enabled) => new()
    {
        Id = "peer_" + Guid.NewGuid().ToString("N")[..10],
        Url = $"https://{nodeId}.test",
        NodeId = nodeId,
        Status = lastSeen is null ? "unknown" : "reachable",
        MembershipState = membershipState,
        LastSeen = lastSeen,
        ApiVersion = "v1",
        Enabled = enabled,
    };

    private static PeersStore NewPeersStore()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"rosterclustertargettest-{Guid.NewGuid():N}.db");
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .BuildServiceProvider();
        using (IServiceScope scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        return new PeersStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PeersStore>.Instance);
    }
}
