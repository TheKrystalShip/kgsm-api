using TheKrystalShip.KGSM.Auth;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Cluster;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit tests for <see cref="PeerHandshakeService.AddPeerAsync"/> — the join-via-seed handshake's four
/// outcomes (P0 §9 self-validation checklist item 1: reachable→<see cref="PeerAddOutcome.Added"/>,
/// unreachable→<see cref="PeerAddOutcome.Unreachable"/>, non-cluster→<see cref="PeerAddOutcome.NotCluster"/>,
/// apiVersion-mismatch→<see cref="PeerAddOutcome.VersionMismatch"/>). The 502/422/409 HTTP status-code
/// mapping on top of these outcomes is asserted at the controller level in
/// <see cref="PeersControllerTests"/>; this file proves the service-layer outcome the controller switches
/// on. Constructed directly against a fake <see cref="IHttpClientFactory"/> standing in for the candidate
/// peer (the <see cref="OutboxDrainerTests"/> pattern) and a real <see cref="PeersStore"/> (temp-file
/// SQLite) — no <c>WebApplicationFactory</c>, no real HTTP.
/// </summary>
public sealed class PeerHandshakeServiceTests
{
    private const string ClusterSecret = "handshake-test-secret";

    [Fact]
    public async Task AddPeerAsync_CandidateAnswersNonSuccess_ReturnsUnreachable()
    {
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, "");
        PeerHandshakeService svc = NewService(handler, out _);

        PeerAddResult result = await svc.AddPeerAsync("https://node-b.test", nickname: null, default);

        Assert.Equal(PeerAddOutcome.Unreachable, result.Outcome);
        Assert.Null(result.Peer);
    }

    [Fact]
    public async Task AddPeerAsync_CandidateConnectionFails_ReturnsUnreachable()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        PeerHandshakeService svc = NewService(handler, out _);

        PeerAddResult result = await svc.AddPeerAsync("https://node-b.test", nickname: null, default);

        Assert.Equal(PeerAddOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task AddPeerAsync_CandidateMissingClusterCapability_ReturnsNotCluster()
    {
        string identity = Identity(nodeId: "node-b", apiVersion: "v1", capabilities: ["monitor", "watchdog"]);
        var handler = new ScriptedHandler(HttpStatusCode.OK, identity);
        PeerHandshakeService svc = NewService(handler, out _);

        PeerAddResult result = await svc.AddPeerAsync("https://node-b.test", nickname: null, default);

        Assert.Equal(PeerAddOutcome.NotCluster, result.Outcome);
    }

    [Fact]
    public async Task AddPeerAsync_CandidateApiVersionMismatch_ReturnsVersionMismatch_WithRemoteVersion()
    {
        string identity = Identity(nodeId: "node-b", apiVersion: "v2", capabilities: ["cluster"]);
        var handler = new ScriptedHandler(HttpStatusCode.OK, identity);
        PeerHandshakeService svc = NewService(handler, out _);

        PeerAddResult result = await svc.AddPeerAsync("https://node-b.test", nickname: null, default);

        Assert.Equal(PeerAddOutcome.VersionMismatch, result.Outcome);
        Assert.Equal("v2", result.RemoteApiVersion);
    }

    [Fact]
    public async Task AddPeerAsync_ReachableClusterMemberMatchingVersion_AddsAndPersistsRow()
    {
        string identity = Identity(nodeId: "node-b", apiVersion: ApiInfo.ApiVersion, capabilities: ["cluster", "monitor"]);
        var handler = new ScriptedHandler(HttpStatusCode.OK, identity);
        PeerHandshakeService svc = NewService(handler, out PeersStore store);

        PeerAddResult result = await svc.AddPeerAsync("https://node-b.test", nickname: "Gaming Box", default);

        Assert.Equal(PeerAddOutcome.Added, result.Outcome);
        Assert.NotNull(result.Peer);
        Assert.Equal("node-b", result.Peer!.NodeId);
        Assert.Equal("https://node-b.test", result.Peer.Url);
        Assert.Equal("Gaming Box", result.Peer.Nickname);
        Assert.True(result.Peer.Enabled);

        // Really landed in the store (not just returned) — the same lookup the disable-list gate and the
        // inbox path use.
        PeerEntity? stored = await store.GetByNodeIdAsync("node-b", default);
        Assert.NotNull(stored);
        Assert.Equal(result.Peer.Id, stored!.Id);
    }

    [Fact]
    public async Task AddPeerAsync_NotAnAbsoluteHttpUrl_ReturnsInvalidUrl_WithoutCallingTheCandidate()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, Identity("node-b", "v1", ["cluster"]));
        PeerHandshakeService svc = NewService(handler, out _);

        PeerAddResult result = await svc.AddPeerAsync("not-a-url", nickname: null, default);

        Assert.Equal(PeerAddOutcome.InvalidUrl, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static string Identity(string nodeId, string apiVersion, string[] capabilities) =>
        JsonSerializer.Serialize(new { nodeId, apiVersion, build = "0.0.0+test", capabilities });

    private static PeerHandshakeService NewService(HttpMessageHandler handler, out PeersStore store)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"handshaketest-{Guid.NewGuid():N}.db");
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .BuildServiceProvider();
        using (IServiceScope scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        store = new PeersStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PeersStore>.Instance);

        var options = Options();
        var tokens = new ClusterTokenService(options, NullLogger<ClusterTokenService>.Instance);
        return new PeerHandshakeService(
            new FakeHttpClientFactory(handler), store, tokens, options, NullLogger<PeerHandshakeService>.Instance);
    }

    private static ApiOptions Options() => new()
    {
        HostId = "node-a", HostLabel = "node-a",
        MonitorSocketPath = "", WatchdogSocketPath = "", AssistantBaseUrl = "", AssistantRelaySecret = "",
        FirewallSocketPath = "", SchedulerSocketPath = "", BotSocketPath = "", KgsmPath = "/usr/bin/kgsm", KgsmJournalDir = "/var/lib/kgsm/events",
        BlueprintCacheTtlSeconds = 60,
        InstanceCacheTtlSeconds = 60,
        LogSources = [], JournalctlPath = "journalctl", SystemctlPath = "systemctl", LogReadTimeoutMs = 5000,
        RawgApiKey = "", RawgCacheDir = Path.GetTempPath(), PublicBaseUrl = "",
        SteamCdnBaseUrl = "https://steamcdn.test/apps",
        LibraryRefreshIntervalDays = 7, LibraryRefreshHour = 6,
        FilesMaxEntries = 200, FilesMaxEditBytes = 2 * 1024 * 1024, BlueprintMaxEditBytes = 256 * 1024,
        LeafOverridesDir = "/tmp/kgsm-api-test-overrides", LeafApplyCanaryMs = 15000,
        LeafDescriptorDir = "/tmp/kgsm-api-test-descriptors", LeafDropInDir = "/tmp/kgsm-api-test-dropins",
        DomainPollMs = 5000, MetricsPollMs = 1000, ServicesPollMs = 5000,
        AuthDisabled = true, SigningKey = "", OAuth = new KgsmAuthOptions(),
        DiscordRedirectUri = "", AuthFrontendUrl = "",
        SessionsCacheTtlMs = 5000, SessionsGcMs = 600000, SessionsRefreshAbsoluteDays = 30,
        // Cluster ON (non-blank secret) — this node's own identity, minted and presented to the candidate.
        ClusterSecret = ClusterSecret, ClusterSecretPrevious = "", NodeId = "node-a",
    };

    /// <summary>Always hands back a fresh <see cref="HttpClient"/> wrapping the SAME handler instance,
    /// never disposing it — same seam as <see cref="OutboxDrainerTests"/>'s fake factory.</summary>
    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Scripted candidate response: a fixed status + body for every request, recording each
    /// request's URI so a test can assert the candidate was never actually called (the invalid-URL case).</summary>
    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri?.ToString() ?? "");
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>Simulates a candidate that can't be reached at all (connection refused/DNS failure) —
    /// throws before any response is produced.</summary>
    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw exception;
    }
}
