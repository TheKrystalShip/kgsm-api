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

using TheKrystalShip.KGSM.Auth;

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
        HttpResponseMessage resp = await Get(_engine, KgsmTier.None, "factorio-1");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Get_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Get(_noEngine, KgsmTier.Viewer, "factorio-1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_UnknownServer_404()
    {
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_KnownServer_200_AutoUpdateFalse()
    {
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "factorio-1");
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
        // No maintenance written on the fake instance → an empty list, which is "no maintenance". The
        // timezone it would be read in is honestly null, never this host's own.
        Assert.Empty(doc.RootElement.GetProperty("maintenanceWindows").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("timezone").ValueKind);
    }

    [Fact]
    public async Task Get_returns_every_maintenance_window_in_the_order_it_is_written()
    {
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "factorio-backup");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement[] windows = [.. doc.RootElement.GetProperty("maintenanceWindows").EnumerateArray()];
        Assert.Equal(2, windows.Length);

        Assert.Equal("daily@05:00", windows[0].GetProperty("id").GetString());
        Assert.Equal("appointment", windows[0].GetProperty("kind").GetString());
        Assert.Equal(["backup"], windows[0].GetProperty("tasks").EnumerateArray().Select(e => e.GetString()));
        Assert.True(windows[0].GetProperty("valid").GetBoolean());

        Assert.Equal("weekly.sun@04:00", windows[1].GetProperty("id").GetString());
        Assert.Equal("weekly.sun@04:00/backup,restart", windows[1].GetProperty("expression").GetString());
        Assert.Equal(["backup", "restart"], windows[1].GetProperty("tasks").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Get_leaves_the_next_fire_null_with_no_scheduler_leaf()
    {
        // The arithmetic is the leaf's. With none provisioned there is nothing to relay, and computing it
        // here would be a second opinion that drifts from the daemon's across a DST boundary.
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "factorio-backup");
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        foreach (JsonElement window in doc.RootElement.GetProperty("maintenanceWindows").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, window.GetProperty("nextFireUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, window.GetProperty("lastRun").ValueKind);
        }
    }

    [Fact]
    public async Task Get_reports_an_unreadable_window_as_invalid_beside_the_ones_that_read()
    {
        // Validity is per window: one that cannot be read disables itself and leaves the rest standing.
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "factorio-broken");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement[] windows = [.. doc.RootElement.GetProperty("maintenanceWindows").EnumerateArray()];
        Assert.Equal(2, windows.Length);

        Assert.True(windows[0].GetProperty("valid").GetBoolean());
        Assert.False(windows[1].GetProperty("valid").GetBoolean());
        // The error names the offending text, which is the whole reason it is worth carrying verbatim.
        Assert.Contains("funday", windows[1].GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_returns_Timezone_and_BackupRetention_from_instance_config()
    {
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "factorio-backup");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Europe/Madrid", doc.RootElement.GetProperty("timezone").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("backupRetention").GetInt32());
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
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Viewer, "factorio-1", "{\"autoUpdate\":true}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Patch(_noEngine, KgsmTier.Operator, "factorio-1", "{\"autoUpdate\":true}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_UnknownServer_404()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "does-not-exist", "{\"autoUpdate\":true}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_BadOrigin_400()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1",
            "{\"autoUpdate\":true,\"origin\":\"hacker\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_EmptyBody_400()
    {
        // A literal null body binds to a null patch → the "a settings body is required" 400.
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1", "null");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_NoFields_400()
    {
        // A body with no recognized settings field (only origin) → 400, nothing applied.
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1", "{\"origin\":\"ui\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_AutoUpdate_Operator_200_AppliesField()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1", "{\"autoUpdate\":true}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement applied = doc.RootElement.GetProperty("applied");
        Assert.Equal("autoUpdate", Assert.Single(applied.EnumerateArray()).GetString());
        Assert.Equal("factorio-1", doc.RootElement.GetProperty("settings").GetProperty("serverId").GetString());
    }

    // --- Maintenance windows (the whole-list replace) ---------------------------------------------

    [Fact]
    public async Task Patch_MaintenanceWindows_applies_the_one_packed_key()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1",
            "{\"maintenanceWindows\":[\"daily@05:00/backup\",\"weekly.sun@04:00/backup,restart\"]}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        string? only = Assert.Single(doc.RootElement.GetProperty("applied").EnumerateArray()).GetString();
        Assert.Equal("maintenanceWindows", only);
    }

    [Fact]
    public async Task Patch_MaintenanceWindows_empty_list_is_no_maintenance()
    {
        // The only way to express deleting a window, which a sparse field-by-field patch cannot.
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-backup",
            "{\"maintenanceWindows\":[]}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Contains("maintenanceWindows",
            doc.RootElement.GetProperty("applied").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Patch_MaintenanceWindows_unreadable_expression_400s_naming_the_offending_text()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1",
            "{\"maintenanceWindows\":[\"weekly.funday@04:00/restart\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"bad_request\"", body);
        Assert.Contains("funday", body);
    }

    [Fact]
    public async Task Patch_MaintenanceWindows_unknown_task_400s()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1",
            "{\"maintenanceWindows\":[\"daily@05:00/defrag\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("defrag", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_MaintenanceWindows_interval_below_the_floor_400s()
    {
        // The poll resolution is a minute, so anything under ten is below the useful range above it.
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1",
            "{\"maintenanceWindows\":[\"5m/restart\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_MaintenanceWindows_interval_above_the_ceiling_400s()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1",
            "{\"maintenanceWindows\":[\"60d/backup\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_MaintenanceWindows_two_windows_on_one_schedule_400s()
    {
        // The id IS the schedule, so a second window on it is the first written twice — the answer is to
        // merge their task sets rather than to pick one.
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1",
            "{\"maintenanceWindows\":[\"daily@05:00/backup\",\"daily@5:00/restart\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("merge", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_MaintenanceWindows_refuses_a_restart_on_a_container()
    {
        // Every disruptive task is issued through the watchdog, and the watchdog supervises native
        // instances alone — so this can only ever record a skipped task, every week, silently.
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "terraria-box",
            "{\"maintenanceWindows\":[\"weekly.sun@04:00/backup,restart\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("restart", body);
        Assert.Contains("native", body);
    }

    [Fact]
    public async Task Patch_MaintenanceWindows_allows_a_backup_on_a_container()
    {
        // The archive beside the refused restart is the half a container CAN have, and it still fires.
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "terraria-box",
            "{\"maintenanceWindows\":[\"daily@05:00/backup\"]}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_MaintenanceWindows_one_bad_expression_applies_nothing()
    {
        // The list is read before any key is written, so a rejected window leaves the instance exactly as
        // it was rather than half-applied beside a field that did land.
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1",
            "{\"autoUpdate\":true,\"maintenanceWindows\":[\"never@nowhere/restart\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.DoesNotContain("applied", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_BackupRetention_invalid_low_returns_400()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1", "{\"backupRetention\":0}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_BackupRetention_invalid_high_returns_400()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1", "{\"backupRetention\":101}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- Phase 6 — crash-restart policy (crashRestart / crashMaxRestarts) --------------------------

    [Fact]
    public async Task Get_Returns_CrashRestart()
    {
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "factorio-backup");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("crashRestart").GetBoolean());
    }

    [Fact]
    public async Task Get_Returns_CrashMaxRestarts()
    {
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "factorio-backup");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(5, doc.RootElement.GetProperty("crashMaxRestarts").GetInt32());
    }

    [Fact]
    public async Task Patch_Writes_CrashRestart()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1", "{\"crashRestart\":true}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var applied = doc.RootElement.GetProperty("applied").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("crashRestart", applied);
    }

    [Fact]
    public async Task Patch_CrashMaxRestarts_TooLow_Returns400()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1", "{\"crashMaxRestarts\":0}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_CrashMaxRestarts_TooHigh_Returns400()
    {
        HttpResponseMessage resp = await Patch(_engine, KgsmTier.Operator, "factorio-1", "{\"crashMaxRestarts\":11}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static HttpClient Client(AuthTestFactory factory, KgsmTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Get(AuthTestFactory factory, KgsmTier? tier, string id) =>
        Client(factory, tier).GetAsync($"/api/v1/servers/{id}/settings");

    private static Task<HttpResponseMessage> Patch(AuthTestFactory factory, KgsmTier? tier, string id, string json) =>
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
    /// Switch-on-input fake (the project convention), with no mutable per-call state. The roster carries
    /// four instances so every branch of the settings path has something to read: a bare native one, one
    /// carrying a two-window maintenance list, one whose window cannot be read, and a container (whose
    /// disruptive tasks the watchdog cannot perform).
    /// </summary>
    internal sealed class FakeInstanceService : IInstanceService
    {
        private static Instance Factorio1() =>
            new() { Name = "factorio-1", BlueprintFile = "factorio.bp.yaml", AutoUpdate = false,
                    Runtime = InstanceRuntime.Native };

        private static Instance FactorioBackup() =>
            new()
            {
                Name = "factorio-backup",
                BlueprintFile = "factorio.bp.yaml",
                AutoUpdate = false,
                Runtime = InstanceRuntime.Native,
                MaintenanceWindows = "daily@05:00/backup;weekly.sun@04:00/backup,restart",
                Timezone = "Europe/Madrid",
                BackupRetention = 10,
                CrashRestart = true,
                CrashMaxRestarts = 5,
            };

        private static Instance FactorioBroken() =>
            new()
            {
                Name = "factorio-broken",
                BlueprintFile = "factorio.bp.yaml",
                AutoUpdate = false,
                Runtime = InstanceRuntime.Native,
                MaintenanceWindows = "daily@05:00/backup;weekly.funday@04:00/restart",
            };

        private static Instance TerrariaContainer() =>
            new() { Name = "terraria-box", BlueprintFile = "terraria.bp.yaml", AutoUpdate = false,
                    Runtime = InstanceRuntime.Container };

        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();
        public Dictionary<string, Instance> GetAll() =>
            new()
            {
                ["factorio-1"] = Factorio1(),
                ["factorio-backup"] = FactorioBackup(),
                ["factorio-broken"] = FactorioBroken(),
                ["terraria-box"] = TerrariaContainer(),
            };

        public Instance? GetInstanceInfo(string instanceName) => instanceName switch
        {
            "factorio-1" => Factorio1(),
            "factorio-backup" => FactorioBackup(),
            "factorio-broken" => FactorioBroken(),
            "terraria-box" => TerrariaContainer(),
            _ => null,
        };

        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value,
            string? actor = null, string? origin = null) =>
            key is "auto_update" or "cpu_priority" or "memory_cap_mb"
                or "maintenance_windows" or "timezone" or "backup_retention"
                or "crash_restart" or "crash_max_restarts"
                ? new KgsmResult(0)
                : new KgsmResult(1, "", $"the engine refused '{key}'");

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new();

        // --- unused by the Phase 0 settings path: honest NotImplemented (never silently fabricate) ---
        public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? library = null, string? version = null, string? displayName = null, string? actor = null, string? origin = null, int? port = null, bool? start = null, string? id = null) => throw new NotImplementedException();
        public KgsmResult SetDisplayName(string instanceId, string displayName, string? actor = null, string? origin = null) => throw new NotImplementedException();
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
                    ["Api:WatchdogSocketPath"] = "/tmp/kgsm-api-tests-watchdog.sock",
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
        public Task<WatchdogActionResult> StartAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> StopAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> BeginMaintenanceAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> EndMaintenanceAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> ForgetAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogInstanceState?> GetStatusAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WatchdogInstanceState>> ListAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WatchdogRunTimes>> GetRunTimesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchdogRunTimes>>(Array.Empty<WatchdogRunTimes>());
        public IAsyncEnumerable<string> FollowConsoleAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        // The console-run pair — stubbed. This fake is not about consoles; a caller reaching here would
        // be testing something it does not stand in for.
        public Task<IReadOnlyList<WatchdogConsoleRun>> GetConsoleRunsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetConsoleRunTailAsync(string instanceName, int run, int lines, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogConsoleWindow> GetConsoleWindowAsync(string instanceName, int lines, int run, long endOffset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogConsoleDownload?> OpenConsoleDownloadAsync(string instanceName, int run, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetConsoleTailAsync(string instanceName, int lines, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<string, WatchdogInstancePresence>?> GetPlayerPresenceAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogUpnpList?> GetUpnpAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();

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
        HttpResponseMessage resp = await Get(_watchdog, KgsmTier.Viewer, "factorio-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // The boot-autostart set is empty → "factorio-1" is not enabled → false (a real bool, not null).
        Assert.False(doc.RootElement.GetProperty("autostart").GetBoolean());
    }

    [Fact]
    public async Task Patch_Autostart_Enable_Operator_200_AppliesField()
    {
        HttpResponseMessage resp = await Patch(_watchdog, KgsmTier.Operator, "factorio-1", "{\"autostart\":true}");
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
        HttpResponseMessage resp = await Patch(_noWatchdog, KgsmTier.Operator, "factorio-1", "{\"autostart\":true}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_AutostartAndAutoUpdate_Operator_200_AppliesBothFields()
    {
        HttpResponseMessage resp = await Patch(_watchdog, KgsmTier.Operator, "factorio-1",
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
        HttpResponseMessage resp = await Patch(_watchdog, KgsmTier.Operator, "factorio-1", "{\"cpuPriority\":\"high\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement applied = doc.RootElement.GetProperty("applied");
        Assert.Equal("cpuPriority", Assert.Single(applied.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Patch_MemoryCapMb_Operator_200_AppliesField()
    {
        HttpResponseMessage resp = await Patch(_watchdog, KgsmTier.Operator, "factorio-1", "{\"memoryCapMb\":512}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement applied = doc.RootElement.GetProperty("applied");
        Assert.Equal("memoryCapMb", Assert.Single(applied.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Patch_CpuPriority_Invalid_400()
    {
        HttpResponseMessage resp = await Patch(_watchdog, KgsmTier.Operator, "factorio-1", "{\"cpuPriority\":\"turbo\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_MemoryCapMb_Negative_400()
    {
        HttpResponseMessage resp = await Patch(_watchdog, KgsmTier.Operator, "factorio-1", "{\"memoryCapMb\":-1}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- Maintenance windows + the timezone they are read in --------------------------------------

    [Fact]
    public async Task Patch_MaintenanceWindows_Operator_200_AppliesField()
    {
        HttpResponseMessage resp = await Patch(_watchdog, KgsmTier.Operator, "factorio-1",
            "{\"maintenanceWindows\":[\"weekly.sun@04:00/restart\"]}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement applied = doc.RootElement.GetProperty("applied");
        Assert.Equal("maintenanceWindows", Assert.Single(applied.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Patch_Timezone_Invalid_400()
    {
        // The clock resolves an unrecognized zone to this host's local one, so an unchecked typo would
        // silently move every appointment on the instance to a zone nobody chose.
        HttpResponseMessage resp = await Patch(_watchdog, KgsmTier.Operator, "factorio-1", "{\"timezone\":\"Mars/Olympus\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- The preview: what a window would do, before anybody saves it -----------------------------

    [Fact]
    public async Task Preview_Viewer_403()
    {
        HttpResponseMessage resp = await Preview(_watchdog, KgsmTier.Viewer, "factorio-1",
            "{\"expression\":\"daily@05:00/backup\"}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Preview_NoExpression_400()
    {
        // A malformed request, as distinct from a badly written window — which is an answer, below.
        HttpResponseMessage resp = await Preview(_watchdog, KgsmTier.Operator, "factorio-1", "{}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Preview_returns_ascending_fires_a_day_apart()
    {
        HttpResponseMessage resp = await Preview(_watchdog, KgsmTier.Operator, "factorio-1",
            "{\"expression\":\"daily@05:00/backup\",\"count\":3,\"timezone\":\"UTC\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal("daily@05:00", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("appointment", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("UTC", doc.RootElement.GetProperty("timezone").GetString());

        DateTimeOffset[] fires =
            [.. doc.RootElement.GetProperty("fires").EnumerateArray().Select(e => e.GetDateTimeOffset())];
        Assert.Equal(3, fires.Length);
        Assert.All(fires, f => Assert.Equal(new TimeSpan(5, 0, 0), f.UtcDateTime.TimeOfDay));
        Assert.Equal(TimeSpan.FromDays(1), fires[1] - fires[0]);
        Assert.Equal(TimeSpan.FromDays(1), fires[2] - fires[1]);
    }

    [Fact]
    public async Task Preview_an_interval_lands_on_whole_boundaries_from_the_epoch()
    {
        // An interval carries no time of day and no timezone by construction, so every host answers
        // identically and nothing has to be anchored at install time.
        HttpResponseMessage resp = await Preview(_watchdog, KgsmTier.Operator, "factorio-1",
            "{\"expression\":\"6h/backup\",\"count\":2}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("interval", doc.RootElement.GetProperty("kind").GetString());

        foreach (JsonElement fire in doc.RootElement.GetProperty("fires").EnumerateArray())
        {
            DateTimeOffset f = fire.GetDateTimeOffset();
            Assert.Equal(0, f.UtcDateTime.Hour % 6);
            Assert.Equal(0, f.UtcDateTime.Minute);
        }
    }

    [Fact]
    public async Task Preview_reads_an_unreadable_expression_as_an_answer()
    {
        // The endpoint's question is "what does this mean", and "it cannot be read" answers it — which is
        // exactly what an editor renders where the next fire would have gone.
        HttpResponseMessage resp = await Preview(_watchdog, KgsmTier.Operator, "factorio-1",
            "{\"expression\":\"weekly.funday@04:00/restart\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("valid").GetBoolean());
        Assert.Contains("funday", doc.RootElement.GetProperty("error").GetString());
        Assert.Empty(doc.RootElement.GetProperty("fires").EnumerateArray());
    }

    [Fact]
    public async Task Preview_BadTimezone_400()
    {
        HttpResponseMessage resp = await Preview(_watchdog, KgsmTier.Operator, "factorio-1",
            "{\"expression\":\"daily@05:00/backup\",\"timezone\":\"Mars/Olympus\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Preview_clamps_an_outsized_count()
    {
        HttpResponseMessage resp = await Preview(_watchdog, KgsmTier.Operator, "factorio-1",
            "{\"expression\":\"daily@05:00/backup\",\"count\":500}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(20, doc.RootElement.GetProperty("fires").GetArrayLength());
    }

    [Fact]
    public async Task Preview_writes_nothing()
    {
        // Pure: it is the editor's companion, and an editor that saved on every keystroke would be a
        // different feature. The instance's windows are untouched afterwards.
        await Preview(_watchdog, KgsmTier.Operator, "factorio-backup",
            "{\"expression\":\"30d/backup\"}");

        HttpResponseMessage after = await Get(_watchdog, KgsmTier.Viewer, "factorio-backup");
        using JsonDocument doc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("maintenanceWindows").GetArrayLength());
    }

    // --- helpers (mirror ServerSettingsTests) ------------------------------------------------------

    private static HttpClient Client(AuthTestFactory factory, KgsmTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Get(AuthTestFactory factory, KgsmTier? tier, string id) =>
        Client(factory, tier).GetAsync($"/api/v1/servers/{id}/settings");

    private static Task<HttpResponseMessage> Patch(AuthTestFactory factory, KgsmTier? tier, string id, string json) =>
        Client(factory, tier).PatchAsync($"/api/v1/servers/{id}/settings",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> Preview(AuthTestFactory factory, KgsmTier? tier, string id, string json) =>
        Client(factory, tier).PostAsync($"/api/v1/servers/{id}/settings/maintenance/preview",
            new StringContent(json, Encoding.UTF8, "application/json"));
}
