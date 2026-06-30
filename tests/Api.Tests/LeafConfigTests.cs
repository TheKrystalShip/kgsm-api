using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Phase 2 (runtime config) coverage — the GET manifest⋈overrides read view (secrets masked), and the PUT
/// apply broker through its real write→render→restart→canary→rollback flow with the systemd/probe seams
/// faked: applied (1 restart), rolled-back (bad value → 2 restarts, snapshot restored), unchanged (no
/// restart), reset (override deleted), unknown-key/invalid-value 400s, the admin gate, and the redacted audit.
/// Each test uses its own factory (fresh DB + overrides dir) so override state never leaks.
/// </summary>
public sealed class LeafConfigTests
{
    private const string Host = AuthTestFactory.HostId;
    private const string MonitorUnit = "kgsm-monitor.service";
    private const string AssistantUnit = "kgsm-assistant-service.service";

    // ---- GET: the manifest shape ------------------------------------------------------------------
    [Fact]
    public async Task GetConfig_Monitor_ManifestShape()
    {
        using var f = new LeafConfigTestFactory();
        JsonElement cfg = await Json(Admin(f).GetAsync($"/api/v1/hosts/{Host}/services/monitor/config"));

        Assert.Equal("monitor", cfg.GetProperty("leaf").GetString());
        Assert.Equal(MonitorUnit, cfg.GetProperty("unit").GetString());
        JsonElement[] fields = cfg.GetProperty("fields").EnumerateArray().ToArray();

        JsonElement logLevel = fields.First(x => x.GetProperty("key").GetString() == "logLevel");
        Assert.Equal("Logging__LogLevel__Default", logLevel.GetProperty("envName").GetString());
        Assert.Equal("enum", logLevel.GetProperty("type").GetString());
        Assert.Equal(6, logLevel.GetProperty("enum").GetArrayLength());     // Trace..Critical
        Assert.False(logLevel.GetProperty("overridden").GetBoolean());      // nothing stored yet
        Assert.Equal(JsonValueKind.Null, logLevel.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, logLevel.GetProperty("default").ValueKind); // never fabricated

        JsonElement interval = fields.First(x => x.GetProperty("key").GetString() == "intervalMs");
        Assert.Equal("KGSM_MONITOR_INTERVAL_MS", interval.GetProperty("envName").GetString());
        Assert.Equal("int", interval.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetConfig_NonConfigTargetLeaf_404()
    {
        using var f = new LeafConfigTestFactory();
        HttpResponseMessage resp = await Admin(f).GetAsync($"/api/v1/hosts/{Host}/services/bot/config");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetConfig_Operator_403()
    {
        using var f = new LeafConfigTestFactory();
        HttpResponseMessage resp = await Client(f, AuthTier.Operator).GetAsync($"/api/v1/hosts/{Host}/services/monitor/config");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- PUT: applied happy path (override written, restarted once, file rendered, audited) --------
    [Fact]
    public async Task PutConfig_Applied_WritesOverride_RestartsOnce_RendersFile_Audits()
    {
        using var f = new LeafConfigTestFactory();
        HttpClient admin = Admin(f);

        JsonElement result = await Json(Put(admin, "monitor", """{"values":{"logLevel":"Debug","intervalMs":"2000"}}"""));
        Assert.Equal("applied", result.GetProperty("outcome").GetString());
        Assert.Equal("operational", result.GetProperty("health").GetProperty("status").GetString());

        // The refetched config (in the apply response) shows the overrides.
        JsonElement fields = result.GetProperty("config").GetProperty("fields");
        JsonElement ll = fields.EnumerateArray().First(x => x.GetProperty("key").GetString() == "logLevel");
        Assert.True(ll.GetProperty("overridden").GetBoolean());
        Assert.Equal("Debug", ll.GetProperty("value").GetString());

        // Restarted exactly once (apply, no rollback).
        Assert.Equal(1, f.Units().RestartCount(MonitorUnit));

        // The override file is rendered with the real env names.
        string file = Path.Combine(f.OverridesDir, "monitor.env");
        Assert.True(File.Exists(file));
        string text = File.ReadAllText(file);
        Assert.Contains("Logging__LogLevel__Default=Debug", text);
        Assert.Contains("KGSM_MONITOR_INTERVAL_MS=2000", text);

        // A service.config audit row lists the changed keys + outcome — never a value.
        JsonElement audit = await Json(admin.GetAsync("/api/v1/audit"));
        JsonElement row = audit.GetProperty("data").EnumerateArray()
            .First(r => r.GetProperty("action").GetString() == "service.config");
        Assert.Equal("applied", row.GetProperty("meta").GetProperty("outcome").GetString());
        Assert.Contains("logLevel", row.GetProperty("meta").GetProperty("keys").GetString());
    }

    // ---- PUT: secret is write-only (masked on read, never echoed, never in the audit) -------------
    [Fact]
    public async Task PutConfig_Secret_WriteOnly_MaskedOnRead_NotInAudit()
    {
        using var f = new LeafConfigTestFactory();
        HttpClient admin = Admin(f);
        const string secret = "tvly-SUPER-SECRET-KEY-1234";

        await Put(admin, "assistant", "{\"values\":{\"webSearchApiKey\":\"" + secret + "\"}}");

        // GET masks the secret: value null, set true, never the value itself.
        HttpResponseMessage getResp = await admin.GetAsync($"/api/v1/hosts/{Host}/services/assistant/config");
        string getBody = await getResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, getBody); // the secret never crosses back to the client
        JsonElement key = JsonDocument.Parse(getBody).RootElement.GetProperty("fields").EnumerateArray()
            .First(x => x.GetProperty("key").GetString() == "webSearchApiKey");
        Assert.True(key.GetProperty("isSecret").GetBoolean());
        Assert.Equal(JsonValueKind.Null, key.GetProperty("value").ValueKind); // ALWAYS null for a secret
        Assert.True(key.GetProperty("set").GetBoolean());

        // The audit trail records the KEY but never the secret value.
        string auditBody = await (await admin.GetAsync("/api/v1/audit")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, auditBody);
        Assert.Contains("webSearchApiKey", auditBody);
    }

    // ---- PUT: a bad value fails the canary → auto-rollback (restart twice, override restored) ------
    [Fact]
    public async Task PutConfig_BadValue_RollsBack_RestartsTwice_RestoresSnapshot()
    {
        using var f = new LeafConfigTestFactory();
        HttpClient admin = Admin(f);

        // The sentinel value the fake probe treats as unhealthy → the apply must roll back.
        JsonElement result = await Json(Put(admin, "assistant",
            "{\"values\":{\"webSearchApiKey\":\"" + FakeLeafProbe.UnhealthyValue + "\"}}"));

        Assert.Equal("rolled_back", result.GetProperty("outcome").GetString());
        // Restarted twice: the (bad) apply, then the rollback restore.
        Assert.Equal(2, f.Units().RestartCount(AssistantUnit));

        // Snapshot restored: the override is gone (it had no prior value).
        JsonElement cfg = await Json(admin.GetAsync($"/api/v1/hosts/{Host}/services/assistant/config"));
        JsonElement key = cfg.GetProperty("fields").EnumerateArray()
            .First(x => x.GetProperty("key").GetString() == "webSearchApiKey");
        Assert.False(key.GetProperty("overridden").GetBoolean());
        Assert.False(key.GetProperty("set").GetBoolean());

        // The rollback is audited (warn).
        JsonElement audit = await Json(admin.GetAsync("/api/v1/audit"));
        JsonElement row = audit.GetProperty("data").EnumerateArray()
            .First(r => r.GetProperty("action").GetString() == "service.config");
        Assert.Equal("rolled_back", row.GetProperty("meta").GetProperty("outcome").GetString());
    }

    // ---- PUT: no effective change → unchanged (no restart) ----------------------------------------
    [Fact]
    public async Task PutConfig_SameValue_Unchanged_NoRestart()
    {
        using var f = new LeafConfigTestFactory();
        HttpClient admin = Admin(f);

        await Put(admin, "monitor", """{"values":{"logLevel":"Warning"}}""");          // applied (1 restart)
        Assert.Equal(1, f.Units().RestartCount(MonitorUnit));

        JsonElement again = await Json(Put(admin, "monitor", """{"values":{"logLevel":"Warning"}}"""));
        Assert.Equal("unchanged", again.GetProperty("outcome").GetString());
        Assert.Equal(1, f.Units().RestartCount(MonitorUnit));                            // still 1 — no restart
    }

    // ---- PUT: reset deletes the override ----------------------------------------------------------
    [Fact]
    public async Task PutConfig_Reset_DeletesOverride()
    {
        using var f = new LeafConfigTestFactory();
        HttpClient admin = Admin(f);

        await Put(admin, "monitor", """{"values":{"logLevel":"Debug"}}""");
        Assert.True(File.Exists(Path.Combine(f.OverridesDir, "monitor.env")));

        JsonElement reset = await Json(Put(admin, "monitor", """{"reset":["logLevel"]}"""));
        Assert.Equal("applied", reset.GetProperty("outcome").GetString());

        JsonElement cfg = await Json(admin.GetAsync($"/api/v1/hosts/{Host}/services/monitor/config"));
        JsonElement ll = cfg.GetProperty("fields").EnumerateArray().First(x => x.GetProperty("key").GetString() == "logLevel");
        Assert.False(ll.GetProperty("overridden").GetBoolean());
        // No overrides left → the file is removed (reset-to-floor).
        Assert.False(File.Exists(Path.Combine(f.OverridesDir, "monitor.env")));
    }

    // ---- PUT: validation -------------------------------------------------------------------------
    [Fact]
    public async Task PutConfig_UnknownKey_400()
    {
        using var f = new LeafConfigTestFactory();
        HttpResponseMessage resp = await Put(Admin(f), "monitor", """{"values":{"nope":"x"}}""");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PutConfig_InvalidEnum_400()
    {
        using var f = new LeafConfigTestFactory();
        HttpResponseMessage resp = await Put(Admin(f), "monitor", """{"values":{"logLevel":"Loud"}}""");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PutConfig_NonConfigTargetLeaf_404()
    {
        using var f = new LeafConfigTestFactory();
        HttpResponseMessage resp = await Put(Admin(f), "bot", """{"values":{"logLevel":"Debug"}}""");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PutConfig_Operator_403()
    {
        using var f = new LeafConfigTestFactory();
        HttpResponseMessage resp = await Put(Client(f, AuthTier.Operator), "monitor", """{"values":{"logLevel":"Debug"}}""");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- helpers ----------------------------------------------------------------------------------
    private static HttpClient Admin(LeafConfigTestFactory f) => Client(f, AuthTier.Admin);

    private static HttpClient Client(LeafConfigTestFactory factory, AuthTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Put(HttpClient c, string leaf, string json) =>
        c.PutAsync($"/api/v1/hosts/{Host}/services/{leaf}/config", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Json(Task<HttpResponseMessage> respTask)
    {
        HttpResponseMessage resp = await respTask;
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
