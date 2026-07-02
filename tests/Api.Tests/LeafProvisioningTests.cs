using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Phase 1 (dynamic provisioning) coverage — connect/disconnect a leaf at runtime: the registry+capability
/// flip, the <c>capabilities.patch</c> SSE emit, the audit row, the admin gate (operator 403 / no token 401),
/// the foreign-host / unknown-leaf 404s, and persistence across a simulated restart. Each mutating test uses
/// its own factory (fresh DB) so the registry state never leaks between tests.
/// </summary>
public sealed class LeafProvisioningTests
{
    private const string Host = AuthTestFactory.HostId;
    private const string MonitorUnitless = "monitor";

    // ---- connect flips absent → provisioned (registry + capability) + audit -----------------------
    [Fact]
    public async Task Connect_Monitor_FlipsProvisioned_AndAudits()
    {
        using var factory = new LeafTestFactory();
        HttpClient admin = Client(factory, AuthTier.Admin);

        // Baseline: monitor absent (provisioned:false) on the capability block + the Services row.
        Assert.False(await MetricsProvisioned(admin));
        Assert.False(await ServiceProvisioned(admin, MonitorUnitless));

        HttpResponseMessage resp = await admin.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonElement row = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(MonitorUnitless, row.GetProperty("id").GetString());
        Assert.True(row.GetProperty("provisioned").GetBoolean());

        // The capability block + the Services row now report provisioned:true.
        Assert.True(await MetricsProvisioned(admin));
        Assert.True(await ServiceProvisioned(admin, MonitorUnitless));

        // A service.connect audit row landed, targeting the leaf, actor = the caller.
        JsonElement audit = await Json(admin.GetAsync("/api/v1/audit"));
        JsonElement[] rows = audit.GetProperty("data").EnumerateArray().ToArray();
        JsonElement connect = rows.First(r => r.GetProperty("action").GetString() == "service.connect");
        Assert.Equal("leaf", connect.GetProperty("target").GetProperty("kind").GetString());
        Assert.Equal(MonitorUnitless, connect.GetProperty("target").GetProperty("id").GetString());
        Assert.Equal("api", connect.GetProperty("origin").GetString());
    }

    // ---- disconnect reverses ----------------------------------------------------------------------
    [Fact]
    public async Task Disconnect_Reverses_AndAudits()
    {
        using var factory = new LeafTestFactory();
        HttpClient admin = Client(factory, AuthTier.Admin);

        await admin.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
        Assert.True(await MetricsProvisioned(admin));

        HttpResponseMessage resp = await admin.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/disconnect", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("provisioned").GetBoolean());

        Assert.False(await MetricsProvisioned(admin));

        JsonElement audit = await Json(admin.GetAsync("/api/v1/audit"));
        Assert.Contains(audit.GetProperty("data").EnumerateArray(),
            r => r.GetProperty("action").GetString() == "service.disconnect");
    }

    // ---- the capabilities.patch is emitted on the stream when a leaf connects ----------------------
    [Fact]
    public async Task Connect_EmitsCapabilitiesPatch_OnTheStream()
    {
        using var factory = new LeafTestFactory();
        string token = factory.AccessToken(AuthTier.Admin);

        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            factory.CreateClient(), $"/api/v1/stream?topics=hosts/{Host}/capabilities", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(resp);

        HttpClient admin = Client(factory, AuthTier.Admin);

        // Connect repeatedly across the read window so the subscription is certainly live before the flip we
        // observe (the LeafProvisioningController flip is idempotent → re-connecting stays provisioned:true).
        JsonElement? frame = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await admin.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
            await admin.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/disconnect", null);
            JsonElement? got = await frames.WaitForFrame(
                f => f.GetProperty("type").GetString() == "capabilities.patch", TimeSpan.FromMilliseconds(500));
            if (got is not null)
            {
                frame = got;
                break;
            }
        }

        Assert.NotNull(frame);
        JsonElement env = frame!.Value;
        Assert.Equal("capabilities.patch", env.GetProperty("type").GetString());
        Assert.True(env.GetProperty("data").TryGetProperty("metrics", out _)); // the full capability block
    }

    // ---- admin gate -------------------------------------------------------------------------------
    [Fact]
    public async Task Connect_Operator_403()
    {
        using var factory = new LeafTestFactory();
        HttpResponseMessage resp = await Client(factory, AuthTier.Operator)
            .PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Connect_NoToken_401()
    {
        using var factory = new LeafTestFactory();
        HttpResponseMessage resp = await Client(factory, tier: null)
            .PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- foreign host / unknown / non-provisionable leaf → 404 ------------------------------------
    [Fact]
    public async Task Connect_ForeignHost_404()
    {
        using var factory = new LeafTestFactory();
        HttpResponseMessage resp = await Client(factory, AuthTier.Admin)
            .PostAsync($"/api/v1/hosts/not-this-host/services/{MonitorUnitless}/connect", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Theory]
    [InlineData("nope")]   // unknown leaf
    [InlineData("api")]    // a real leaf, but not runtime-provisionable
    [InlineData("bot")]
    public async Task Connect_UnknownOrNonProvisionableLeaf_404(string leaf)
    {
        using var factory = new LeafTestFactory();
        HttpResponseMessage resp = await Client(factory, AuthTier.Admin)
            .PostAsync($"/api/v1/hosts/{Host}/services/{leaf}/connect", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- provisioning persists across a restart (registry is DB-backed) ---------------------------
    [Fact]
    public async Task Provisioning_PersistsAcrossRestart()
    {
        string db = Path.Combine(Path.GetTempPath(), $"kgsm-api-leaf-persist-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new LeafTestFactory(db))
            {
                HttpClient admin = Client(first, AuthTier.Admin);
                await admin.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
                Assert.True(await ServiceProvisioned(admin, MonitorUnitless));
            } // dispose → host stops → the SQLite row is committed

            // A second process pointed at the SAME db must load the persisted flip on startup (no re-connect).
            using var second = new LeafTestFactory(db);
            HttpClient admin2 = Client(second, AuthTier.Admin);
            Assert.True(await ServiceProvisioned(admin2, MonitorUnitless));
            Assert.True(await MetricsProvisioned(admin2));
        }
        finally { try { File.Delete(db); } catch { /* best effort */ } }
    }

    // ---- helpers ----------------------------------------------------------------------------------
    private static HttpClient Client(LeafTestFactory factory, AuthTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static async Task<bool> MetricsProvisioned(HttpClient c)
    {
        JsonElement d = await Json(c.GetAsync($"/api/v1/hosts/{Host}"));
        return d.GetProperty("capabilities").GetProperty("metrics").GetProperty("provisioned").GetBoolean();
    }

    private static async Task<bool> ServiceProvisioned(HttpClient c, string leaf)
    {
        JsonElement d = await Json(c.GetAsync($"/api/v1/hosts/{Host}/services"));
        JsonElement row = d.GetProperty("data").EnumerateArray().First(s => s.GetProperty("id").GetString() == leaf);
        return row.TryGetProperty("provisioned", out JsonElement p) && p.GetBoolean();
    }

    private static async Task<JsonElement> Json(Task<HttpResponseMessage> respTask)
    {
        HttpResponseMessage resp = await respTask;
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
