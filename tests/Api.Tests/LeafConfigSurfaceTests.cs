using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The descriptor-driven config surface end to end: a leaf's own shipped descriptor replacing this API's
/// built-in fallback, effective-value provenance (override → floor → default, with honest unknown), the
/// editability gate, descriptor-declared bounds, and the <c>applied_unreachable</c> outcome a wiring change
/// produces when the liveness canary is green but this API can no longer reach the leaf.
/// </summary>
public sealed class LeafConfigSurfaceTests
{
    private const string Host = AuthTestFactory.HostId;
    private const string MonitorUnit = "kgsm-monitor.service";

    /// <summary>A monitor descriptor whose floor is one source the test controls.</summary>
    private static string MonitorDescriptor(string floorPath, string kind = "env-file") => $$"""
    {
      "schemaVersion": 1,
      "id": "monitor",
      "displayName": "Monitor",
      "unit": "kgsm-monitor.service",
      "role": "Host and per-server resource metrics.",
      "onDemand": false,
      "applyMode": "restart",
      "floorSources": [{ "kind": "{{kind}}", "path": "{{floorPath}}" }],
      "groups": [
        { "id": "sampling", "label": "Sampling", "order": 1 },
        { "id": "sockets", "label": "Sockets", "order": 2 }
      ],
      "fields": [
        { "key": "intervalMs", "env": "KGSM_MONITOR_INTERVAL_MS", "label": "Sample interval",
          "description": "How often the monitor samples.", "group": "sampling", "type": "int",
          "default": "1000", "min": 100, "unit": "ms", "risk": "safe" },
        { "key": "maintenanceMs", "env": "KGSM_MONITOR_MAINT_MS", "label": "Maintenance interval",
          "description": "Rollup cadence.", "group": "sampling", "type": "int", "default": "60000",
          "min": 1000, "unit": "ms", "risk": "safe" },
        { "key": "hostId", "env": "KGSM_MONITOR_HOST_ID", "label": "Host id",
          "description": "Identity metrics are stored under.", "group": "sampling", "type": "string",
          "risk": "wiring", "pairedApiKey": "Api__HostId" },
        { "key": "socketPath", "env": "KGSM_MONITOR_SOCKET", "label": "Metrics socket",
          "description": "Where consumers scrape.", "group": "sockets", "type": "path",
          "default": "/run/kgsm-monitor/metrics.sock", "risk": "wiring" }
      ]
    }
    """;

    // ── The descriptor replaces the built-in fallback ─────────────────────────

    [Fact]
    public async Task A_shipped_descriptor_supersedes_the_built_in_manifest()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "")));

        JsonElement cfg = await Json(Admin(f).GetAsync(ConfigUrl("monitor")));

        Assert.True(cfg.GetProperty("fromDescriptor").GetBoolean());
        Assert.Equal("restart", cfg.GetProperty("applyMode").GetString());

        string[] keys = [.. cfg.GetProperty("fields").EnumerateArray().Select(x => x.GetProperty("key").GetString()!)];
        Assert.Contains("socketPath", keys);        // descriptor-only — the built-in manifest has no such key
        Assert.DoesNotContain("logLevel", keys);    // and the manifest's keys no longer leak in

        // Groups ride along, ordered, so the panel can section the page.
        JsonElement[] groups = [.. cfg.GetProperty("groups").EnumerateArray()];
        Assert.Equal(["sampling", "sockets"], groups.Select(g => g.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Without_a_descriptor_the_built_in_fallback_still_serves()
    {
        using var f = new LeafConfigTestFactory();   // no descriptors installed

        JsonElement cfg = await Json(Admin(f).GetAsync(ConfigUrl("monitor")));

        // Nothing regresses while descriptors roll out repo by repo — but the panel is told the surface is
        // the short built-in one, not the leaf's full declaration.
        Assert.False(cfg.GetProperty("fromDescriptor").GetBoolean());
        Assert.Contains("logLevel",
            cfg.GetProperty("fields").EnumerateArray().Select(x => x.GetProperty("key").GetString()));
    }

    // ── Provenance ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Effective_value_resolves_override_then_floor_then_default()
    {
        using var f = new LeafConfigTestFactory();
        // The leaf's own config sets one key; the other two are left to the leaf's coded default.
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "KGSM_MONITOR_MAINT_MS=30000\n")));
        await Put(Admin(f), "monitor", """{"values":{"intervalMs":"2500"}}""");

        JsonElement cfg = await Json(Admin(f).GetAsync(ConfigUrl("monitor")));

        JsonElement interval = Field(cfg, "intervalMs");
        Assert.Equal("override", interval.GetProperty("source").GetString());
        Assert.Equal("2500", interval.GetProperty("effective").GetString());
        Assert.True(interval.GetProperty("overridden").GetBoolean());

        JsonElement maint = Field(cfg, "maintenanceMs");
        Assert.Equal("floor", maint.GetProperty("source").GetString());
        Assert.Equal("30000", maint.GetProperty("effective").GetString());
        Assert.Equal("30000", maint.GetProperty("floor").GetString());
        Assert.False(maint.GetProperty("overridden").GetBoolean());

        JsonElement socket = Field(cfg, "socketPath");
        Assert.Equal("default", socket.GetProperty("source").GetString());
        Assert.Equal("/run/kgsm-monitor/metrics.sock", socket.GetProperty("effective").GetString());
    }

    [Fact]
    public async Task An_unreadable_floor_reports_unknown_not_the_default()
    {
        using var f = new LeafConfigTestFactory();
        // The descriptor names a unit that is not there — so this API cannot know what the leaf runs with.
        // (An absent optional env file is different: that genuinely means "sets nothing", and falls through.)
        f.InstallDescriptor("monitor", MonitorDescriptor(
            Path.Combine(f.OverridesDir, "no-such-unit.service").Replace("\\", "/"), kind: "systemd-unit"));

        JsonElement cfg = await Json(Admin(f).GetAsync(ConfigUrl("monitor")));
        JsonElement socket = Field(cfg, "socketPath");

        Assert.Equal("unknown", socket.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, socket.GetProperty("effective").ValueKind);
        // The declared default is still reported as the descriptor's record of it — it is just not claimed
        // to be what is running.
        Assert.Equal("/run/kgsm-monitor/metrics.sock", socket.GetProperty("default").GetString());
    }

    [Fact]
    public async Task A_secret_never_echoes_its_value_from_any_tier()
    {
        using var f = new LeafConfigTestFactory();
        string floor = FloorFile(f, "WebSearch__ApiKey=tvly-THE-REAL-SECRET-VALUE\n");
        f.InstallDescriptor("assistant", $$"""
        {
          "schemaVersion": 1, "id": "assistant", "displayName": "Assistant",
          "unit": "kgsm-assistant-service.service", "role": "LLM assistant.",
          "onDemand": false, "applyMode": "restart",
          "floorSources": [{ "kind": "env-file", "path": "{{floor}}" }],
          "fields": [
            { "key": "webSearchApiKey", "env": "WebSearch__ApiKey", "label": "Web search key",
              "description": "Write-only.", "type": "secret" }
          ]
        }
        """);

        HttpResponseMessage resp = await Admin(f).GetAsync(ConfigUrl("assistant"));
        string body = await resp.Content.ReadAsStringAsync();
        JsonElement key = Field(JsonDocument.Parse(body).RootElement, "webSearchApiKey");

        // The floor is where a real secret actually lives, so this is the tier that must never leak.
        Assert.DoesNotContain("THE-REAL-SECRET-VALUE", body);
        Assert.Equal(JsonValueKind.Null, key.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, key.GetProperty("effective").ValueKind);
        Assert.Equal(JsonValueKind.Null, key.GetProperty("floor").ValueKind);
        // Knowing a secret is set is not knowing the secret — the provenance tier is still reported.
        Assert.True(key.GetProperty("set").GetBoolean());
        Assert.Equal("floor", key.GetProperty("source").GetString());
    }

    // ── Editability ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_leaf_with_no_override_dropin_is_readable_but_locked()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "")));
        f.UnwireDropIn(MonitorUnit);   // a host that never ran setup-leaf-config.sh

        JsonElement cfg = await Json(Admin(f).GetAsync(ConfigUrl("monitor")));
        Assert.False(cfg.GetProperty("editable").GetBoolean());
        Assert.Contains("setup-leaf-config.sh", cfg.GetProperty("editableReason").GetString());

        // A write would render a file nothing reads and then fail at the restart — refuse it up front, and
        // as a 409: nothing about the request is malformed.
        HttpResponseMessage resp = await Put(Admin(f), "monitor", """{"values":{"intervalMs":"2500"}}""");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal(0, f.Units().RestartCount(MonitorUnit));
    }

    [Fact]
    public async Task A_wired_leaf_is_editable()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "")));

        JsonElement cfg = await Json(Admin(f).GetAsync(ConfigUrl("monitor")));

        Assert.True(cfg.GetProperty("editable").GetBoolean());
        Assert.False(cfg.TryGetProperty("editableReason", out _));
    }

    // ── Descriptor-declared bounds ────────────────────────────────────────────

    [Fact]
    public async Task A_value_below_the_leafs_own_floor_is_rejected_before_any_restart()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "")));

        // The monitor silently discards an interval under 100ms and keeps its default. Accepting it would
        // report a change that never happened — the bound comes from the leaf's own descriptor, not from here.
        HttpResponseMessage resp = await Put(Admin(f), "monitor", """{"values":{"intervalMs":"50"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("at least 100 ms", await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, f.Units().RestartCount(MonitorUnit));
    }

    [Fact]
    public async Task A_value_within_bounds_applies()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "")));

        JsonElement result = await Json(Put(Admin(f), "monitor", """{"values":{"intervalMs":"100"}}"""));

        Assert.Equal("applied", result.GetProperty("outcome").GetString());
        Assert.Equal(1, f.Units().RestartCount(MonitorUnit));
    }

    // ── applied_unreachable ───────────────────────────────────────────────────

    [Fact]
    public async Task A_wiring_change_that_severs_the_api_reports_applied_unreachable()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "")));

        JsonElement result = await Json(Put(Admin(f), "monitor",
            $$$"""{"values":{"socketPath":"{{{FakeLeafReachability.UnreachableValue}}}"}}"""));

        // The unit came back up — the liveness canary is green — so this is NOT a rollback. The change was
        // asked for and is in effect; the API just says so honestly instead of claiming success.
        Assert.Equal("applied_unreachable", result.GetProperty("outcome").GetString());
        Assert.Equal("down", result.GetProperty("health").GetProperty("status").GetString());
        Assert.Equal(1, f.Units().RestartCount(MonitorUnit));
        Assert.Contains("reset", result.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        // Not reverted: the override is still stored, so the panel shows what is actually running.
        JsonElement cfg = result.GetProperty("config");
        Assert.True(Field(cfg, "socketPath").GetProperty("overridden").GetBoolean());
    }

    [Fact]
    public async Task A_safe_change_never_runs_the_reachability_check()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "")));

        // The sentinel would report unreachable if consulted — but intervalMs is a safe field, so it is not.
        JsonElement result = await Json(Put(Admin(f), "monitor", """{"values":{"intervalMs":"2500"}}"""));
        Assert.Equal("applied", result.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task No_reachability_signal_is_never_reported_as_a_break()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "")));

        JsonElement result = await Json(Put(Admin(f), "monitor",
            $$$"""{"values":{"socketPath":"{{{FakeLeafReachability.NoSignalValue}}}"}}"""));

        // Nothing was measured. Reporting a break on an absence of evidence would fabricate a status.
        Assert.Equal("applied", result.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_paired_key_disagreement_is_named_on_both_sides()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "")));

        // hostId is wiring and pairs with Api__HostId. Setting the leaf's to something else means the
        // monitor stores its metrics under one identity and this API queries another.
        JsonElement result = await Json(Put(Admin(f), "monitor", """{"values":{"hostId":"some-other-host"}}"""));

        Assert.Equal("applied_unreachable", result.GetProperty("outcome").GetString());
        string message = result.GetProperty("message").GetString()!;
        Assert.Contains("some-other-host", message);      // what the leaf now says
        Assert.Contains("Api__HostId", message);     // and what this API still reads
    }

    [Fact]
    public async Task A_paired_key_that_agrees_applies_cleanly()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("monitor", MonitorDescriptor(FloorFile(f, "")));

        JsonElement result = await Json(Put(Admin(f), "monitor", $$$"""{"values":{"hostId":"{{{Host}}}"}}"""));

        Assert.Equal("applied", result.GetProperty("outcome").GetString());
    }

    // ── A leaf this API has never heard of ────────────────────────────────────

    [Fact]
    public async Task A_descriptor_only_leaf_is_adopted_onto_the_services_board()
    {
        using var f = new LeafConfigTestFactory();
        f.InstallDescriptor("weatherwatch", """
        {
          "schemaVersion": 1, "id": "weatherwatch", "displayName": "Weather Watch",
          "unit": "kgsm-weatherwatch.service", "role": "A leaf added after this API was built.",
          "onDemand": false, "applyMode": "restart", "floorSources": [],
          "fields": [
            { "key": "logLevel", "env": "Logging__LogLevel__Default", "label": "Log level",
              "description": "Severity.", "type": "enum",
              "values": ["Debug", "Information"], "default": "Information" }
          ]
        }
        """);

        JsonElement board = await Json(Admin(f).GetAsync($"/api/v1/hosts/{Host}/services"));
        JsonElement row = board.GetProperty("data").EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == "weatherwatch");

        Assert.Equal("Weather Watch", row.GetProperty("displayName").GetString());
        Assert.Equal("kgsm-weatherwatch.service", row.GetProperty("unit").GetString());
        // No deep health: this API has no probe for a leaf it does not know, and inventing one would be
        // exactly the fabrication the ecosystem forbids. systemd liveness is universal and still reported.
        Assert.False(row.TryGetProperty("health", out _));
        Assert.False(string.IsNullOrEmpty(row.GetProperty("state").GetString()));

        // And its configuration surface is served with no rebuild of this API.
        JsonElement cfg = await Json(Admin(f).GetAsync(ConfigUrl("weatherwatch")));
        Assert.True(cfg.GetProperty("fromDescriptor").GetBoolean());
        Assert.Equal("Weather Watch", cfg.GetProperty("displayName").GetString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ConfigUrl(string leaf) => $"/api/v1/hosts/{Host}/services/{leaf}/config";

    private static JsonElement Field(JsonElement config, string key) =>
        config.GetProperty("fields").EnumerateArray().First(x => x.GetProperty("key").GetString() == key);

    /// <summary>Write a leaf's own config file (its floor) and return the path the descriptor declares.</summary>
    private static string FloorFile(LeafConfigTestFactory f, string content)
    {
        Directory.CreateDirectory(f.OverridesDir);
        string path = Path.Combine(f.OverridesDir, $"floor-{Guid.NewGuid():N}.env");
        File.WriteAllText(path, content);
        return path.Replace("\\", "/");
    }

    private static HttpClient Admin(LeafConfigTestFactory f)
    {
        HttpClient c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(AuthTier.Admin));
        return c;
    }

    private static Task<HttpResponseMessage> Put(HttpClient c, string leaf, string json) =>
        c.PutAsync(ConfigUrl(leaf), new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Json(Task<HttpResponseMessage> respTask)
    {
        HttpResponseMessage resp = await respTask;
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
