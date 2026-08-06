using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Coverage for <c>POST /servers/{id}/players/{identity}/kick|ban|unban</c>.
/// </summary>
/// <remarks>
/// The load-bearing property is the trust boundary: the request names a roster entry, never an
/// address, and the token that reaches the engine is built from the server-side record that key
/// resolves to. The tests below assert what the engine was actually handed, because that — not the
/// status code — is what a client could otherwise influence.
/// </remarks>
public sealed class ServerModerationTests
    : IClassFixture<ServerModerationTests.ModerationTestFactory>
{
    private const string ByIp = "romestead";      // kick/ban/unban {ip}
    private const string ByName = "minecraft-1";  // kick/ban/unban {name}
    private const string NoModeration = "rust-1"; // declares none

    private readonly ModerationTestFactory _f;

    public ServerModerationTests(ModerationTestFactory f) => _f = f;

    private HttpClient Client(KgsmTier? tier)
    {
        HttpClient c = _f.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.AccessToken(t));
        return c;
    }

    private PlayerHistoryService History => _f.Services.GetRequiredService<PlayerHistoryService>();
    private static FakeModerationInstanceService Engine => ModerationTestFactory.Engine;

    /// <summary>Seed one player the way the audit consumer's join handler would.</summary>
    private string SeedPlayer(string server, string? id, string? name, string? addr)
    {
        History.Join(server, sessionKey: addr ?? name ?? id, id: id, name: name, addr: addr,
            DateTimeOffset.UtcNow.AddMinutes(-1));

        return History.GetRoster(server)
            .Single(p => p.PlayerId == id && p.PlayerName == name && p.PlayerAddr == addr)
            .PlayerIdentity;
    }

    [Fact]
    public async Task NoToken_401()
    {
        HttpResponseMessage resp = await Client(null)
            .PostAsync($"/api/v1/servers/{ByIp}/players/whoever/kick", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ViewerTier_403_OperatorGated()
    {
        HttpResponseMessage resp = await Client(KgsmTier.Viewer)
            .PostAsync($"/api/v1/servers/{ByIp}/players/whoever/kick", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownServer_404()
    {
        HttpResponseMessage resp = await Client(KgsmTier.Operator)
            .PostAsync("/api/v1/servers/nope/players/whoever/kick", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PlayerNeverSeenOnThisServer_404_AndNothingIsSent()
    {
        // The roster is the only source of a target. A key it does not hold cannot be moderated —
        // this is what stops a caller naming someone (or something) the server never saw.
        Engine.Reset();

        HttpResponseMessage resp = await Client(KgsmTier.Operator)
            .PostAsync($"/api/v1/servers/{ByIp}/players/198.51.100.9/kick", null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Null(Engine.LastCall);
    }

    [Fact]
    public async Task GameDeclaresNoCommand_409_AndNothingIsSent()
    {
        Engine.Reset();
        string identity = SeedPlayer(NoModeration, id: null, name: "Someone", addr: null);

        HttpResponseMessage resp = await Client(KgsmTier.Operator)
            .PostAsync($"/api/v1/servers/{NoModeration}/players/{identity}/kick", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Null(Engine.LastCall);
        History.Reset(NoModeration);
    }

    [Fact]
    public async Task PlayerLacksTheIdentityTheGameWants_409_AndNothingIsSent()
    {
        // A Steam-relay game exposes no address, so an {ip}-keyed command has nothing to address.
        // Refused honestly rather than substituting the name, which would ban the wrong thing.
        Engine.Reset();
        string identity = SeedPlayer(ByIp, id: null, name: "NoAddressHere", addr: null);

        HttpResponseMessage resp = await Client(KgsmTier.Operator)
            .PostAsync($"/api/v1/servers/{ByIp}/players/{identity}/ban", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Null(Engine.LastCall);
        History.Reset(ByIp);
    }

    [Theory]
    [InlineData("kick")]
    [InlineData("ban")]
    [InlineData("unban")]
    public async Task IpKeyedGame_SendsTheRosterAddressWithoutItsPort(string action)
    {
        // The roster stores ip:port because that is what a connection log carries; the game moderates
        // the host. The port is ephemeral, so sending it would address a socket that no longer exists.
        Engine.Reset();
        string identity = SeedPlayer(ByIp, id: null, name: "Walterus", addr: "95.19.50.122:61543");

        HttpResponseMessage resp = await Client(KgsmTier.Operator)
            .PostAsync($"/api/v1/servers/{ByIp}/players/{identity}/{action}?origin=ui", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(Engine.LastCall);
        Assert.Equal(action, Engine.LastCall!.Verb);
        Assert.Equal(ByIp, Engine.LastCall.Instance);
        Assert.Equal("95.19.50.122", Engine.LastCall.Target);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(action, doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("ip", doc.RootElement.GetProperty("targetKind").GetString());
        // The resolved token is deliberately absent from the response — handing it back would invite
        // a client to start sending it.
        Assert.False(doc.RootElement.TryGetProperty("target", out _));

        History.Reset(ByIp);
    }

    [Fact]
    public async Task NameKeyedGame_SendsTheRosterName()
    {
        Engine.Reset();
        string identity = SeedPlayer(ByName, id: "uuid-1", name: "Notch", addr: "10.0.0.5:2222");

        HttpResponseMessage resp = await Client(KgsmTier.Operator)
            .PostAsync($"/api/v1/servers/{ByName}/players/{identity}/ban", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // The game asked for a name, so the name is sent — even though the record also carries an
        // address and an id. The blueprint decides, not whichever field looks most specific.
        Assert.Equal("Notch", Engine.LastCall!.Target);

        History.Reset(ByName);
    }

    [Fact]
    public async Task ProvenanceIsThreadedOntoTheEngineCall()
    {
        // The audit row is written from kgsm's echo, so if actor/origin do not reach the engine the
        // trail cannot name who did it.
        Engine.Reset();
        string identity = SeedPlayer(ByIp, id: null, name: "Walterus", addr: "95.19.50.122:61543");

        await Client(KgsmTier.Operator)
            .PostAsync($"/api/v1/servers/{ByIp}/players/{identity}/kick?origin=ui", null);

        Assert.False(string.IsNullOrWhiteSpace(Engine.LastCall!.Actor));
        Assert.Equal("ui", Engine.LastCall.Origin);

        History.Reset(ByIp);
    }

    [Fact]
    public async Task BadOrigin_400_AndNothingIsSent()
    {
        Engine.Reset();
        string identity = SeedPlayer(ByIp, id: null, name: "Walterus", addr: "95.19.50.122:61543");

        HttpResponseMessage resp = await Client(KgsmTier.Operator)
            .PostAsync($"/api/v1/servers/{ByIp}/players/{identity}/kick?origin=system", null);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(Engine.LastCall);

        History.Reset(ByIp);
    }

    [Fact]
    public async Task EngineRefuses_502_NotAFakeSuccess()
    {
        // e.g. the server is not running — the engine refuses and the API must not report a kick
        // that never happened.
        Engine.Reset();
        Engine.FailWith = "Cannot kick on 'romestead': the server is not running";
        string identity = SeedPlayer(ByIp, id: null, name: "Walterus", addr: "95.19.50.122:61543");

        HttpResponseMessage resp = await Client(KgsmTier.Operator)
            .PostAsync($"/api/v1/servers/{ByIp}/players/{identity}/kick", null);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Contains("not running", doc.RootElement.GetProperty("error").GetProperty("message").GetString());

        Engine.FailWith = null;
        History.Reset(ByIp);
    }

    [Fact]
    public void BanAndUnban_DoNotTouchLastSeen_OnlyPresenceDoes()
    {
        // lastSeen means "when this player was last PRESENT". Moderating someone who is not
        // connected is not a sighting, and because the roster sorts on lastSeen, writing the moment
        // of the ban would also jump them up the list for something they did not do.
        History.Reset(ByIp);
        var joinedAt = DateTimeOffset.UtcNow.AddHours(-3);
        History.Join(ByIp, sessionKey: "1.2.3.4:5", id: null, name: "Stale", addr: "1.2.3.4:5", joinedAt);
        History.Leave(ByIp, sessionKey: "1.2.3.4:5", id: null, name: "Stale", addr: "1.2.3.4:5", joinedAt);

        string identity = History.GetRoster(ByIp).Single(p => p.PlayerName == "Stale").PlayerIdentity;
        DateTimeOffset before = History.GetRoster(ByIp).Single(p => p.PlayerName == "Stale").LastSeen;

        History.Ban(ByIp, identity, reason: null);
        RosterPlayer banned = History.GetRoster(ByIp).Single(p => p.PlayerName == "Stale");
        Assert.Equal(PlayerStatus.banned, banned.Status);
        Assert.Equal(before, banned.LastSeen);

        History.Unban(ByIp, identity);
        RosterPlayer lifted = History.GetRoster(ByIp).Single(p => p.PlayerName == "Stale");
        Assert.Equal(PlayerStatus.offline, lifted.Status);
        Assert.Null(lifted.BanReason);
        Assert.Equal(before, lifted.LastSeen);

        History.Reset(ByIp);
    }

    [Fact]
    public async Task RosterResponse_ReportsWhatTheGameSupports()
    {
        HttpResponseMessage resp = await Client(KgsmTier.Operator).GetAsync($"/api/v1/servers/{ByIp}/players");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement mod = doc.RootElement.GetProperty("moderation");
        Assert.True(mod.GetProperty("kick").GetBoolean());
        Assert.True(mod.GetProperty("ban").GetBoolean());
        Assert.True(mod.GetProperty("unban").GetBoolean());
        Assert.Equal("ip", mod.GetProperty("targetKind").GetString());
    }

    [Fact]
    public async Task RosterResponse_NeverClaimsSupportTheBlueprintDidNotDeclare()
    {
        HttpResponseMessage resp = await Client(KgsmTier.Operator)
            .GetAsync($"/api/v1/servers/{NoModeration}/players");

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement mod = doc.RootElement.GetProperty("moderation");
        Assert.False(mod.GetProperty("kick").GetBoolean());
        Assert.False(mod.GetProperty("ban").GetBoolean());
        Assert.False(mod.GetProperty("unban").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mod.GetProperty("targetKind").ValueKind);
    }

    public sealed class ModerationTestFactory : AuthTestFactory
    {
        // One instance shared with the tests so they can read back what the engine was handed. The
        // suite's assertions are call-ordered, so the class is not run in parallel with itself.
        internal static readonly FakeModerationInstanceService Engine = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(Engine);
            });
        }
    }

    internal sealed record ModerationCall(string Verb, string Instance, string Target, string? Actor, string? Origin);

    internal sealed class FakeModerationInstanceService : IInstanceService
    {
        public ModerationCall? LastCall { get; private set; }
        public string? FailWith { get; set; }

        public void Reset()
        {
            LastCall = null;
            FailWith = null;
        }

        private KgsmResult Record(string verb, string instance, string target, string? actor, string? origin)
        {
            LastCall = new ModerationCall(verb, instance, target, actor, origin);
            return FailWith is null ? new KgsmResult(0, "", "") : new KgsmResult(1, "", FailWith);
        }

        public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null)
            => Record("kick", instanceName, target, actor, origin);

        public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null)
            => Record("ban", instanceName, target, actor, origin);

        public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null)
            => Record("unban", instanceName, target, actor, origin);

        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();
        public Dictionary<string, Instance> GetAll() => new()
        {
            [ByIp] = new Instance
            {
                Name = ByIp,
                BlueprintFile = "romestead.bp.yaml",
                PlayerJoinedRegex = "logged in",
                PlayerLeftRegex = "disconnected",
                KickCommand = "kick {ip}",
                BanCommand = "ban {ip}",
                UnbanCommand = "unban {ip}",
            },
            [ByName] = new Instance
            {
                Name = ByName,
                BlueprintFile = "minecraft.bp.yaml",
                PlayerJoinedRegex = "joined the game",
                PlayerLeftRegex = "left the game",
                KickCommand = "kick {name}",
                BanCommand = "ban {name}",
                UnbanCommand = "pardon {name}",
            },
            [NoModeration] = new Instance
            {
                Name = NoModeration,
                BlueprintFile = "rust.bp.yaml",
                PlayerJoinedRegex = "connected",
                PlayerLeftRegex = "disconnected",
            },
        };

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) =>
            GetAll().ToDictionary(
                kv => kv.Key,
                kv => Reading<InstanceRuntimeStatus>.Measured(
                    new InstanceRuntimeStatus { InstanceName = kv.Key, Status = true }));

        public Instance? GetInstanceInfo(string instanceName) => GetAll().GetValueOrDefault(instanceName);

        // --- unused by this endpoint: honest NotImplemented (never silently fabricate) ---
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null, bool? start = null) => throw new NotImplementedException();
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
        public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public List<InstanceBackup> GetBackupsDetailed(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
