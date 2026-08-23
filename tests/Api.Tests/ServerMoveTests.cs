using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>POST /servers/{id}/move</c> — the panel's route to moving an instance's files onto another disk,
/// proven through the real pipeline with the engine seam faked.
/// </summary>
/// <remarks>
/// <para>
/// What these hold is the <b>gate</b>: which refusals this API can answer from what it already holds, and
/// which it must leave to the engine. The four it answers — an unknown server, a library this host does
/// not carry, one whose root is away, and the library the instance is already in — exist so the form
/// answers beside its own selector instead of producing a job that fails a moment later somewhere nobody
/// is looking. Free space is deliberately NOT among them: the engine measures what the instance actually
/// occupies before it copies, and a second measurement here could disagree with the one that decides.
/// </para>
/// <para>
/// The happy path settles as a <c>202</c> + a job whose verb is <c>move</c>. That job holding the
/// server's in-flight slot is the whole point of the shape — the engine starts the instance once on the
/// new path to confirm it runs there, so run-state flickers "running" partway through and a surface needs
/// a span it can trust instead.
/// </para>
/// </remarks>
public sealed class ServerMoveTests
    : IClassFixture<ServerMoveTests.MoveEngineFactory>, IClassFixture<AuthTestFactory>
{
    private readonly MoveEngineFactory _engine;
    private readonly AuthTestFactory _noEngine;

    public ServerMoveTests(MoveEngineFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
    }

    [Fact]
    public async Task Move_MissingLibrary_400()
    {
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Admin, "stopped-1", "{}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Move_BadOrigin_400()
    {
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Admin, "stopped-1",
            "{\"library\":\"archive\",\"origin\":\"hacker\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Move_UnknownServer_404()
    {
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Admin, "not-a-server",
            "{\"library\":\"archive\"}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Move_UnknownLibrary_400_NamingIt()
    {
        // A name this host does not carry is a client-input problem, like an unknown blueprint on install.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Admin, "stopped-1",
            "{\"library\":\"nosuch\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("nosuch", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Move_OfflineTargetLibrary_409_NamingTheRoot()
    {
        // A conflict, not a malformed request: the name is right and the move will work once the disk is
        // back. The path is what tells somebody which one to plug in.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Admin, "stopped-1",
            "{\"library\":\"away\"}");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("/mnt/away", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Move_IntoItsOwnLibrary_409()
    {
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Admin, "stopped-1",
            "{\"library\":\"default\"}");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("already in library", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Move_RunningServer_409()
    {
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Admin, "running-1",
            "{\"library\":\"archive\"}");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("stop it", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Move_UnknownRunState_IsAdmitted()
    {
        // The same rule CommandGate holds for every other verb: an unknown status never blocks. The
        // engine refuses a running instance itself, so guessing here would only refuse a move that would
        // have worked.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Admin, "unread-1",
            "{\"library\":\"archive\"}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task Move_Accepted_ReturnsAMoveJob()
    {
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Admin, "stopped-1",
            "{\"library\":\"archive\",\"origin\":\"ui\"}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"verb\":\"move\"", body, StringComparison.Ordinal);
        Assert.Contains("\"serverId\":\"stopped-1\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Move_Operator_403()
    {
        // Placement shapes the host, so it takes the same authority as registering a library — not the
        // operator tier that covers acting on one server.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator, "stopped-1",
            "{\"library\":\"archive\"}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Move_NoToken_401()
    {
        HttpResponseMessage resp = await Post(_engine, tier: null, "stopped-1", "{\"library\":\"archive\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Move_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Post(_noEngine, KgsmTier.Admin, "anything",
            "{\"library\":\"archive\"}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static HttpClient Client(AuthTestFactory factory, KgsmTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Post(
        AuthTestFactory factory, KgsmTier? tier, string id, string json) =>
        Client(factory, tier).PostAsync($"/api/v1/servers/{id}/move",
            new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>
    /// <see cref="AuthTestFactory"/> with a faked engine and library registry, so the move gate runs every
    /// branch it owns without a live kgsm.
    /// </summary>
    public sealed class MoveEngineFactory : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new MoveFakeInstanceService());
                services.RemoveAll<ILibraryService>();
                services.AddSingleton<ILibraryService>(new FakeLibraryRegistry());
            });
        }
    }

    /// <summary>Three instances, one per run-state the gate distinguishes: stopped, running, unread.</summary>
    private sealed class MoveFakeInstanceService : IInstanceService
    {
        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();

        public Dictionary<string, Instance> GetAll() => new()
        {
            ["stopped-1"] = InDefault("stopped-1"),
            ["running-1"] = InDefault("running-1"),
            // The engine reports neither a runtime nor a run state for an instance it cannot read.
            ["unread-1"] = new Instance
            {
                Name = "unread-1",
                Blueprint = "factorio",
                Library = "away",
                LibraryDir = "/mnt/away",
                LibraryState = InstanceLibraryState.Offline,
            },
        };

        private static Instance InDefault(string id) => new()
        {
            Name = id,
            BlueprintFile = "factorio.bp.yaml",
            Runtime = InstanceRuntime.Native,
            Library = "default",
            LibraryDir = "/opt",
            LibraryState = InstanceLibraryState.Online,
        };

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new()
        {
            ["stopped-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "stopped-1", Status = false }),
            ["running-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "running-1", Status = true }),
            ["unread-1"] = Reading<InstanceRuntimeStatus>.Measured(
                new InstanceRuntimeStatus { InstanceName = "unread-1", Status = null }),
        };

        // The gate settles on the HTTP response; the engine call itself runs off-request and succeeds.
        public KgsmResult Move(string instanceName, string library, bool skipSpaceCheck = false,
            string? actor = null, string? origin = null) => new(0);

        // --- unused by the move gate: honest NotImplemented (never silently fabricate) ---
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? library = null, string? version = null,
            string? displayName = null, string? actor = null, string? origin = null, int? port = null,
            bool? start = null, string? id = null) => throw new NotImplementedException();
        public KgsmResult SetDisplayName(string instanceId, string displayName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Instance? GetInstanceInfo(string instanceName) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
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
        public KgsmResult CheckUpdate(string instanceName, bool emit = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public List<InstanceBackup> GetBackupsDetailed(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null, string? reason = null, string? retention = null) => throw new NotImplementedException();
        public KgsmResult PinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult UnpinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public List<InstanceConfigEntry>? GetInstanceConfig(string instanceName, bool settableOnly = false) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult DeleteBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    /// <summary>One mounted target, one whose root is away, and the library the fixtures live in.</summary>
    private sealed class FakeLibraryRegistry : ILibraryService
    {
        public List<Library>? List() =>
        [
            new() { Name = "default", Path = "/opt", State = LibraryState.Online },
            new() { Name = "archive", Path = "/mnt/archive", State = LibraryState.Online },
            new() { Name = "away", Path = "/mnt/away", State = LibraryState.Offline },
        ];

        public KgsmResult Add(string path, string? name = null, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Remove(string name, bool force = false, string? drainTo = null, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Rename(string oldName, string newName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    }
}
