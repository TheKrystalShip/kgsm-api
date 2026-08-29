using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Json;
using TheKrystalShip.Api.Services.Cluster;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Multi-node, in-process convergence tests for the P0.5 membership gossip layer (<c>PLAN-peers.md §2·b</c>)
/// — three real <see cref="ClusterNodeFactory"/> nodes wired together through <see cref="MultiNodeRouter"/>
/// (the same no-socket <c>TestServer.CreateHandler()</c> routing trick <see cref="ClusterTwoNodeTests"/> uses
/// for the two-node message bus, extended to route by hostname across N nodes instead of one fixed target).
/// <see cref="RosterMergerTests"/> already pins the merge decision table in isolation; this file proves the
/// real <see cref="GossipWorker"/>/<see cref="PeerLatencyPoller"/> loops converge it end-to-end: transitive
/// discovery, a permanently-unreachable phantom never getting first-hand promoted, the full failure-timer
/// chain (suspect → dead → reaped), self-refutation of a false <c>dead</c> report, and that gossip never
/// leaves a row in the durable outbox (G4 — it is a wholly separate, ephemeral transport).
/// </summary>
/// <remarks>
/// <b>The latency-poll cadence.</b> <see cref="PeerLatencyPoller"/>'s interval is <see cref="ApiOptions.ClusterPollMs"/>
/// (env <c>Api__ClusterPollMs</c>, floored at 250ms) — every <see cref="ClusterNodeFactory"/> node below
/// pins it to <c>250</c> (the <c>pollMs</c> ctor default), the same order of magnitude as the gossip cadence, so
/// a first-hand probe result (promotion to <c>reachable</c>, or the initial unreachable detection that seeds
/// the failure timer) lands within a couple hundred ms rather than a fixed 10s wait. Timeouts below are still
/// generous multiples of the relevant window (poll/gossip/suspect/reap), never a bare 1× margin, so the tests
/// stay non-flaky under CI jitter without needing the old ~15s-per-probe-stage ceiling.
/// </remarks>
public sealed class GossipConvergenceTests
{
    // ── 1. Transitive discovery: A learns about C purely via B, never told about C directly ───────────

    [Fact]
    public async Task Convergence_ThreeNodes_ALearnsC_ThroughB_ViaGossipOnly()
    {
        var router = new MultiNodeRouter();
        string dbA = NewDbPath("conv-a"), dbB = NewDbPath("conv-b"), dbC = NewDbPath("conv-c");
        try
        {
            const string secret = "gossip-convergence-secret";
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", secret, dbPath: dbA,
                gossipHandlerFactory: () => router, latencyHandlerFactory: () => router,
                advertiseUrl: "http://node-a", pollMs: 250);
            await using var factoryB = new ClusterNodeFactory(
                "node-b", "host-b", secret, dbPath: dbB,
                gossipHandlerFactory: () => router, latencyHandlerFactory: () => router,
                advertiseUrl: "http://node-b", pollMs: 250);
            await using var factoryC = new ClusterNodeFactory(
                "node-c", "host-c", secret, dbPath: dbC,
                gossipHandlerFactory: () => router, latencyHandlerFactory: () => router,
                advertiseUrl: "http://node-c", pollMs: 250);

            // Force each TestServer to build so its in-memory pipeline is registered in the router BEFORE
            // any gossip tick can rely on it.
            router.Register("node-a", factoryA.Server.CreateHandler());
            router.Register("node-b", factoryB.Server.CreateHandler());
            router.Register("node-c", factoryC.Server.CreateHandler());

            // A chain, deliberately no direct A↔C link: A only knows B, B only knows C.
            PeersStore storeA = factoryA.Services.GetRequiredService<PeersStore>();
            PeersStore storeB = factoryB.Services.GetRequiredService<PeersStore>();
            await storeA.UpsertAsync(SeedPeer("node-b", "http://node-b"), default);
            await storeB.UpsertAsync(SeedPeer("node-c", "http://node-c"), default);

            bool learnedC = await PollUntilAsync(
                async () => await storeA.GetByNodeIdAsync("node-c", default) is not null,
                TimeSpan.FromSeconds(5));

            Assert.True(learnedC, "node A must learn about node C purely via gossip through node B — no direct add");
            PeerEntity? learned = await storeA.GetByNodeIdAsync("node-c", default);
            Assert.NotNull(learned);
            Assert.Equal("node-c", learned!.NodeId);
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); DeleteBestEffort(dbC); }
    }

    // ── 2. A permanently-unreachable phantom is never promoted first-hand alive ─────────────────────

    [Fact]
    public async Task PhantomPeer_NeverReachable_NeverFirstHandPromoted_EscalatesToSuspectOrDead()
    {
        var router = new MultiNodeRouter();
        string dbA = NewDbPath("phantom-a");
        try
        {
            const string secret = "gossip-phantom-secret";
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", secret, dbPath: dbA,
                gossipHandlerFactory: () => router, latencyHandlerFactory: () => router,
                advertiseUrl: "http://node-a", suspectMs: 1500, reapMs: 4000, pollMs: 250);
            router.Register("node-a", factoryA.Server.CreateHandler());
            // node-ghost is deliberately NEVER registered — every probe/sync to it fails-open to a 503.

            PeersStore storeA = factoryA.Services.GetRequiredService<PeersStore>();
            await storeA.UpsertAsync(SeedPeer("node-ghost", "http://node-ghost"), default);

            // The 250ms-cadence latency poller must mark it unreachable within a few ticks (its first-hand
            // probe fails every time — node-ghost is never registered in the router).
            bool wentUnreachable = await PollUntilAsync(
                async () => (await storeA.GetByNodeIdAsync("node-ghost", default))?.Status == "unreachable",
                TimeSpan.FromSeconds(3));
            Assert.True(wentUnreachable, "an unregistered/unreachable peer must be probed and marked unreachable");

            // Once first-hand-unreachable, the failure timer (the ~250ms gossip cadence here) escalates
            // alive→suspect on the next round, then suspect→dead after ClusterSuspectMs (1.5s).
            bool escalated = await PollUntilAsync(
                async () =>
                {
                    string? state = (await storeA.GetByNodeIdAsync("node-ghost", default))?.MembershipState;
                    return state is GossipState.Suspect or GossipState.Dead;
                },
                TimeSpan.FromSeconds(3));
            Assert.True(escalated, "an unreachable phantom peer must escalate to suspect/dead, never stay a plain alive");

            PeerEntity? final = await storeA.GetByNodeIdAsync("node-ghost", default);
            Assert.NotNull(final);
            Assert.NotEqual("reachable", final!.Status);
            Assert.Null(final.LastSeen); // never first-hand authenticated (G3) — no probe ever succeeded
        }
        finally { DeleteBestEffort(dbA); }
    }

    // ── 3. Killed → suspect → dead → reaped: the full failure-timer chain ───────────────────────────

    [Fact]
    public async Task KilledPeer_GoesSuspectThenDeadThenReaped()
    {
        var router = new MultiNodeRouter();
        string dbA = NewDbPath("kill-a"), dbB = NewDbPath("kill-b");
        try
        {
            const string secret = "gossip-kill-secret";
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", secret, dbPath: dbA,
                gossipHandlerFactory: () => router, latencyHandlerFactory: () => router,
                advertiseUrl: "http://node-a", suspectMs: 1500, reapMs: 4000, pollMs: 250);
            await using var factoryB = new ClusterNodeFactory(
                "node-b", "host-b", secret, dbPath: dbB,
                gossipHandlerFactory: () => router, latencyHandlerFactory: () => router,
                advertiseUrl: "http://node-b", suspectMs: 1500, reapMs: 4000, pollMs: 250);

            router.Register("node-a", factoryA.Server.CreateHandler());
            router.Register("node-b", factoryB.Server.CreateHandler());

            PeersStore storeA = factoryA.Services.GetRequiredService<PeersStore>();
            await storeA.UpsertAsync(SeedPeer("node-b", "http://node-b"), default);

            // Let the latency poller first-hand-verify B is up before "killing" it — a few 250ms ticks.
            bool reachable = await PollUntilAsync(
                async () => (await storeA.GetByNodeIdAsync("node-b", default))?.Status == "reachable",
                TimeSpan.FromSeconds(3));
            Assert.True(reachable, "node A must first-hand reach node B before the kill");

            // GENUINELY kill B — dispose the node so its own gossip worker STOPS. Blocking only inbound
            // traffic (router.SetDown alone) would leave B's process alive and still gossiping outbound to A;
            // A would keep receiving authenticated syncs from B (mutual liveness evidence, PLAN-peers.md §2·b
            // G5) and correctly hold it alive forever. A real crash silences the node in both directions, and
            // only then does A's last-evidence clock run down to suspect→dead. SetDown makes A's probe to B
            // fail fast (503) rather than hang on the disposed handler.
            router.SetDown("node-b", true);
            await factoryB.DisposeAsync();

            // With no probe success AND no inbound sync from the now-silent B, A's last-evidence clock crosses
            // ClusterSuspectMs (1.5s) → suspect. A few× that window for CI jitter.
            bool suspect = await PollUntilAsync(
                async () => (await storeA.GetByNodeIdAsync("node-b", default))?.MembershipState == GossipState.Suspect,
                TimeSpan.FromSeconds(5));
            Assert.True(suspect, "a silenced peer must lose its liveness evidence and go suspect");

            // suspect→dead after another ClusterSuspectMs (1.5s) in suspect — a few× that window.
            bool dead = await PollUntilAsync(
                async () => (await storeA.GetByNodeIdAsync("node-b", default))?.MembershipState == GossipState.Dead,
                TimeSpan.FromSeconds(5));
            Assert.True(dead, "a suspect peer must escalate to dead once the suspect timeout elapses");

            // dead→reaped after ClusterReapMs (4s) — a couple× that window.
            bool reaped = await PollUntilAsync(
                async () => await storeA.GetByNodeIdAsync("node-b", default) is null,
                TimeSpan.FromSeconds(9));
            Assert.True(reaped, "a dead peer must be reaped (row removed) once the reap timeout elapses");
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── 4. Refutation: a false dead@2 report about self is beaten by a higher self-incarnation ──────

    [Fact]
    public async Task Refute_FalseDeadReportAboutSelf_SelfEntryBecomesAliveAtHigherIncarnation()
    {
        string dbB = NewDbPath("refute-b");
        try
        {
            const string secret = "gossip-refute-secret";
            await using var factoryB = new ClusterNodeFactory(
                "node-b", "host-b", secret, dbPath: dbB, advertiseUrl: "http://node-b");

            // Deterministic direct POST rather than relying on a random gossip pick landing on B: mint B's
            // OWN cluster token (Sync doesn't validate `From` against the bearer's `iss` — it merges a whole
            // roster, not a single actor's claim) and hand it a roster reporting B itself dead@2.
            IClusterTokenService tokensB = factoryB.Services.GetRequiredService<IClusterTokenService>();
            string token = tokensB.Mint().Token;

            var jsonOptions = new JsonSerializerOptions();
            ApiJson.Configure(jsonOptions);

            var syncRequest = new SyncRequest(
                "node-a", [new SyncMember("node-b", [new NodeCandidate("https://node-b", Client: true)], 2, GossipState.Dead, "v1")]);

            using HttpClient client = factoryB.CreateClient();
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/peers/sync")
            {
                Content = JsonContent.Create(syncRequest, options: jsonOptions),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response = await client.SendAsync(httpRequest);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            SyncResponse? sync = await response.Content.ReadFromJsonAsync<SyncResponse>(jsonOptions);
            Assert.NotNull(sync);

            SyncMember? selfEntry = sync!.Members.FirstOrDefault(m => m.NodeId == "node-b");
            Assert.NotNull(selfEntry);
            Assert.Equal(GossipState.Alive, selfEntry!.State);
            Assert.True(
                selfEntry.Incarnation >= 3,
                $"expected B to refute dead@2 by raising its incarnation to >= 3, got {selfEntry.Incarnation}");
        }
        finally { DeleteBestEffort(dbB); }
    }

    // ── 5. Gossip is ephemeral (G4): it never writes a durable outbox row ────────────────────────────

    [Fact]
    public async Task Gossip_NeverWritesOutboxRows()
    {
        var router = new MultiNodeRouter();
        string dbA = NewDbPath("no-outbox-a"), dbB = NewDbPath("no-outbox-b");
        try
        {
            const string secret = "gossip-no-outbox-secret";
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", secret, dbPath: dbA,
                gossipHandlerFactory: () => router, latencyHandlerFactory: () => router,
                advertiseUrl: "http://node-a", pollMs: 250);
            await using var factoryB = new ClusterNodeFactory(
                "node-b", "host-b", secret, dbPath: dbB,
                gossipHandlerFactory: () => router, latencyHandlerFactory: () => router,
                advertiseUrl: "http://node-b", pollMs: 250);

            router.Register("node-a", factoryA.Server.CreateHandler());
            router.Register("node-b", factoryB.Server.CreateHandler());

            PeersStore storeA = factoryA.Services.GetRequiredService<PeersStore>();
            await storeA.UpsertAsync(SeedPeer("node-b", "http://node-b"), default);

            // Let several gossip rounds (the ~250ms cadence) run — no session.revoke or any other bus
            // message is ever enqueued in this test.
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.Equal(0, await OutboxCountAsync(factoryA));
            Assert.Equal(0, await OutboxCountAsync(factoryB));
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static PeerEntity SeedPeer(string nodeId, string url) => new()
    {
        Id = "peer_" + nodeId,
        Url = url,
        NodeId = nodeId,
        Enabled = true,
        MembershipState = GossipState.Alive,
        Status = "unknown",
        ApiVersion = "v1",
    };

    private static async Task<int> OutboxCountAsync(ClusterNodeFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ClusterOutbox.AsNoTracking().CountAsync();
    }

    private static string NewDbPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"kgsm-api-gossip-{label}-{Guid.NewGuid():N}.db");

    private static void DeleteBestEffort(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>Polls <paramref name="condition"/> every 150ms until it returns <see langword="true"/> or
    /// <paramref name="timeout"/> elapses (one final check after the deadline) — same idiom as
    /// <see cref="ClusterTwoNodeTests"/>'s helper of the same name, duplicated locally since it's private
    /// there.</summary>
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

    /// <summary>
    /// An in-process, multi-node stand-in for the network: routes an outbound request to whichever node's
    /// real <c>TestServer</c> handler is <see cref="Register"/>ed under that request's URI host — the
    /// <see cref="ClusterTwoNodeTests"/> single-target routing trick generalized to N hosts sharing ONE
    /// <see cref="HttpMessageHandler"/> instance, so every node's gossip/latency-poll traffic to
    /// <c>http://node-a</c>/<c>http://node-b</c>/<c>http://node-c</c> lands on the right in-memory pipeline.
    /// An unregistered host, or one explicitly <see cref="SetDown"/>, fails open to a <c>503</c> — the same
    /// observable shape as a real connection failure, never a thrown exception (mirrors every leaf's
    /// fail-open posture, <c>PLAN-peers.md §2</c> #25).
    /// </summary>
    private sealed class MultiNodeRouter : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, HttpMessageInvoker> _handlers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _down = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Wire one node's host name to its real in-memory handler (typically
        /// <c>factory.Server.CreateHandler()</c>). <c>disposeHandler: false</c> — the owning
        /// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> disposes its own
        /// <c>TestServer</c>, this router must never dispose it out from under a sibling node's traffic.</summary>
        public void Register(string host, HttpMessageHandler handler) =>
            _handlers[host] = new HttpMessageInvoker(handler, disposeHandler: false);

        /// <summary>Flip a host's reachability — the "kill"/"revive" switch <see cref="KilledPeer_GoesSuspectThenDeadThenReaped"/>
        /// uses instead of actually tearing a node down.</summary>
        public void SetDown(string host, bool down) => _down[host] = down;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri?.Host ?? "";
            if (_down.TryGetValue(host, out bool isDown) && isDown)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (_handlers.TryGetValue(host, out HttpMessageInvoker? invoker))
                return await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // Not yet registered (e.g. a phantom node that never exists) — fail open, same as unreachable.
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        // This single instance is shared across every node's gossip/latency named HttpClient registrations
        // in a test — never let one node's handler-lifetime rotation dispose it out from under the others.
        protected override void Dispose(bool disposing) { }
    }
}
