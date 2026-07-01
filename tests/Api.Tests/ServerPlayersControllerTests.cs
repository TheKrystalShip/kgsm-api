using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Coverage for <c>GET /servers/{id}/players</c> (player-presence-contract.md §5), proven through the
/// real pipeline with the engine seam faked (the project's <c>FakeOpsInstanceService</c> convention —
/// see <c>Tier1OpsTests</c>). Load-bearing: **operator**-gated (the contract's explicit call, unlike the
/// viewer-gated read on other server sub-resources); the honest <c>unknown</c>-vs-<c>configured</c>-empty
/// distinction (§5's central rule — a game with no detection must NEVER read as "0 players online");
/// and that the live roster (<see cref="PlayerHistoryService"/>, pre-seeded here the same way the audit
/// consumer would drive it) actually surfaces through the endpoint when detection IS configured.
/// </summary>
public sealed class ServerPlayersControllerTests
    : IClassFixture<ServerPlayersControllerTests.PlayersTestFactory>, IClassFixture<AuthTestFactory>
{
    private const string Detected = "factorio-1";   // both regexes configured
    private const string JoinOnly = "valheim-1";    // only the join regex configured — still "configured"
    private const string NoDetection = "rust-1";    // neither regex configured — "unknown"

    private readonly PlayersTestFactory _engine;
    private readonly AuthTestFactory _noEngine;

    public ServerPlayersControllerTests(PlayersTestFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
    }

    private static HttpClient Client(AuthTestFactory f, AuthTier? tier)
    {
        HttpClient c = f.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(t));
        return c;
    }

    [Fact]
    public async Task NoToken_401()
    {
        HttpResponseMessage resp = await Client(_engine, null).GetAsync($"/api/v1/servers/{Detected}/players");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ViewerTier_403_OperatorGatedNotViewerGated()
    {
        // The contract's explicit call: operator, not the read-is-viewer default other sub-resources use.
        HttpResponseMessage resp = await Client(_engine, AuthTier.Viewer).GetAsync($"/api/v1/servers/{Detected}/players");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownServerId_404()
    {
        HttpResponseMessage resp = await Client(_engine, AuthTier.Operator).GetAsync("/api/v1/servers/nope/players");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Client(_noEngine, AuthTier.Operator).GetAsync($"/api/v1/servers/{Detected}/players");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task NoDetectionConfigured_Unknown_PlayersForcedEmpty()
    {
        // The central honesty rule (§5): NEITHER regex set → detection:"unknown" and players MUST be []
        // regardless of whatever the history projection happens to hold for this id.
        HttpResponseMessage resp = await Client(_engine, AuthTier.Operator).GetAsync($"/api/v1/servers/{NoDetection}/players");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("unknown", doc.RootElement.GetProperty("detection").GetString());
        Assert.Empty(doc.RootElement.GetProperty("players").EnumerateArray());
    }

    [Fact]
    public async Task Configured_NobodyConnected_HonestEmptyRoster_NotUnknown()
    {
        // Detection configured, nobody joined yet — a REAL empty, distinct from "unknown".
        HttpResponseMessage resp = await Client(_engine, AuthTier.Operator).GetAsync($"/api/v1/servers/{JoinOnly}/players");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("configured", doc.RootElement.GetProperty("detection").GetString());
        Assert.Empty(doc.RootElement.GetProperty("players").EnumerateArray());
    }

    [Fact]
    public async Task Configured_LiveRoster_Surfaces()
    {
        // Pre-seed the history exactly the way KgsmAuditConsumer's join handler would (via the same
        // singleton the controller reads) — proves the read path end-to-end, not just the gate.
        var history = _engine.Services.GetRequiredService<PlayerHistoryService>();
        var since = DateTimeOffset.UtcNow.AddMinutes(-2);
        history.Join(Detected, sessionKey: "76561198000000000", id: "76561198000000000",
            name: "Heisen", addr: null, since);

        HttpResponseMessage resp = await Client(_engine, AuthTier.Operator).GetAsync($"/api/v1/servers/{Detected}/players");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("configured", doc.RootElement.GetProperty("detection").GetString());
        JsonElement.ArrayEnumerator players = doc.RootElement.GetProperty("players").EnumerateArray();
        JsonElement p = Assert.Single(players);
        Assert.Equal("76561198000000000", p.GetProperty("playerIdentity").GetString());
        Assert.Equal("Heisen", p.GetProperty("playerName").GetString());
        Assert.Equal("76561198000000000", p.GetProperty("playerId").GetString());
        Assert.True(p.GetProperty("playerAddr").ValueKind is JsonValueKind.Null);
        Assert.Equal("online", p.GetProperty("status").GetString());

        history.Reset(Detected); // don't leak state into other tests sharing this factory's singleton
    }

    /// <summary><see cref="AuthTestFactory"/> with a fake <see cref="IInstanceService"/> whose roster
    /// carries the three detection shapes this endpoint's gate needs to distinguish.</summary>
    public sealed class PlayersTestFactory : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new FakePresenceInstanceService());
            });
        }
    }

    private sealed class FakePresenceInstanceService : IInstanceService
    {
        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();
        public Dictionary<string, Instance> GetAll() => new()
        {
            [Detected] = new Instance
            {
                Name = Detected, BlueprintFile = "factorio.bp.yaml",
                PlayerJoinedRegex = @"Client (?<name>.+?) \((?<id>\d+)\) is ready",
                PlayerLeftRegex = @"Client disconnected.*ClientId: (?<id>\d+)",
            },
            [JoinOnly] = new Instance
            {
                Name = JoinOnly, BlueprintFile = "valheim.bp.yaml",
                PlayerJoinedRegex = @"Got character ZDOID from (?<name>.+?) : (?<key>\d+):\d+",
                PlayerLeftRegex = "",
            },
            [NoDetection] = new Instance { Name = NoDetection, BlueprintFile = "rust.bp.yaml" },
        };

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new()
        {
            [Detected] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus { InstanceName = Detected, Status = true }),
            [JoinOnly] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus { InstanceName = JoinOnly, Status = true }),
            [NoDetection] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus { InstanceName = NoDetection, Status = true }),
        };

        public Instance? GetInstanceInfo(string instanceName) => GetAll().GetValueOrDefault(instanceName);

        // --- unused by this endpoint: honest NotImplemented (never silently fabricate) ---
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetInstalledVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetLatestVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult CheckUpdate(string instanceName) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
