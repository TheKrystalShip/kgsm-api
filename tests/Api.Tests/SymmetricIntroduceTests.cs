using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Cluster;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The symmetric join exchange (<c>PLAN-peers.md</c> P0.6): one predicate on both sides, addresses carried
/// by reflection rather than configuration, and an outcome that does not depend on which node the operator
/// happened to be looking at. Two real in-process nodes wherever the claim is about what the pair ends up
/// holding; the predicate itself is exercised directly, because "the same function answers for both sides"
/// is a statement about the function.
/// </summary>
public sealed class SymmetricIntroduceTests
{
    private const string Secret = "introduce-test-secret";

    private static string NewDbPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"kgsm-api-introduce-{label}-{Guid.NewGuid():N}.db");

    private static void DeleteBestEffort(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static HttpRequestMessage Add(string url, string token, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/peers")
        {
            Content = JsonContent.Create(new { url }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return request;
    }

    private static async Task<IReadOnlyList<PeerEntity>> RosterAsync(ClusterNodeFactory node)
    {
        PeersStore store = node.Services.GetRequiredService<PeersStore>();
        return await store.ListAsync(default);
    }

    // ── 1. One predicate, the same answer whichever side is asked ────────────────────────────────────

    [Theory]
    [InlineData("node-a", "v1", true, "https://ok.test", PeerAddOutcome.IsSelf)]
    [InlineData("node-b", "v99", true, "https://ok.test", PeerAddOutcome.VersionMismatch)]
    [InlineData("node-b", "v1", false, "https://ok.test", PeerAddOutcome.NotCluster)]
    [InlineData("node-b", "v1", true, "http://public.example.com", PeerAddOutcome.InsecureTransport)]
    public async Task TheSamePredicateAnswersForBothDirections(
        string nodeId, string apiVersion, bool cluster, string candidate, PeerAddOutcome expected)
    {
        string db = NewDbPath("predicate");
        try
        {
            await using var node = new ClusterNodeFactory("node-a", "host-a", Secret, dbPath: db);
            PeerHandshakeService handshake = node.Services.GetRequiredService<PeerHandshakeService>();

            var card = new NodeCard(
                nodeId,
                apiVersion == "v1" ? ApiInfo.ApiVersion : apiVersion,
                "0.0.0+test",
                cluster ? ["cluster"] : ["monitor"],
                [new NodeCandidate(candidate, Client: true)],
                0);

            // Asked as the initiator (validating what came back) and as the receiver (validating what
            // arrived): one function, so one answer.
            Assert.Equal(expected, handshake.Validate(card));

            (PeerAddOutcome received, IntroduceExchange? answer) = await handshake.ReceiveAsync(
                new IntroduceExchange(card, null, []), observedAddress: null, default);
            Assert.Equal(expected, received);
            Assert.Null(answer);
        }
        finally { DeleteBestEffort(db); }
    }

    [Fact]
    public void APlaintextPublicAddressIsRefusedAndAPrivateOneIsNot()
    {
        Assert.False(PeerHandshakeService.IsTransportAcceptable("http://node.example.com"));
        Assert.True(PeerHandshakeService.IsTransportAcceptable("https://node.example.com"));
        Assert.True(PeerHandshakeService.IsTransportAcceptable("http://192.168.1.129:8080"));
        Assert.True(PeerHandshakeService.IsTransportAcceptable("http://10.0.0.4:8080"));
        Assert.True(PeerHandshakeService.IsTransportAcceptable("http://127.0.0.1:8097"));
        Assert.True(PeerHandshakeService.IsTransportAcceptable("http://hotbox"));
        Assert.True(PeerHandshakeService.IsTransportAcceptable("http://hotbox.lan"));
        Assert.False(PeerHandshakeService.IsTransportAcceptable("ftp://node.example.com"));
    }

    // ── 2. The joining node learns its own address from being joined ─────────────────────────────────

    [Fact]
    public async Task AJoinedNodeAdoptsTheAddressItWasIntroducedAt()
    {
        string dbA = NewDbPath("a-adopt"), dbB = NewDbPath("b-adopt");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", Secret, dbPath: dbB);
            HttpMessageHandler toB = factoryB.Server.CreateHandler();
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", Secret, dbPath: dbA, handshakeHandlerFactory: () => toB);

            // Node B is configured with no address of its own — the state a freshly-installed node is in.
            SelfIdentityStore selfB = factoryB.Services.GetRequiredService<SelfIdentityStore>();
            Assert.Empty(await selfB.CandidatesAsync(default));

            using HttpClient clientA = factoryA.CreateClient();
            string token = AuthTestFactory.MintTokenWithRow(factoryA.Services, KgsmTier.Admin, access: true);
            HttpResponseMessage resp = await clientA.SendAsync(Add("http://node-b", token));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            // B now knows where it answers, because A told it — and B can hand that on to the next node it
            // is introduced to, which is what makes the chain work without anyone editing a config file.
            IReadOnlyList<NodeCandidate> mine = await selfB.CandidatesAsync(default);
            Assert.Contains(mine, c => c.Url == "http://node-b" && c.Client);

            // And B holds a roster row for A, from the same single exchange.
            IReadOnlyList<PeerEntity> rosterB = await RosterAsync(factoryB);
            PeerEntity a = Assert.Single(rosterB);
            Assert.Equal("node-a", a.NodeId);
            Assert.NotEmpty(PeerCandidates.Decode(a.Candidates));
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── 3. Order does not matter ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IntroducingEitherDirectionLeavesTheSameRoster()
    {
        static async Task<(string Local, string Remote)> RunAsync(bool addFromA)
        {
            string dbA = NewDbPath("a-order"), dbB = NewDbPath("b-order");
            try
            {
                await using var factoryB = new ClusterNodeFactory("node-b", "host-b", Secret, dbPath: dbB);
                HttpMessageHandler toB = factoryB.Server.CreateHandler();
                await using var factoryA = new ClusterNodeFactory(
                    "node-a", "host-a", Secret, dbPath: dbA, handshakeHandlerFactory: () => toB);
                HttpMessageHandler toA = factoryA.Server.CreateHandler();
                await using var factoryBWithA = new ClusterNodeFactory(
                    "node-b2", "host-b", Secret, dbPath: dbB, handshakeHandlerFactory: () => toA);

                ClusterNodeFactory initiator = addFromA ? factoryA : factoryB;
                string target = addFromA ? "http://node-b" : "http://node-a";

                using HttpClient client = initiator.CreateClient();
                string token = AuthTestFactory.MintTokenWithRow(initiator.Services, KgsmTier.Admin, access: true);
                if (!addFromA)
                {
                    // Same act, the other way round: node B is the one an admin is looking at.
                    using HttpClient clientB = factoryBWithA.CreateClient();
                    string tokenB = AuthTestFactory.MintTokenWithRow(
                        factoryBWithA.Services, KgsmTier.Admin, access: true);
                    await clientB.SendAsync(Add(target, tokenB));
                    IReadOnlyList<PeerEntity> local = await RosterAsync(factoryBWithA);
                    IReadOnlyList<PeerEntity> remote = await RosterAsync(factoryA);
                    return (Describe(local), Describe(remote));
                }

                await client.SendAsync(Add(target, token));
                return (Describe(await RosterAsync(factoryA)), Describe(await RosterAsync(factoryB)));
            }
            finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
        }

        static string Describe(IReadOnlyList<PeerEntity> roster) =>
            string.Join(";", roster.Select(p => $"{p.NodeId}:{p.Enabled}:{p.AddressVerified}").Order());

        (string localFromA, string remoteFromA) = await RunAsync(addFromA: true);
        (string localFromB, string remoteFromB) = await RunAsync(addFromA: false);

        // Whoever initiated, each node ends up holding one enabled, not-yet-address-verified row for the
        // other. The pair's state is the same; only which admin clicked differs.
        Assert.Equal("node-b:True:False", localFromA);
        Assert.Equal("node-a:True:False", remoteFromA);
        Assert.Equal("node-a:True:False", localFromB);
        Assert.Equal("node-b2:True:False", remoteFromB);
    }

    [Fact]
    public async Task SimultaneousMutualIntroductionLeavesOneRowPerNode()
    {
        string dbA = NewDbPath("a-race"), dbB = NewDbPath("b-race");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", Secret, dbPath: dbB);
            HttpMessageHandler toB = factoryB.Server.CreateHandler();
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", Secret, dbPath: dbA, handshakeHandlerFactory: () => toB);

            using HttpClient clientA = factoryA.CreateClient();
            string token = AuthTestFactory.MintTokenWithRow(factoryA.Services, KgsmTier.Admin, access: true);

            // The same introduction run twice over — an admin double-clicking, or both ends being added at
            // once. Keyed on node id, so it converges on one row rather than accumulating.
            await Task.WhenAll(
                clientA.SendAsync(Add("http://node-b", token)),
                clientA.SendAsync(Add("http://node-b", token)));

            Assert.Single(await RosterAsync(factoryA));
            Assert.Single(await RosterAsync(factoryB));
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── 4. An address is a claim until it is probed ──────────────────────────────────────────────────

    [Fact]
    public void ANodeOnlyCandidateIsNeverHandedToTheBrowser()
    {
        string encoded = PeerCandidates.Encode([new NodeCandidate("http://10.0.0.4:8080", Client: false)]);
        IReadOnlyList<NodeCandidate> decoded = PeerCandidates.Decode(encoded);

        // Node-to-node will happily use it; the roster reports no client URL rather than handing the SPA an
        // address a browser cannot reach.
        Assert.Equal("http://10.0.0.4:8080", PeerCandidates.Best(decoded));
        Assert.Equal("", PeerCandidates.ClientUrl(decoded));
    }

    [Fact]
    public async Task AFreshlyIntroducedPeerCarriesAnUnverifiedAddress()
    {
        string dbA = NewDbPath("a-unverified"), dbB = NewDbPath("b-unverified");
        try
        {
            await using var factoryB = new ClusterNodeFactory("node-b", "host-b", Secret, dbPath: dbB);
            HttpMessageHandler toB = factoryB.Server.CreateHandler();
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", Secret, dbPath: dbA, handshakeHandlerFactory: () => toB);

            using HttpClient clientA = factoryA.CreateClient();
            string token = AuthTestFactory.MintTokenWithRow(factoryA.Services, KgsmTier.Admin, access: true);
            await clientA.SendAsync(Add("http://node-b", token));

            // The exchange proves the far side answers as a cluster member; it does not prove that the
            // address will still answer as that node on the next call, which is the poller's job.
            PeerEntity b = Assert.Single(await RosterAsync(factoryA));
            Assert.False(b.AddressVerified);
            Assert.Equal("http://node-b", b.Url);
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }

    // ── 5. The panel origin travels with the join ────────────────────────────────────────────────────

    [Fact]
    public async Task APanelOriginLearnedAtJoinAnswersCorsOnTheOtherNode()
    {
        const string panel = "https://panel.example";
        string dbA = NewDbPath("a-cors"), dbB = NewDbPath("b-cors");
        try
        {
            // Both nodes carry an allowlist that does NOT name the panel, so the permissive
            // nothing-configured branch is off and only a learned origin can widen it.
            await using var factoryB = new ClusterNodeFactory(
                "node-b", "host-b", Secret, dbPath: dbB, corsOrigins: "https://unrelated.example");
            HttpMessageHandler toB = factoryB.Server.CreateHandler();
            await using var factoryA = new ClusterNodeFactory(
                "node-a", "host-a", Secret, dbPath: dbA, handshakeHandlerFactory: () => toB,
                corsOrigins: "https://unrelated.example");

            using HttpClient clientA = factoryA.CreateClient();
            string token = AuthTestFactory.MintTokenWithRow(factoryA.Services, KgsmTier.Admin, access: true);

            // The admin is using the panel, so the request carries its origin. A records it and hands it to
            // B in the same exchange.
            HttpResponseMessage resp = await clientA.SendAsync(Add("http://node-b", token, origin: panel));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            SelfIdentityStore selfB = factoryB.Services.GetRequiredService<SelfIdentityStore>();
            Assert.Contains(panel, await selfB.PanelOriginsAsync(default));

            // And B answers CORS from it — a browser that has never been configured against B can call it.
            using HttpClient clientB = factoryB.CreateClient();
            var probe = new HttpRequestMessage(HttpMethod.Get, "/health");
            probe.Headers.Add("Origin", panel);
            HttpResponseMessage allowed = await clientB.SendAsync(probe);
            Assert.Equal(panel, Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));

            var stranger = new HttpRequestMessage(HttpMethod.Get, "/health");
            stranger.Headers.Add("Origin", "https://somewhere.else");
            HttpResponseMessage refused = await clientB.SendAsync(stranger);
            Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally { DeleteBestEffort(dbA); DeleteBestEffort(dbB); }
    }
}
