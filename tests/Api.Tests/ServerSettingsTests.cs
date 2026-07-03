using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Phase 0 coverage for the settings spine — <c>GET /servers/{id}/settings</c> (viewer read) and
/// <c>PATCH /servers/{id}/settings</c> (operator write), proven through the real pipeline with the engine
/// seam faked (a switch-on-input <see cref="FakeInstanceService"/>). The load-bearing contracts: the read is
/// viewer-gated + honestly 404/503; the sparse PATCH is operator-gated, rejects an empty/no-recognized-field
/// body <c>400</c>, and on a real field applies via <c>SetInstanceConfigValue</c> → <c>200</c> with the
/// applied field list. Echo-path audit (the write stamps actor+origin; the fake emits no event) means no
/// direct row here — same no-double-write discipline as config.
/// </summary>
public sealed class ServerSettingsTests
    : IClassFixture<ServerSettingsTests.EngineTestFactory>, IClassFixture<AuthTestFactory>
{
    private readonly EngineTestFactory _engine;   // a fake engine → the read/write happy + sad branches
    private readonly AuthTestFactory _noEngine;   // engine unprovisioned → the 503 degrade

    public ServerSettingsTests(EngineTestFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
    }

    // --- GET /servers/{id}/settings ----------------------------------------------------------------

    [Fact]
    public async Task Get_NoToken_401()
    {
        HttpResponseMessage resp = await Get(_engine, tier: null, "factorio-1");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_NoneTier_403()
    {
        // Authenticated but below viewer → 403 (the 401/403 split: identity present, tier too low).
        HttpResponseMessage resp = await Get(_engine, AuthTier.None, "factorio-1");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Get_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Get(_noEngine, AuthTier.Viewer, "factorio-1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_UnknownServer_404()
    {
        HttpResponseMessage resp = await Get(_engine, AuthTier.Viewer, "does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_KnownServer_200_AutoUpdateFalse()
    {
        HttpResponseMessage resp = await Get(_engine, AuthTier.Viewer, "factorio-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("factorio-1", doc.RootElement.GetProperty("serverId").GetString());
        Assert.False(doc.RootElement.GetProperty("autoUpdate").GetBoolean());
        // No watchdog on the EngineTestFactory → autostart is an honest null (never fabricated false).
        Assert.True(doc.RootElement.TryGetProperty("autostart", out var autostartEl));
        Assert.Equal(JsonValueKind.Null, autostartEl.ValueKind);
        // No cpu_priority / memory_cap_mb in the fake instance → both honest null (never guessed).
        Assert.True(doc.RootElement.TryGetProperty("cpuPriority", out var cpuEl));
        Assert.Equal(JsonValueKind.Null, cpuEl.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("memoryCapMb", out var memEl));
        Assert.Equal(JsonValueKind.Null, memEl.ValueKind);
        // Phase 3 — no schedule config on the fake instance + no scheduler leaf → all honest null.
        foreach (string field in new[] { "scheduledRestart", "restartTime", "restartDay", "timezone", "nextFireUtc" })
        {
            Assert.True(doc.RootElement.TryGetProperty(field, out var el), $"missing field: {field}");
            Assert.Equal(JsonValueKind.Null, el.ValueKind);
        }
    }

    [Fact]
    public async Task Get_returns_AutoBackupOnRestart_from_instance_config()
    {
        HttpResponseMessage resp = await Get(_engine, AuthTier.Viewer, "factorio-backup");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("autoBackupOnRestart").GetBoolean());
    }

    [Fact]
    public async Task Get_returns_BackupRetention_from_instance_config()
    {
        HttpResponseMessage resp = await Get(_engine, AuthTier.Viewer, "factorio-backup");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(10, doc.RootElement.GetProperty("backupRetention").GetInt32());
        // No scheduler leaf on the EngineTestFactory → last-backup status is honest null (never fabricated).
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("lastBackupUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("lastBackupOk").ValueKind);
    }

    // --- PATCH /servers/{id}/settings --------------------------------------------------------------

    [Fact]
    public async Task Patch_NoToken_401()
    {
        HttpResponseMessage resp = await Patch(_engine, tier: null, "factorio-1", "{\"autoUpdate\":true}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_Viewer_403()
    {
        // Operator-gated: a viewer can read settings but cannot write them.
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Viewer, "factorio-1", "{\"autoUpdate\":true}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Patch(_noEngine, AuthTier.Operator, "factorio-1", "{\"autoUpdate\":true}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_UnknownServer_404()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "does-not-exist", "{\"autoUpdate\":true}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_BadOrigin_400()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1",
            "{\"autoUpdate\":true,\"origin\":\"hacker\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_EmptyBody_400()
    {
        // A literal null body binds to a null patch → the "a settings body is required" 400.
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1", "null");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_NoFields_400()
    {
        // A body with no recognized settings field (only origin) → 400, nothing applied.
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1", "{\"origin\":\"ui\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_AutoUpdate_Operator_200_AppliesField()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1", "{\"autoUpdate\":true}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement applied = doc.RootElement.GetProperty("applied");
        Assert.Equal("autoUpdate", Assert.Single(applied.EnumerateArray()).GetString());
        Assert.Equal("factorio-1", doc.RootElement.GetProperty("settings").GetProperty("serverId").GetString());
    }

    // --- Phase 4 — auto-backup (autoBackupOnRestart / backupRetention) ------------------------------

    [Fact]
    public async Task Patch_AutoBackupOnRestart_writes_config_key()
    {
        // A concurrently-set cadence satisfies the "auto-backup needs a schedule" guard → the write applies.
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1",
            "{\"autoBackupOnRestart\":true,\"scheduledRestart\":\"daily\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var applied = doc.RootElement.GetProperty("applied").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("autoBackupOnRestart", applied);
    }

    [Fact]
    public async Task Patch_BackupRetention_invalid_low_returns_400()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1", "{\"backupRetention\":0}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_BackupRetention_invalid_high_returns_400()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1", "{\"backupRetention\":101}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_AutoBackupOnRestart_true_without_cadence_returns_400()
    {
        // factorio-1 has no scheduled_restart and the patch supplies none → enabling auto-backup is rejected.
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1", "{\"autoBackupOnRestart\":true}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- Phase 6 — crash-restart policy (crashRestart / crashMaxRestarts) --------------------------

    [Fact]
    public async Task Get_Returns_CrashRestart()
    {
        HttpResponseMessage resp = await Get(_engine, AuthTier.Viewer, "factorio-backup");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("crashRestart").GetBoolean());
    }

    [Fact]
    public async Task Get_Returns_CrashMaxRestarts()
    {
        HttpResponseMessage resp = await Get(_engine, AuthTier.Viewer, "factorio-backup");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(5, doc.RootElement.GetProperty("crashMaxRestarts").GetInt32());
    }

    [Fact]
    public async Task Patch_Writes_CrashRestart()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1", "{\"crashRestart\":true}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var applied = doc.RootElement.GetProperty("applied").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("crashRestart", applied);
    }

    [Fact]
    public async Task Patch_CrashMaxRestarts_TooLow_Returns400()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1", "{\"crashMaxRestarts\":0}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_CrashMaxRestarts_TooHigh_Returns400()
    {
        HttpResponseMessage resp = await Patch(_engine, AuthTier.Operator, "factorio-1", "{\"crashMaxRestarts\":11}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static HttpClient Client(AuthTestFactory factory, AuthTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Get(AuthTestFactory factory, AuthTier? tier, string id) =>
        Client(factory, tier).GetAsync($"/api/v1/servers/{id}/settings");

    private static Task<HttpResponseMessage> Patch(AuthTestFactory factory, AuthTier? tier, string id, string json) =>
        Client(factory, tier).PatchAsync($"/api/v1/servers/{id}/settings",
            new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>
    /// <see cref="AuthTestFactory"/> with a fake <see cref="IInstanceService"/> registered, so the settings
    /// read/write path exercises its real branches (roster lookup, info read, config-set) without a live kgsm.
    /// </summary>
    public class EngineTestFactory : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new FakeInstanceService());
                // Remove the always-registered watchdog client so tests that want "no watchdog"
                // get a clean null from GetService — the WatchdogTestFactory re-adds a fake.
                services.RemoveAll<IWatchdogClient>();
            });
        }
    }

    /// <summary>
    /// Switch-on-input fake (the project convention): the roster carries one instance ("factorio-1",
    /// AutoUpdate=false) so the gate has something to admit; info returns it by id; config-set accepts only
    /// "auto_update". No mutable per-call state.
    /// </summary>
    private sealed class FakeInstanceService : IInstanceService
    {
        private static Instance Factorio1() =>
            new() { Name = "factorio-1", BlueprintFile = "factorio.bp.yaml", AutoUpdate = false };

        // Phase 4 — a second instance carrying auto-backup config, so the GET path has something to surface.
        private static Instance FactorioBackup() =>
            new()
            {
                Name = "factorio-backup",
                BlueprintFile = "factorio.bp.yaml",
                AutoUpdate = false,
                ScheduledRestart = "daily",
                AutoBackupOnRestart = true,
                BackupRetention = 10,
                CrashRestart = true,
                CrashMaxRestarts = 5,
            };

        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();
        public Dictionary<string, Instance> GetAll() =>
            new() { ["factorio-1"] = Factorio1(), ["factorio-backup"] = FactorioBackup() };

        public Instance? GetInstanceInfo(string instanceName) => instanceName switch
        {
            "factorio-1" => Factorio1(),
            "factorio-backup" => FactorioBackup(),
            _ => null,
        };

        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value,
            string? actor = null, string? origin = null) =>
            key is "auto_update" or "cpu_priority" or "memory_cap_mb"
                or "scheduled_restart" or "restart_time" or "restart_day" or "timezone"
                or "auto_backup_on_restart" or "backup_retention"
                or "crash_restart" or "crash_max_restarts"
                ? new KgsmResult(0)
                : new KgsmResult(1, "", $"the engine refused '{key}'");

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new();

        // --- unused by the Phase 0 settings path: honest NotImplemented (never silently fabricate) ---
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null) => throw new NotImplementedException();
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
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    /// <summary>
    /// <see cref="EngineTestFactory"/> plus a fake <see cref="IWatchdogClient"/>, so the Phase 1 autostart
    /// path (read + enable/disable fan-out) exercises its real branches without a live watchdog daemon.
    /// </summary>
    public sealed class WatchdogTestFactory : EngineTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // A non-blank socket makes LeafRegistry seed watchdog=provisioned — the request-time gate the
            // controller checks (the client is always registered; provisioning is the flag). The real client
            // would dial this path, but the fake below replaces it, so the path is never opened.
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KGSM_API_WATCHDOG_SOCKET"] = "/tmp/kgsm-api-tests-watchdog.sock",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IWatchdogClient>();
                services.AddSingleton<IWatchdogClient>(new FakeWatchdogClient());
            });
        }
    }

    /// <summary>
    /// Switch-on-input fake watchdog (no mutable state, parallel-safe): the boot-autostart set is empty, so a
    /// GET on "factorio-1" reads autostart=false; enable/disable succeed (Ok=true). Only the three settings-path
    /// methods are implemented; everything else is honest NotImplemented (never silently fabricate).
    /// </summary>
    private sealed class FakeWatchdogClient : IWatchdogClient
    {
        public Task<IReadOnlyList<string>> GetEnabledNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<WatchdogActionResult> EnableAsync(string instanceName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WatchdogActionResult { Instance = instanceName, Ok = true, Message = "enabled" });

        public Task<WatchdogActionResult> DisableAsync(string instanceName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WatchdogActionResult { Instance = instanceName, Ok = true, Message = "disabled" });

        public Task<WatchdogActionResult> SetCpuPriorityAsync(string instanceName, string priority, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WatchdogActionResult { Instance = instanceName, Ok = true, Message = $"cpu.weight applied ({priority})" });

        public Task<WatchdogActionResult> RestartAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) =>
            Task.FromResult(new WatchdogActionResult { Instance = instanceName, Ok = true, Message = $"restarted (origin={origin})" });

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        // --- unused by the settings path: honest NotImplemented (never silently fabricate) ---
        public Task<WatchdogReadyState?> GetReadyAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> StartAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> StopAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogInstanceState?> GetStatusAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WatchdogInstanceState>> ListAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<string> FollowConsoleAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetConsoleTailAsync(string instanceName, int lines, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<string, IReadOnlyList<WatchdogPlayer>>?> GetAllPlayersAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public void Dispose() { }
    }
}

/// <summary>
/// Phase 1 coverage for the settings spine's <c>autostart</c> field — the watchdog fan-out on
/// <c>GET</c>/<c>PATCH /servers/{id}/settings</c>. Proven through the real pipeline with both the engine seam
/// (<see cref="ServerSettingsTests.FakeInstanceService"/>) and the watchdog seam
/// (<see cref="ServerSettingsTests.WatchdogTestFactory"/>) faked. Load-bearing contracts: a provisioned
/// watchdog surfaces autostart (false, not null, when the name is not in the boot set) and applies an
/// enable/disable; an absent watchdog degrades the read to null but rejects the write <c>503</c> (never
/// fabricates a false autostart).
/// </summary>
public sealed class ServerSettingsWithWatchdogTests
    : IClassFixture<ServerSettingsTests.WatchdogTestFactory>, IClassFixture<ServerSettingsTests.EngineTestFactory>
{
    private readonly ServerSettingsTests.WatchdogTestFactory _watchdog;  // fake engine + fake watchdog
    private readonly ServerSettingsTests.EngineTestFactory _noWatchdog;  // fake engine, no watchdog

    public ServerSettingsWithWatchdogTests(
        ServerSettingsTests.WatchdogTestFactory watchdog, ServerSettingsTests.EngineTestFactory noWatchdog)
    {
        _watchdog = watchdog;
        _noWatchdog = noWatchdog;
    }

    [Fact]
    public async Task Get_KnownServer_WithWatchdog_200_AutostartFalse()
    {
        HttpResponseMessage resp = await Get(_watchdog, AuthTier.Viewer, "factorio-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // The boot-autostart set is empty → "factorio-1" is not enabled → false (a real bool, not null).
        Assert.False(doc.RootElement.GetProperty("autostart").GetBoolean());
    }

    [Fact]
    public async Task Patch_Autostart_Enable_Operator_200_AppliesField()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1", "{\"autostart\":true}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement applied = doc.RootElement.GetProperty("applied");
        Assert.Equal("autostart", Assert.Single(applied.EnumerateArray()).GetString());
        Assert.Equal("factorio-1", doc.RootElement.GetProperty("settings").GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task Patch_Autostart_NoWatchdog_503()
    {
        // No watchdog provisioned → the write cannot proceed; honest 503 rather than a fabricated apply.
        HttpResponseMessage resp = await Patch(_noWatchdog, AuthTier.Operator, "factorio-1", "{\"autostart\":true}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_AutostartAndAutoUpdate_Operator_200_AppliesBothFields()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1",
            "{\"autoUpdate\":true,\"autostart\":true}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var applied = doc.RootElement.GetProperty("applied").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("autoUpdate", applied);
        Assert.Contains("autostart", applied);
    }

    // --- Phase 2 — cpuPriority + memoryCapMb -------------------------------------------------------

    [Fact]
    public async Task Patch_CpuPriority_Operator_200_AppliesField()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1", "{\"cpuPriority\":\"high\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement applied = doc.RootElement.GetProperty("applied");
        Assert.Equal("cpuPriority", Assert.Single(applied.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Patch_MemoryCapMb_Operator_200_AppliesField()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1", "{\"memoryCapMb\":512}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement applied = doc.RootElement.GetProperty("applied");
        Assert.Equal("memoryCapMb", Assert.Single(applied.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Patch_CpuPriority_Invalid_400()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1", "{\"cpuPriority\":\"turbo\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_MemoryCapMb_Negative_400()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1", "{\"memoryCapMb\":-1}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- Phase 3 — schedule (scheduledRestart / restartTime / restartDay / timezone) ---------------

    [Fact]
    public async Task Patch_ScheduledRestart_Operator_200_AppliesField()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1", "{\"scheduledRestart\":\"daily\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement applied = doc.RootElement.GetProperty("applied");
        Assert.Equal("scheduledRestart", Assert.Single(applied.EnumerateArray()).GetString());
        // No scheduler leaf provisioned in the test host → nextFireUtc is honest null (never fabricated).
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("settings").GetProperty("nextFireUtc").ValueKind);
    }

    [Fact]
    public async Task Patch_ScheduledRestart_Invalid_400()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1", "{\"scheduledRestart\":\"hourly\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_RestartTime_Invalid_400()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1", "{\"restartTime\":\"25:00\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_Timezone_Invalid_400()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1", "{\"timezone\":\"Mars/Olympus\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_RestartDay_Invalid_400()
    {
        HttpResponseMessage resp = await Patch(_watchdog, AuthTier.Operator, "factorio-1", "{\"restartDay\":\"funday\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- helpers (mirror ServerSettingsTests) ------------------------------------------------------

    private static HttpClient Client(AuthTestFactory factory, AuthTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Get(AuthTestFactory factory, AuthTier? tier, string id) =>
        Client(factory, tier).GetAsync($"/api/v1/servers/{id}/settings");

    private static Task<HttpResponseMessage> Patch(AuthTestFactory factory, AuthTier? tier, string id, string json) =>
        Client(factory, tier).PatchAsync($"/api/v1/servers/{id}/settings",
            new StringContent(json, Encoding.UTF8, "application/json"));
}
