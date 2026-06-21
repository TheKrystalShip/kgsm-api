using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Coverage for the Tier-1 ops write/read surfaces — per-server config (<c>GET</c>/<c>PATCH /servers/{id}/config</c>),
/// the <c>update</c> command verb, and backups (<c>GET</c>/<c>POST /servers/{id}/backups</c>,
/// <c>POST /servers/{id}/backups/restore</c>) — proven through the real pipeline with the engine seam faked
/// (a switch-on-input <see cref="FakeOpsInstanceService"/>, the project convention). The load-bearing
/// contracts asserted synchronously on the HTTP response: the gate (404/409/503/400), the auth tiers
/// (viewer-read / operator-write), and the editable-key boundary (protected keys ⇒ 400, nothing applied).
/// The async happy path mutates the host and is a trusted-host live-validate (like M3/M8·b); here the 202 +
/// job shape and the no-double-write invariant (update/backup are the kgsm echo path — the API writes no
/// audit row, so /audit stays empty) are what's verified.
/// </summary>
public sealed class Tier1OpsTests
    : IClassFixture<Tier1OpsTests.EngineTestFactory>, IClassFixture<AuthTestFactory>
{
    private const string Server = "factorio-1";   // in the fake roster (stopped by default)
    private const string Running = "valheim-1";   // in the fake roster, reported RUNNING

    private readonly EngineTestFactory _engine;   // fake engine registered → the gate's real branches
    private readonly AuthTestFactory _noEngine;   // engine unprovisioned → the 503 degrade

    public Tier1OpsTests(EngineTestFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
    }

    // ===== config: GET /servers/{id}/config ========================================================

    [Fact]
    public async Task ConfigGet_Known_200_EditableMap()
    {
        HttpResponseMessage resp = await Client(_engine, AuthTier.Viewer).GetAsync($"/api/v1/servers/{Server}/config");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal(Server, root.GetProperty("serverId").GetString());

        JsonElement values = root.GetProperty("values");
        // Editable keys are surfaced (kgsm snake_case, verbatim) with stringified values. Assertions are
        // order-independent of the PATCH test (which shares this stateful fake): a bool key stringifies to
        // "true"/"false" (not 0/1), an int key to its decimal form — the SHAPE, not a mutable seed value.
        Assert.Contains(values.GetProperty("auto_update").GetString(), new[] { "true", "false" }); // bool form
        Assert.True(values.TryGetProperty("executable_arguments", out _));
        Assert.Equal("30", values.GetProperty("stop_command_timeout_seconds").GetString());        // int → "30"
        Assert.True(values.TryGetProperty("level_name", out _));
        // Protected keys are NOT offered (the IsEditableKey boundary = kgsm's protected complement).
        Assert.False(values.TryGetProperty("name", out _));
        Assert.False(values.TryGetProperty("install_dir", out _));
        Assert.False(values.TryGetProperty("ports", out _));
        Assert.False(values.TryGetProperty("enable_firewall_management", out _));
    }

    [Fact]
    public async Task ConfigGet_Unknown_404()
    {
        HttpResponseMessage resp = await Client(_engine, AuthTier.Viewer).GetAsync("/api/v1/servers/nope/config");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ConfigGet_NoToken_401()
    {
        HttpResponseMessage resp = await Client(_engine, tier: null).GetAsync($"/api/v1/servers/{Server}/config");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ConfigGet_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Client(_noEngine, AuthTier.Viewer).GetAsync($"/api/v1/servers/{Server}/config");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    // ===== config: PATCH /servers/{id}/config ======================================================

    [Fact]
    public async Task ConfigPatch_Valid_200_AppliedAndFreshConfig()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, Server,
            "{\"values\":{\"auto_update\":\"false\",\"level_name\":\"newworld\"}}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        string[] applied = root.GetProperty("applied").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(new[] { "auto_update", "level_name" }, applied);
        // The fresh config reflects the writes (the fake echoes set values back through GetInstanceInfo).
        Assert.Equal("false", root.GetProperty("config").GetProperty("values").GetProperty("auto_update").GetString());
        Assert.Equal("newworld", root.GetProperty("config").GetProperty("values").GetProperty("level_name").GetString());
    }

    [Fact]
    public async Task ConfigPatch_ProtectedKey_400_NothingApplied()
    {
        // A protected key trips the pre-check → 400 before any write (stricter than the engine, not a bypass).
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, Server,
            "{\"values\":{\"auto_update\":\"true\",\"install_dir\":\"/evil\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"bad_request\"", body);
        Assert.Contains("install_dir", body);
    }

    [Fact]
    public async Task ConfigPatch_EngineRefusesMidApply_400_ReportsAlreadyApplied()
    {
        // The fake refuses the sentinel value "BOOM" (an engine refusal that passes the key pre-check). The
        // first key applies, the second is refused → 400 surfacing the real stderr AND the already-applied key
        // (honest about the non-atomic partial state).
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, Server,
            "{\"values\":{\"level_name\":\"ok\",\"save_command\":\"BOOM\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"bad_request\"", body);
        Assert.Contains("already applied: level_name", body);
    }

    [Fact]
    public async Task ConfigPatch_EmptyBody_400()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, Server, "{\"values\":{}}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("at least one", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConfigPatch_BadOrigin_400()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, Server,
            "{\"values\":{\"auto_update\":\"true\"},\"origin\":\"hacker\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ConfigPatch_Unknown_404()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "nope",
            "{\"values\":{\"auto_update\":\"true\"}}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ConfigPatch_Viewer_403()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Viewer, Server,
            "{\"values\":{\"auto_update\":\"true\"}}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ConfigPatch_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Patch(_noEngine, AuthTier.Operator, Server,
            "{\"values\":{\"auto_update\":\"true\"}}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ===== update verb: POST /servers/{id}/commands ================================================

    [Fact]
    public async Task Update_Valid_202_UpdateJob_NoAuditDoubleWrite()
    {
        // factorio-1 is reported STOPPED → admissible. 202 + an update job.
        HttpResponseMessage resp = await Command(_engine, AuthTier.Operator, Server, "{\"verb\":\"update\"}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("update", job.GetProperty("verb").GetString());
        Assert.Equal(Server, job.GetProperty("serverId").GetString());

        // No double-write: update is the echo path (kgsm owns server.update). The fake emits no event and the
        // runner writes no row directly, so /audit stays empty — a stray direct write would surface here.
        HttpResponseMessage audit = await Client(_engine, AuthTier.Viewer).GetAsync("/api/v1/audit");
        using JsonDocument page = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
        Assert.Empty(page.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Update_WhenRunning_409()
    {
        // valheim-1 is reported RUNNING → kgsm refuses an update on a running instance; the gate 409s it
        // synchronously rather than accepting a doomed job.
        HttpResponseMessage resp = await Command(_engine, AuthTier.Operator, Running, "{\"verb\":\"update\"}");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("must be stopped", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Update_Viewer_403()
    {
        HttpResponseMessage resp = await Command(_engine, AuthTier.Viewer, Server, "{\"verb\":\"update\"}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Update_UnknownVerb_400_StillRejected()
    {
        // Sanity that the closed set didn't widen: a junk verb is still 400.
        HttpResponseMessage resp = await Command(_engine, AuthTier.Operator, Server, "{\"verb\":\"frobnicate\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ===== backups: GET /servers/{id}/backups ======================================================

    [Fact]
    public async Task BackupsList_Known_200_NamesOnly()
    {
        HttpResponseMessage resp = await Client(_engine, AuthTier.Viewer).GetAsync($"/api/v1/servers/{Server}/backups");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal(Server, root.GetProperty("serverId").GetString());
        JsonElement backups = root.GetProperty("backups");
        Assert.Equal(2, backups.GetArrayLength());
        Assert.Equal("factorio-1-2026-06-21.bak", backups[0].GetProperty("name").GetString());
        // Names only — no fabricated size/when/type (the engine doesn't report them).
        Assert.False(backups[0].TryGetProperty("size", out _));
        Assert.False(backups[0].TryGetProperty("when", out _));
    }

    [Fact]
    public async Task BackupsList_Unknown_404()
    {
        HttpResponseMessage resp = await Client(_engine, AuthTier.Viewer).GetAsync("/api/v1/servers/nope/backups");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task BackupsList_NoToken_401()
    {
        HttpResponseMessage resp = await Client(_engine, tier: null).GetAsync($"/api/v1/servers/{Server}/backups");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task BackupsList_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Client(_noEngine, AuthTier.Viewer).GetAsync($"/api/v1/servers/{Server}/backups");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ===== backups: POST /servers/{id}/backups (create) ============================================

    [Fact]
    public async Task BackupCreate_Valid_202_CreateJob()
    {
        HttpResponseMessage resp = await PostJson(_engine, AuthTier.Operator, $"/api/v1/servers/{Server}/backups", "{}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("backup_create", job.GetProperty("verb").GetString());
        Assert.Equal(Server, job.GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task BackupCreate_Unknown_404()
    {
        HttpResponseMessage resp = await PostJson(_engine, AuthTier.Operator, "/api/v1/servers/nope/backups", "{}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task BackupCreate_Viewer_403()
    {
        HttpResponseMessage resp = await PostJson(_engine, AuthTier.Viewer, $"/api/v1/servers/{Server}/backups", "{}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task BackupCreate_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await PostJson(_noEngine, AuthTier.Operator, $"/api/v1/servers/{Server}/backups", "{}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ===== backups: POST /servers/{id}/backups/restore =============================================

    [Fact]
    public async Task BackupRestore_Valid_202_RestoreJob()
    {
        HttpResponseMessage resp = await PostJson(_engine, AuthTier.Operator,
            $"/api/v1/servers/{Server}/backups/restore", "{\"backup\":\"factorio-1-2026-06-21.bak\"}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("backup_restore", job.GetProperty("verb").GetString());
        Assert.Equal(Server, job.GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task BackupRestore_MissingBackupName_400()
    {
        HttpResponseMessage resp = await PostJson(_engine, AuthTier.Operator,
            $"/api/v1/servers/{Server}/backups/restore", "{}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("backup name is required", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BackupRestore_Unknown_404()
    {
        HttpResponseMessage resp = await PostJson(_engine, AuthTier.Operator,
            "/api/v1/servers/nope/backups/restore", "{\"backup\":\"x.bak\"}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task BackupRestore_Viewer_403()
    {
        HttpResponseMessage resp = await PostJson(_engine, AuthTier.Viewer,
            $"/api/v1/servers/{Server}/backups/restore", "{\"backup\":\"x.bak\"}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ===== helpers =================================================================================

    private static HttpClient Client(AuthTestFactory factory, AuthTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Patch(AuthTestFactory f, AuthTier? tier, string id, string json) =>
        Client(f, tier).PatchAsync($"/api/v1/servers/{id}/config",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> Command(AuthTestFactory f, AuthTier? tier, string id, string json) =>
        Client(f, tier).PostAsync($"/api/v1/servers/{id}/commands",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> PostJson(AuthTestFactory f, AuthTier? tier, string path, string json) =>
        Client(f, tier).PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary><see cref="AuthTestFactory"/> with a fake <see cref="IInstanceService"/> registered so the
    /// Tier-1 ops gates exercise their real branches (roster lookup, config read/write, backup list, the
    /// running-status update gate) without a live kgsm.</summary>
    public sealed class EngineTestFactory : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new FakeOpsInstanceService());
            });
        }
    }

    /// <summary>
    /// Stateful fake (config writes must round-trip for the PATCH happy-path assertion). Roster: factorio-1
    /// (stopped) + valheim-1 (running, so the update gate has a 409 case). config-set echoes into a per-key
    /// store that GetInstanceInfo reflects; it refuses a protected key (defensive — the controller pre-check
    /// catches them first) and the sentinel value "BOOM" (an engine refusal that passes the key check).
    /// </summary>
    private sealed class FakeOpsInstanceService : IInstanceService
    {
        // factorio-1's mutable editable config (seeded with non-defaults so the GET assertions are meaningful).
        private readonly Dictionary<string, string> _config = new()
        {
            ["auto_update"] = "true",
            ["executable_arguments"] = "--start saves/w.zip",
            ["level_name"] = "default",
            ["save_command"] = "/save",
            ["stop_command_timeout_seconds"] = "30",
        };

        public Dictionary<string, Instance> GetAll() => new()
        {
            [Server] = new Instance { Name = Server, BlueprintFile = "factorio.bp.yaml" },
            [Running] = new Instance { Name = Running, BlueprintFile = "valheim.bp.yaml" },
        };

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new()
        {
            [Server] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus { InstanceName = Server, Status = false }),
            [Running] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus { InstanceName = Running, Status = true }),
        };

        public Instance? GetInstanceInfo(string instanceName)
        {
            if (!string.Equals(instanceName, Server, StringComparison.Ordinal)
                && !string.Equals(instanceName, Running, StringComparison.Ordinal))
                return null;

            // Reflect the mutable config so the PATCH happy-path's fresh-config assertion is real.
            return new Instance
            {
                Name = instanceName,
                BlueprintFile = "factorio.bp.yaml",
                AutoUpdate = _config["auto_update"] == "true",
                ExecutableArguments = _config["executable_arguments"],
                LevelName = _config["level_name"],
                SaveCommand = _config["save_command"],
                StopCommandTimeoutSeconds = int.Parse(_config["stop_command_timeout_seconds"]),
            };
        }

        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null)
        {
            // Defensive engine-side refusal of a protected key (the controller pre-check normally catches it).
            if (key is "name" or "install_dir" or "ports" or "enable_firewall_management")
                return new KgsmResult(22, "", $"'{key}' is a protected key and cannot be set with config-set");
            // The sentinel value the mid-apply-refusal test relies on.
            if (value == "BOOM")
                return new KgsmResult(1, "", "the engine rejected this value");
            _config[key] = value;
            return new KgsmResult(0);
        }

        public KgsmResult GetBackups(string instanceName) =>
            new(0, "factorio-1-2026-06-21.bak\nfactorio-1-2026-06-20.bak\n");

        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => new(0);

        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => new(0);

        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => new(0);

        // --- unused by these gates: honest NotImplemented (never silently fabricate) ---
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null) => throw new NotImplementedException();
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
        public KgsmResult SendInput(string instanceName, string command) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
