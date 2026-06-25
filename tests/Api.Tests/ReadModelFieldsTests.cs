using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Tier-1 additive read-model fields, proven through the real read pipeline with the engine seam faked:
/// <list type="bullet">
///   <item><c>updateAvailable</c> (bool?) + <c>startedAt</c> (ISO-UTC) on the Server DTO — sourced from the
///     instance status reading's <c>VersionInfo.UpdatesAvailable</c> / <c>ProcessInfo.StartTime</c>.</item>
///   <item><c>panelVersion</c> on the Host DTO — the in-process <see cref="ApiInfo.ApiVersion"/>, no upstream dep.</item>
/// </list>
/// The honesty-coupling is the load-bearing assertion: a field present ⇒ it was really sourced; absent/unparseable
/// ⇒ explicit <c>null</c>, never a fabricated <c>false</c>/<c>0</c>/guessed timezone. Each field is asserted both
/// when sourced (appears with the right value) and when not (present-as-null).
/// </summary>
public sealed class ReadModelFieldsTests
    : IClassFixture<ReadModelFieldsTests.StatusEngineFactory>, IClassFixture<AuthTestFactory>
{
    private readonly StatusEngineFactory _engine;   // a fake engine with a rich status roster
    private readonly AuthTestFactory _base;          // no engine — for the host-only panelVersion read

    public ReadModelFieldsTests(StatusEngineFactory engine, AuthTestFactory @base)
    {
        _engine = engine;
        _base = @base;
    }

    // --- panelVersion (Host DTO) -------------------------------------------------------------------

    [Fact]
    public async Task Host_PanelVersion_IsApiVersion_OnListAndDetail()
    {
        HttpClient c = Client(_base, AuthTier.Viewer);

        // GET /hosts (list) — the single host carries panelVersion == the in-process ApiInfo.ApiVersion.
        using JsonDocument list = await GetJson(c, "/api/v1/hosts");
        JsonElement host = list.RootElement.EnumerateArray().Single();
        Assert.Equal(ApiInfo.ApiVersion, host.GetProperty("panelVersion").GetString());

        // GET /hosts/{id} (detail) — same value, no drift.
        using JsonDocument detail = await GetJson(c, $"/api/v1/hosts/{AuthTestFactory.HostId}");
        Assert.Equal(ApiInfo.ApiVersion, detail.RootElement.GetProperty("panelVersion").GetString());
    }

    [Fact]
    public async Task Host_PanelVersion_MatchesApiRootHandshake()
    {
        // panelVersion must equal the GET /api/v1 handshake version — they share one const, so the
        // SPA's connectivity check and the host card report the same panel version.
        using JsonDocument root = await GetJson(_base.CreateClient(), "/api/v1");
        string handshake = root.RootElement.GetProperty("version").GetString()!;

        using JsonDocument list = await GetJson(Client(_base, AuthTier.Viewer), "/api/v1/hosts");
        string panel = list.RootElement.EnumerateArray().Single().GetProperty("panelVersion").GetString()!;

        Assert.Equal(handshake, panel);
    }

    // --- updateAvailable + startedAt (Server DTO) --------------------------------------------------

    [Fact]
    public async Task Server_UpdateAvailable_And_StartedAt_AppearWhenSourced()
    {
        // "factorio-checked" is a running instance whose measured status reports a checked update (true)
        // and a parseable UTC process start time -> both fields surface with their real values.
        JsonElement srv = await GetServer(_engine, "factorio-checked");

        Assert.True(srv.GetProperty("updateAvailable").GetBoolean());
        // ISO-8601 UTC Z (the api timestamp convention) — the exact moment kgsm reported, not "now".
        Assert.Equal("2026-06-16T14:23:01.0000000Z", srv.GetProperty("startedAt").GetString());
    }

    [Fact]
    public async Task Server_UpdateAvailable_False_WhenCheckedAndUpToDate()
    {
        // A measured, checked instance that is up to date reports updateAvailable:false — a REAL false
        // (the check ran), distinct from the unchecked null below.
        JsonElement srv = await GetServer(_engine, "factorio-uptodate");
        Assert.False(srv.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task Server_UpdateAvailable_Null_WhenUnchecked_NeverFabricatedFalse()
    {
        // The honesty case mirroring production (fast mode): the status is measured but the update check
        // did NOT run (Version.Checked=false -> UpdatesAvailable=null). The field must be present and
        // null — NOT a fabricated false that would read as "no update available".
        JsonElement srv = await GetServer(_engine, "factorio-unchecked");

        JsonElement ua = srv.GetProperty("updateAvailable");
        Assert.Equal(JsonValueKind.Null, ua.ValueKind);
    }

    [Fact]
    public async Task Server_StartedAt_Null_WhenStopped()
    {
        // A stopped instance has no process -> StartTime null -> startedAt present-as-null.
        JsonElement srv = await GetServer(_engine, "factorio-stopped");

        JsonElement sa = srv.GetProperty("startedAt");
        Assert.Equal(JsonValueKind.Null, sa.ValueKind);
        Assert.Equal("stopped", srv.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Server_StartedAt_Null_WhenUnspecifiedKind_NeverGuessesTimezone()
    {
        // The real kgsm case: start_time is a naive local-time string (DateTimeKind.Unspecified after the
        // lib's STJ parse). Its offset is unknown, so we surface null rather than stamping a guessed zone
        // — the honesty invariant. (In production the lib can't even parse this form; here we prove the
        // mapping's own guard refuses an Unspecified-kind value.)
        JsonElement srv = await GetServer(_engine, "factorio-naive");

        JsonElement sa = srv.GetProperty("startedAt");
        Assert.Equal(JsonValueKind.Null, sa.ValueKind);
    }

    [Fact]
    public async Task Server_NewFields_Null_WhenStatusUnreadable()
    {
        // An Unavailable reading (management file can't answer) -> status unknown, and the new fields are
        // present-as-null (never sourced from a non-measured reading).
        JsonElement srv = await GetServer(_engine, "factorio-broken");

        Assert.Equal("unknown", srv.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, srv.GetProperty("updateAvailable").ValueKind);
        Assert.Equal(JsonValueKind.Null, srv.GetProperty("startedAt").ValueKind);
    }

    [Fact]
    public async Task Server_NewFields_RideTheDetailView()
    {
        // The fields are on the shared Server record, so GET /servers/{id} (detail) carries them too.
        using JsonDocument doc = await GetJson(Client(_engine, AuthTier.Viewer), "/api/v1/servers/factorio-checked");
        JsonElement srv = doc.RootElement;
        Assert.True(srv.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal("2026-06-16T14:23:01.0000000Z", srv.GetProperty("startedAt").GetString());
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static HttpClient Client(AuthTestFactory factory, AuthTier tier)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(tier));
        return c;
    }

    private static async Task<JsonDocument> GetJson(HttpClient c, string path)
    {
        HttpResponseMessage resp = await c.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Fetch the GET /servers list and return the element with the given id (a JsonElement clone
    /// so it outlives the parsed document).</summary>
    private static async Task<JsonElement> GetServer(AuthTestFactory factory, string id)
    {
        using JsonDocument doc = await GetJson(Client(factory, AuthTier.Viewer), "/api/v1/servers");
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
            if (e.GetProperty("id").GetString() == id)
                return e.Clone();
        throw new Xunit.Sdk.XunitException($"server '{id}' not in the /servers list");
    }

    /// <summary>An <see cref="AuthTestFactory"/> with a fake engine whose roster exercises every branch of
    /// the new field mapping (checked/unchecked update, UTC/naive/stopped start time, an unreadable status).</summary>
    public sealed class StatusEngineFactory : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new StatusFakeInstanceService());
            });
        }
    }

    /// <summary>
    /// Switch-on-input fake (the project convention): a fixed roster + a matching status map that drives
    /// each mapping branch. No mutable per-call state. Mutating/log/etc. members throw NotImplemented (honest
    /// — the read path never calls them).
    /// </summary>
    private sealed class StatusFakeInstanceService : IInstanceService
    {
        private static readonly string[] Ids =
            ["factorio-checked", "factorio-uptodate", "factorio-unchecked", "factorio-stopped", "factorio-naive", "factorio-broken"];

        public Dictionary<string, Instance> GetAll() =>
            Ids.ToDictionary(id => id, id => new Instance { Name = id, BlueprintFile = "factorio.bp.yaml" });

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new()
        {
            // Running, update check ran and found an update, parseable UTC start time.
            ["factorio-checked"] = Measured(running: true, checkedUpdate: true, updatesAvailable: true,
                start: new DateTime(2026, 6, 16, 14, 23, 1, DateTimeKind.Utc)),
            // Running, update check ran and the server is up to date -> a REAL false.
            ["factorio-uptodate"] = Measured(running: true, checkedUpdate: true, updatesAvailable: false,
                start: new DateTime(2026, 6, 16, 14, 23, 1, DateTimeKind.Utc)),
            // Running, fast mode -> no check -> updates_available null (the production default).
            ["factorio-unchecked"] = Measured(running: true, checkedUpdate: false, updatesAvailable: null,
                start: new DateTime(2026, 6, 16, 14, 23, 1, DateTimeKind.Utc)),
            // Stopped -> no process -> StartTime null.
            ["factorio-stopped"] = Measured(running: false, checkedUpdate: false, updatesAvailable: null, start: null),
            // Running but the start time has an UNKNOWN offset (Unspecified kind, as kgsm's lstart parses) ->
            // startedAt must be null, not a guessed zone.
            ["factorio-naive"] = Measured(running: true, checkedUpdate: false, updatesAvailable: null,
                start: new DateTime(2026, 6, 16, 14, 23, 1, DateTimeKind.Unspecified)),
            // The management file couldn't answer --status -> a typed Unavailable reading.
            ["factorio-broken"] = Reading<InstanceRuntimeStatus>.Unavailable("requires regeneration", ReadingCode.RequiresRegeneration),
        };

        private static Reading<InstanceRuntimeStatus> Measured(
            bool running, bool checkedUpdate, bool? updatesAvailable, DateTime? start) =>
            Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus
            {
                Status = running,
                Process = new ProcessInfo { StartTime = start },
                Version = new VersionInfo { Current = "1.1.0", Checked = checkedUpdate, UpdatesAvailable = updatesAvailable },
            });

        // --- not exercised by the read path: honest NotImplemented (never silently fabricate) ---
        public Instance? GetInstanceInfo(string instanceName) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetInstalledVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetLatestVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult CheckUpdate(string instanceName) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
