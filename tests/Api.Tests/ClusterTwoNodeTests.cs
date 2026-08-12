using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Phase 4 of the cluster message bus (<c>docs/cluster-message-bus-plan.md §11</c>) — the two-node
/// end-to-end self-validation. Phases 1–3 (envelope/inbox, unit-level drainer/backoff/TTL, GC) are each
/// individually proven (<see cref="ClusterInboxTests"/>, <see cref="OutboxDrainerTests"/>,
/// <see cref="ClusterTokenServiceTests"/>) but never together, wired as two real nodes talking over the
/// real HTTP client/server code paths. This file closes that gap by running TWO complete
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> instances
/// ("node-a"/"node-b") in the same process and routing node A's real <see cref="OutboxDrainer"/> HTTP
/// traffic into node B's real in-memory <c>TestServer</c> pipeline — no sockets, no ports, but every byte
/// still crosses the real <see cref="Controllers.PeersController.Inbox"/> action, the real
/// <see cref="IClusterTokenService"/> mint/validate, and the real <see cref="SessionRevokeHandler"/>.
/// </summary>
/// <remarks>
/// <b>The routing trick.</b> <c>TestServer.CreateHandler()</c> returns an <see cref="HttpMessageHandler"/>
/// that dispatches ANY request into that server's in-memory pipeline using only the request's path —
/// the host/port in the URI is never consulted. So node A's outbox rows can carry a placeholder
/// <c>TargetUrl</c> like <c>http://node-b</c> (see <see cref="ClusterTarget"/>) and still land on node
/// B's real controller, as long as node A's <c>"cluster-outbox-drainer"</c> named <see cref="HttpClient"/>
/// is wired to use node B's handler as its primary handler
/// (<see cref="ClusterNodeFactory"/>'s <c>drainerHandlerFactory</c> constructor parameter, applied via
/// <c>ConfigurePrimaryHttpMessageHandler</c> in <c>ConfigureTestServices</c>).
/// </remarks>
public sealed class ClusterTwoNodeTests
{
    // ── 1. Happy path: A enqueues, B is up throughout, the row is delivered and the session revoked ──

    [Fact]
    public async Task HappyPath_NodeA_EnqueuesSessionRevoke_NodeB_RevokesAndOutboxDelivers()
    {
        const string secret = "two-node-happy-path-secret";
        string dbA = NewDbPath("a-happy"), dbB = NewDbPath("b-happy");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            // Build B's TestServer FIRST so its in-memory handler exists before we wire A to it.
            HttpMessageHandler handlerToB = factoryB.Server.CreateHandler();
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", secret, dbPath: dbA, drainerHandlerFactory: () => handlerToB);

            SessionStore storeB = factoryB.Services.GetRequiredService<SessionStore>();
            string sid = "sid_2node_happy_" + Guid.NewGuid().ToString("N");
            await storeB.CreateAsync(
                sid, "discord:two-node-test-user", "host-b",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30),
                userAgent: null, initialJti: null);

            IClusterBus busA = factoryA.Services.GetRequiredService<IClusterBus>();
            await busA.EnqueueAsync(
                "session.revoke", new { scope = "sid", sid },
                [new ClusterTarget("node-b", "http://node-b")], CancellationToken.None);

            bool revoked = await PollUntilAsync(
                async () => (await storeB.GetByIdAsync(sid))?.Revoked ?? false,
                TimeSpan.FromSeconds(5));
            Assert.True(revoked, "expected node B's session to be revoked via the cluster bus within 5s");

            // The drainer marks its own row `delivered` only AFTER it observes B's 2xx response — that
            // happens strictly after (and racily close to) B applying the handler above, so poll here
            // too rather than a single-shot read right after the revoke poll settles.
            bool delivered = await PollUntilAsync(
                async () => (await GetOutboxRowAsync(factoryA, "node-b"))?.Status == "delivered",
                TimeSpan.FromSeconds(2));
            Assert.True(delivered, "expected node A's outbox row for this message to reach 'delivered'");

            OutboxMessage? row = await GetOutboxRowAsync(factoryA, "node-b");
            Assert.NotNull(row);
            Assert.Equal("delivered", row!.Status);
            Assert.NotNull(row.DeliveredAt);
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── 2. Down-then-up: the keystone test — B is unreachable, then comes back ─────────────────────

    [Fact]
    public async Task DownThenUp_QueuedWhileBDown_RedeliveredAndRevokedOnceBRecovers()
    {
        const string secret = "two-node-down-up-secret";
        string dbA = NewDbPath("a-downup"), dbB = NewDbPath("b-downup");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            var toggle = new TogglableHandler(factoryB.Server.CreateHandler()) { Down = true };
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", secret, dbPath: dbA, drainerHandlerFactory: () => toggle);

            SessionStore storeB = factoryB.Services.GetRequiredService<SessionStore>();
            string sid = "sid_2node_downup_" + Guid.NewGuid().ToString("N");
            await storeB.CreateAsync(
                sid, "discord:two-node-test-user", "host-b",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30),
                userAgent: null, initialJti: null);

            IClusterBus busA = factoryA.Services.GetRequiredService<IClusterBus>();
            await busA.EnqueueAsync(
                "session.revoke", new { scope = "sid", sid },
                [new ClusterTarget("node-b", "http://node-b")], CancellationToken.None);

            // While B is "down" (the toggle short-circuits every request to a 503), give the drainer a
            // couple of ticks + at least one backoff-scheduled retry to run, then assert it has NOT
            // delivered and IS actively retrying (Attempts >= 1, still pending).
            bool retriedWhileDown = await PollUntilAsync(
                async () =>
                {
                    OutboxMessage? row = await GetOutboxRowAsync(factoryA, "node-b");
                    return row is { Status: "pending", Attempts: >= 1 };
                },
                TimeSpan.FromSeconds(3));
            Assert.True(retriedWhileDown, "expected at least one failed delivery attempt while B is down");

            SessionEntry? stillActive = await storeB.GetByIdAsync(sid);
            Assert.NotNull(stillActive);
            Assert.False(stillActive!.Revoked, "the session must NOT be revoked while B is unreachable");

            OutboxMessage? pendingRow = await GetOutboxRowAsync(factoryA, "node-b");
            Assert.NotNull(pendingRow);
            Assert.Equal("pending", pendingRow!.Status);

            // Node B "comes back online".
            toggle.Down = false;

            bool revoked = await PollUntilAsync(
                async () => (await storeB.GetByIdAsync(sid))?.Revoked ?? false,
                TimeSpan.FromSeconds(8));
            Assert.True(revoked, "expected the queued revoke to be redelivered and applied once B recovers");

            // Same race as the happy-path test: A's row flips to `delivered` slightly AFTER B applies
            // the handler (the drainer marks it once the 2xx response comes back) — poll, don't
            // single-shot read.
            bool delivered = await PollUntilAsync(
                async () => (await GetOutboxRowAsync(factoryA, "node-b"))?.Status == "delivered",
                TimeSpan.FromSeconds(2));
            Assert.True(delivered, "expected node A's outbox row to reach 'delivered' once B recovers");

            OutboxMessage? deliveredRow = await GetOutboxRowAsync(factoryA, "node-b");
            Assert.NotNull(deliveredRow);
            Assert.Equal("delivered", deliveredRow!.Status);
            Assert.NotNull(deliveredRow.DeliveredAt);
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── 3. Auth fail-closed end-to-end: mismatched cluster secrets ──────────────────────────────────

    [Fact]
    public async Task AuthFailClosed_MismatchedClusterSecrets_RowDeadLetters_SessionUntouched()
    {
        string dbA = NewDbPath("a-authfail"), dbB = NewDbPath("b-authfail");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", "b-only-secret", dbPath: dbB);
            HttpMessageHandler handlerToB = factoryB.Server.CreateHandler();
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", "a-only-secret", dbPath: dbA, drainerHandlerFactory: () => handlerToB);

            SessionStore storeB = factoryB.Services.GetRequiredService<SessionStore>();
            string sid = "sid_2node_authfail_" + Guid.NewGuid().ToString("N");
            await storeB.CreateAsync(
                sid, "discord:two-node-test-user", "host-b",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30),
                userAgent: null, initialJti: null);

            IClusterBus busA = factoryA.Services.GetRequiredService<IClusterBus>();
            await busA.EnqueueAsync(
                "session.revoke", new { scope = "sid", sid },
                [new ClusterTarget("node-b", "http://node-b")], CancellationToken.None);

            bool dead = await PollUntilAsync(
                async () => (await GetOutboxRowAsync(factoryA, "node-b"))?.Status == "dead",
                TimeSpan.FromSeconds(5));
            Assert.True(dead, "a token signed with A's secret must be permanently rejected (401) by B and dead-lettered");

            OutboxMessage? row = await GetOutboxRowAsync(factoryA, "node-b");
            Assert.NotNull(row);
            Assert.Contains("401", row!.LastError ?? "", StringComparison.Ordinal);

            SessionEntry? untouched = await storeB.GetByIdAsync(sid);
            Assert.NotNull(untouched);
            Assert.False(untouched!.Revoked, "B must never apply a message from a token it rejected");
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── 4. Join-via-seed: the real handshake against a real second node (P0 §9 checklist item 1) ─────

    [Fact]
    public async Task JoinViaSeed_NodeA_HandshakesRealNodeB_Returns201_AndNodeBListedEnabled()
    {
        const string secret = "two-node-join-secret";
        string dbA = NewDbPath("a-join"), dbB = NewDbPath("b-join");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);
            // Route node A's handshake client at node B's real in-memory pipeline — the same routing
            // trick as the outbox drainer above, applied to PeerHandshakeService's named client instead.
            HttpMessageHandler handlerToB = factoryB.Server.CreateHandler();
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", secret, dbPath: dbA, handshakeHandlerFactory: () => handlerToB);

            using HttpClient clientA = factoryA.CreateClient();
            string adminToken = AuthTestFactory.MintTokenWithRow(factoryA.Services, KgsmTier.Admin, access: true);

            var addRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/peers")
            {
                Content = JsonContent.Create(new { url = "http://node-b", nickname = "Node B" }),
            };
            addRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            HttpResponseMessage addResp = await clientA.SendAsync(addRequest);

            Assert.Equal(HttpStatusCode.Created, addResp.StatusCode);
            JsonElement added = JsonDocument.Parse(await addResp.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("node-b", added.GetProperty("nodeId").GetString());
            Assert.True(added.GetProperty("enabled").GetBoolean());

            var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/peers");
            listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            HttpResponseMessage listResp = await clientA.SendAsync(listRequest);
            JsonElement peers = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("peers");
            Assert.Contains(peers.EnumerateArray(), p => p.GetProperty("nodeId").GetString() == "node-b");
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── 5. Disable-list gate, cross-node (P0 §9 checklist item 3) ────────────────────────────────────

    [Fact]
    public async Task DisableListGate_UnknownAccepted_DisabledRejected403_ReEnabledAccepted()
    {
        const string secret = "two-node-disable-gate-secret";
        string dbA = NewDbPath("a-gate"), dbB = NewDbPath("b-gate");
        try
        {
            await using var factoryA = new ClusterNodeFactory("node-a", "host-a", secret, dbPath: dbA);
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", secret, dbPath: dbB);

            // Node B mints its OWN real service token (iss = "node-b") — posted straight at node A's
            // inbox, no drainer/outbox involved; this file's earlier tests already exercise that path.
            IClusterTokenService tokensB = factoryB.Services.GetRequiredService<IClusterTokenService>();

            string Envelope() => JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid().ToString("N"),
                type = "session.revoke",
                from = "node-b",
                ts = DateTimeOffset.UtcNow,
                payload = new { scope = "sid", sid = "does-not-matter" },
            });

            // node-b is unknown to A's roster — absence is not rejection.
            HttpResponseMessage before = await PostInboxAsync(factoryA, tokensB.Mint().Token, Envelope());
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

            // A explicitly disables node-b.
            PeersStore peersOnA = factoryA.Services.GetRequiredService<PeersStore>();
            await peersOnA.UpsertAsync(new PeerEntity
            {
                Id = "peer_node_b_gate_test",
                Url = "http://node-b",
                NodeId = "node-b",
                Status = "unknown",
                ApiVersion = "v1",
                Enabled = false,
            }, default);

            HttpResponseMessage whileDisabled = await PostInboxAsync(factoryA, tokensB.Mint().Token, Envelope());
            Assert.Equal(HttpStatusCode.Forbidden, whileDisabled.StatusCode);
            JsonElement error = JsonDocument.Parse(await whileDisabled.Content.ReadAsStringAsync())
                .RootElement.GetProperty("error");
            Assert.Equal("peer_disabled", error.GetProperty("code").GetString());

            // Re-enabling restores acceptance — the row is a toggle, not a one-way ban.
            await peersOnA.SetEnabledAsync("peer_node_b_gate_test", true, default);
            HttpResponseMessage afterReEnable = await PostInboxAsync(factoryA, tokensB.Mint().Token, Envelope());
            Assert.Equal(HttpStatusCode.OK, afterReEnable.StatusCode);
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    private static async Task<HttpResponseMessage> PostInboxAsync(ClusterNodeFactory factory, string token, string body)
    {
        using HttpClient client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/peers/inbox")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static string NewDbPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"kgsm-api-cluster2node-{label}-{Guid.NewGuid():N}.db");

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

    /// <summary>Polls <paramref name="condition"/> every 150ms until it returns <see langword="true"/>
    /// or <paramref name="timeout"/> elapses (one final check after the deadline) — deterministic-enough
    /// waiting for a real background drain loop without a fixed sleep.</summary>
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

    /// <summary>A <see cref="DelegatingHandler"/> standing in for "is node B reachable right now" —
    /// while <see cref="Down"/> is <see langword="true"/> every request short-circuits to a <c>503</c>
    /// (simulating the target being unreachable) without ever touching the inner handler; otherwise it
    /// forwards into the wrapped handler (node B's real <c>TestServer</c> handler) unchanged. Flipping
    /// <see cref="Down"/> mid-test is how <see cref="DownThenUp_QueuedWhileBDown_RedeliveredAndRevokedOnceBRecovers"/>
    /// simulates node B going down and then coming back.</summary>
    private sealed class TogglableHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public volatile bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Down
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : base.SendAsync(request, ct);
    }
}

/// <summary>
/// A standalone cluster node for the two-node tests — an <see cref="AuthTestFactory"/> (auth ON, sessions
/// ON, a known signing key, the Discord seam faked, engine/monitor unprovisioned) re-configured with its
/// OWN node identity, host identity, cluster secret, drain interval, and DB file, so two instances can run
/// side by side in one process as genuinely independent nodes. Optionally rewires the outbox drainer's
/// named <see cref="HttpClient"/> (<see cref="OutboxDrainer.HttpClientName"/>) and/or the join-via-seed
/// handshake's named client (<see cref="PeerHandshakeService.HttpClientName"/>) onto a caller-supplied
/// handler — the seam <see cref="ClusterTwoNodeTests"/> uses to route this node's outbound cluster traffic
/// into another node's in-memory <c>TestServer</c> pipeline instead of a real socket.
/// </summary>
public sealed class ClusterNodeFactory(
    string nodeId,
    string hostId,
    string clusterSecret,
    int drainMs = 250,
    string? dbPath = null,
    Func<HttpMessageHandler>? drainerHandlerFactory = null,
    Func<HttpMessageHandler>? handshakeHandlerFactory = null,
    Func<HttpMessageHandler>? gossipHandlerFactory = null,
    Func<HttpMessageHandler>? latencyHandlerFactory = null,
    string? advertiseUrl = null,
    int gossipMs = 250,
    int suspectMs = 1500,
    int reapMs = 4000,
    int pollMs = 250) : AuthTestFactory
{
    public string NodeId { get; } = nodeId;
    public string ClusterHostId { get; } = hostId;
    public string DbPath { get; } = dbPath ?? Path.Combine(Path.GetTempPath(), $"kgsm-api-cluster-{nodeId}-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Api:HostId"] = ClusterHostId,
                ["Api:NodeId"] = NodeId,
                ["Api:DbPath"] = DbPath,
                ["Api:EventJournalDir"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-events-{Guid.NewGuid():N}"),
                // Scan a directory that has no journals in it. The default is the machine's real
                // /var/lib, so a test host would otherwise merge THIS machine's watchdog and monitor
                // history into every assertion about what a test just did.
                ["Api:JournalStateRoot"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-state-{Guid.NewGuid():N}"),
                ["Api:ClusterSecret"] = clusterSecret,
                // Clamped to a 250ms floor by ApiOptions.FromConfiguration regardless of what's passed.
                ["Api:ClusterDrainMs"] = drainMs.ToString(CultureInfo.InvariantCulture),
                // P0.5 gossip cadence — floored by ApiOptions.FromConfiguration (gossip 250ms,
                // suspect/reap 1000ms) regardless of what's passed here.
                ["Api:ClusterGossipMs"] = gossipMs.ToString(CultureInfo.InvariantCulture),
                ["Api:ClusterSuspectMs"] = suspectMs.ToString(CultureInfo.InvariantCulture),
                ["Api:ClusterReapMs"] = reapMs.ToString(CultureInfo.InvariantCulture),
                // PeerLatencyPoller's first-hand probe cadence — floored at 250ms, same as gossip.
                ["Api:ClusterPollMs"] = pollMs.ToString(CultureInfo.InvariantCulture),
            };
            if (advertiseUrl is not null)
                settings["Api:ClusterAdvertiseUrl"] = advertiseUrl;
            config.AddInMemoryCollection(settings);
        });

        if (drainerHandlerFactory is not null)
        {
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(OutboxDrainer.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(drainerHandlerFactory));
        }

        if (handshakeHandlerFactory is not null)
        {
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(PeerHandshakeService.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(handshakeHandlerFactory));
        }

        if (gossipHandlerFactory is not null)
        {
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(GossipWorker.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(gossipHandlerFactory));
        }

        if (latencyHandlerFactory is not null)
        {
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(PeerLatencyPoller.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(latencyHandlerFactory));
        }
    }
}
