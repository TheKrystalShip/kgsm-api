using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Controllers;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The rename surface — <c>PUT /servers/{id}/display-name</c> and <c>DELETE</c> (clear) — proven
/// through the real pipeline with the engine seam faked. The load-bearing contracts: the tier split (a
/// viewer cannot rename), the route is keyed on the immutable id and never on a label, an over-cap label
/// is rejected rather than truncated, an empty PUT is refused so an emptied field cannot silently strip a
/// server's name, a clear reports the id the server now reads as, and the write is attributed so the
/// engine's echo can name who did it.
/// </summary>
public sealed class ServerDisplayNameTests
    : IClassFixture<ServerDisplayNameTests.RenameEngineFactory>, IClassFixture<AuthTestFactory>
{
    private readonly RenameEngineFactory _engine;
    private readonly AuthTestFactory _noEngine;   // engine unprovisioned → the 503 degrade

    public ServerDisplayNameTests(RenameEngineFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
    }

    [Fact]
    public async Task Put_NoToken_401()
    {
        HttpResponseMessage resp = await Put(_engine, tier: null, "labelled", "{\"displayName\":\"x\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Put_Viewer_403()
    {
        // Renaming is a write: a viewer who can read the label cannot change it.
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Viewer, "labelled", "{\"displayName\":\"x\"}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Put_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Put(_noEngine, KgsmTier.Operator, "labelled", "{\"displayName\":\"x\"}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_UnknownServer_404()
    {
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "does-not-exist", "{\"displayName\":\"x\"}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Put_ByLabelRatherThanId_404()
    {
        // Labels are not unique and are not identifiers. Resolving one here would let two servers sharing
        // a name rename each other, so the route only ever means the id.
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "Sunday Server", "{\"displayName\":\"x\"}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Put_EmptyLabel_400()
    {
        // An emptied field must not silently strip a server's name — clearing is DELETE.
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "labelled", "{\"displayName\":\"   \"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_ControlCharactersOnly_400()
    {
        // Sanitizing leaves nothing, so this is the empty case even though the body was not empty —
        // measured after normalization, like the note's cap.
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "labelled", "{\"displayName\":\"\\u0007\\u0007\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Put_OverCap_400_RejectedNotTruncated()
    {
        string tooLong = new('x', ServerDisplayNameController.MaxLength + 1);
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "labelled",
            $"{{\"displayName\":\"{tooLong}\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_BadOrigin_400()
    {
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "labelled",
            "{\"displayName\":\"x\",\"origin\":\"hacker\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Put_Valid_200_StoresSanitizedLabel_Attributed()
    {
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "writable",
            "{\"displayName\":\"  Sunday Server \\u2728  \",\"origin\":\"ui\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        RecordingRenameService fake = _engine.Fake;
        Assert.Equal("writable", fake.LastDisplayNameInstance);
        // Trimmed, but the emoji and the spacing inside survive — the label never reaches a path.
        Assert.Equal("Sunday Server ✨", fake.LastDisplayName);
        Assert.Equal("ui", fake.LastDisplayNameOrigin);
        Assert.NotNull(fake.LastDisplayNameActor);
    }

    [Fact]
    public async Task Put_Valid_200_ReportsWhatTheEngineStored()
    {
        // The response is a re-read, not an echo of the request: what the engine holds is the answer.
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "labelled", "{\"displayName\":\"anything\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("labelled", doc.RootElement.GetProperty("serverId").GetString());
        Assert.Equal("Sunday Server", doc.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Put_EngineRefuses_500_WithItsOwnDetail()
    {
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "refuses", "{\"displayName\":\"x\"}");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"engine_refused\"", body);
        Assert.Contains("display_name is protected", body);
    }

    [Fact]
    public async Task Delete_ClearsTheLabel_AndReportsTheIdItNowReadsAs()
    {
        HttpResponseMessage resp = await Delete(_engine, KgsmTier.Operator, "unlabelled");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The empty label is what the engine is asked for; the instance then reads as its id, and the
        // response says so rather than reporting a blank name.
        Assert.Equal(string.Empty, _engine.Fake.LastDisplayName);
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("unlabelled", doc.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Delete_Viewer_403()
    {
        HttpResponseMessage resp = await Delete(_engine, KgsmTier.Viewer, "labelled");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ServerDto_CarriesTheLabelAsName_AndTheIdAsId()
    {
        // The seam this whole phase fills: `name` is the label somebody reads, `id` is the key. An
        // instance with no label of its own reports its id in both, never a blank name.
        HttpResponseMessage resp = await Client(_engine, KgsmTier.Viewer).GetAsync("/api/v1/servers");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Dictionary<string, string?> byId = doc.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("name").GetString());

        Assert.Equal("Sunday Server", byId["labelled"]);
        Assert.Equal("unlabelled", byId["unlabelled"]);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static HttpClient Client(AuthTestFactory factory, KgsmTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Put(AuthTestFactory factory, KgsmTier? tier, string id, string json) =>
        Client(factory, tier).PutAsync($"/api/v1/servers/{Uri.EscapeDataString(id)}/display-name",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> Delete(AuthTestFactory factory, KgsmTier? tier, string id) =>
        Client(factory, tier).DeleteAsync($"/api/v1/servers/{Uri.EscapeDataString(id)}/display-name");

    /// <summary>
    /// <see cref="AuthTestFactory"/> with a recording fake <see cref="IInstanceService"/>, so the rename
    /// path exercises its real branches (roster gate, the engine write, the post-write re-read).
    /// </summary>
    public class RenameEngineFactory : AuthTestFactory
    {
        public RecordingRenameService Fake { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(Fake);
                services.RemoveAll<IWatchdogClient>();
            });
        }
    }

    /// <summary>
    /// A roster covering every label state — one carrying a label, one with none (so the id-fallback is
    /// reachable), one that accepts writes, and one whose engine refuses the key. Writes are recorded
    /// rather than applied, so the read side stays fixed and the fake needs no locking.
    /// </summary>
    public sealed class RecordingRenameService : IInstanceService
    {
        public string? LastDisplayNameInstance { get; private set; }
        public string? LastDisplayName { get; private set; }
        public string? LastDisplayNameActor { get; private set; }
        public string? LastDisplayNameOrigin { get; private set; }

        private static Instance Labelled() => new()
        {
            Name = "labelled",
            BlueprintFile = "factorio.bp.yaml",
            DisplayName = "Sunday Server",
        };

        // No display_name in the config — Instance.DisplayName resolves to the id, which is what the DTO
        // must carry rather than a blank.
        private static Instance Unlabelled() => new() { Name = "unlabelled", BlueprintFile = "factorio.bp.yaml" };

        private static Instance Writable() => new() { Name = "writable", BlueprintFile = "factorio.bp.yaml" };

        private static Instance Refuses() => new() { Name = "refuses", BlueprintFile = "factorio.bp.yaml" };

        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();

        public Dictionary<string, Instance> GetAll() => new()
        {
            ["labelled"] = Labelled(),
            ["unlabelled"] = Unlabelled(),
            ["writable"] = Writable(),
            ["refuses"] = Refuses(),
        };

        public Instance? GetInstanceInfo(string instanceName) => instanceName switch
        {
            "labelled" => Labelled(),
            "unlabelled" => Unlabelled(),
            "writable" => Writable(),
            "refuses" => Refuses(),
            _ => null,
        };

        public KgsmResult SetDisplayName(string instanceId, string displayName, string? actor = null, string? origin = null)
        {
            LastDisplayNameInstance = instanceId;
            LastDisplayName = displayName;
            LastDisplayNameActor = actor;
            LastDisplayNameOrigin = origin;

            return instanceId == "refuses"
                ? new KgsmResult(8, "", "display_name is protected")
                : new KgsmResult(0);
        }

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new();

        // --- unused by the rename path: honest NotImplemented (never silently fabricate) ---
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? id = null) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? library = null, string? version = null, string? displayName = null, string? actor = null, string? origin = null, int? port = null, bool? start = null, string? id = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Move(string instanceName, string library, bool skipSpaceCheck = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
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
        public KgsmResult Announce(string instanceName, string message, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
