using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Phase 1 (dynamic provisioning) coverage — connect/disconnect a leaf at runtime: the registry+capability
/// flip, the <c>capabilities.patch</c> SSE emit, the audit row, the owner gate (operator 403 / no token 401),
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
        HttpClient owner = Client(factory, Persona.Owner);

        // Baseline: monitor absent (provisioned:false) on the capability block + the Services row.
        Assert.False(await MetricsProvisioned(owner));
        Assert.False(await ServiceProvisioned(owner, MonitorUnitless));

        HttpResponseMessage resp = await owner.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonElement row = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(MonitorUnitless, row.GetProperty("id").GetString());
        Assert.True(row.GetProperty("provisioned").GetBoolean());

        // The capability block + the Services row now report provisioned:true.
        Assert.True(await MetricsProvisioned(owner));
        Assert.True(await ServiceProvisioned(owner, MonitorUnitless));

        // A service.connect audit row landed, targeting the leaf, actor = the caller.
        JsonElement audit = await Json(owner.GetAsync("/api/v1/audit"));
        JsonElement[] rows = audit.GetProperty("data").EnumerateArray().ToArray();
        JsonElement connect = rows.First(r => r.GetProperty("action").GetString() == "service.connected");
        Assert.Equal("leaf", connect.GetProperty("target").GetProperty("kind").GetString());
        Assert.Equal(MonitorUnitless, connect.GetProperty("target").GetProperty("id").GetString());
        Assert.Equal("api", connect.GetProperty("origin").GetString());
    }

    // ---- disconnect reverses ----------------------------------------------------------------------
    [Fact]
    public async Task Disconnect_Reverses_AndAudits()
    {
        using var factory = new LeafTestFactory();
        HttpClient owner = Client(factory, Persona.Owner);

        await owner.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
        Assert.True(await MetricsProvisioned(owner));

        HttpResponseMessage resp = await owner.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/disconnect", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("provisioned").GetBoolean());

        Assert.False(await MetricsProvisioned(owner));

        JsonElement audit = await Json(owner.GetAsync("/api/v1/audit"));
        Assert.Contains(audit.GetProperty("data").EnumerateArray(),
            r => r.GetProperty("action").GetString() == "service.disconnected");
    }

    // ---- the capabilities.patch is emitted on the stream when a leaf connects ----------------------
    [Fact]
    public async Task Connect_EmitsCapabilitiesPatch_OnTheStream()
    {
        using var factory = new LeafTestFactory();
        string token = factory.AccessToken(Persona.Owner);

        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            factory.CreateClient(), $"/api/v1/stream?topics=hosts/{Host}/capabilities", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(resp);

        HttpClient owner = Client(factory, Persona.Owner);

        // Connect repeatedly across the read window so the subscription is certainly live before the flip we
        // observe (the LeafProvisioningController flip is idempotent → re-connecting stays provisioned:true).
        JsonElement? frame = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await owner.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
            await owner.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/disconnect", null);
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

    // ---- owner gate -------------------------------------------------------------------------------
    [Fact]
    public async Task Connect_Operator_403()
    {
        using var factory = new LeafTestFactory();
        HttpResponseMessage resp = await Client(factory, Persona.Runner)
            .PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Connect_NoToken_401()
    {
        using var factory = new LeafTestFactory();
        HttpResponseMessage resp = await Client(factory, persona: null)
            .PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- foreign host / unknown / non-provisionable leaf → 404 ------------------------------------
    [Fact]
    public async Task Connect_ForeignHost_404()
    {
        using var factory = new LeafTestFactory();
        HttpResponseMessage resp = await Client(factory, Persona.Owner)
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
        HttpResponseMessage resp = await Client(factory, Persona.Owner)
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
                HttpClient owner = Client(first, Persona.Owner);
                await owner.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/connect", null);
                Assert.True(await ServiceProvisioned(owner, MonitorUnitless));
            } // dispose → host stops → the SQLite row is committed

            // A second process pointed at the SAME db must load the persisted flip on startup (no re-connect).
            using var second = new LeafTestFactory(db);
            HttpClient owner2 = Client(second, Persona.Owner);
            Assert.True(await ServiceProvisioned(owner2, MonitorUnitless));
            Assert.True(await MetricsProvisioned(owner2));
        }
        finally { try { File.Delete(db); } catch { /* best effort */ } }
    }

    // ---- a runtime disconnect also survives, and a real config change still wins -------------------
    [Fact]
    public async Task Disconnect_PersistsAcrossRestart()
    {
        string db = Path.Combine(Path.GetTempPath(), $"kgsm-api-leaf-disc-{Guid.NewGuid():N}.db");
        const string socket = "/run/kgsm-monitor/metrics.sock";
        try
        {
            using (var first = new LeafTestFactory(db, socket))
            {
                HttpClient owner = Client(first, Persona.Owner);
                Assert.True(await ServiceProvisioned(owner, MonitorUnitless)); // config seeds it provisioned
                await owner.PostAsync($"/api/v1/hosts/{Host}/services/{MonitorUnitless}/disconnect", null);
                Assert.False(await ServiceProvisioned(owner, MonitorUnitless));
            }

            // Same config, so nothing about the host moved — the operator's disconnect stands.
            using var second = new LeafTestFactory(db, socket);
            Assert.False(await ServiceProvisioned(Client(second, Persona.Owner), MonitorUnitless));
        }
        finally { try { File.Delete(db); } catch { /* best effort */ } }
    }

    /// <summary>
    /// The case the stored flag alone cannot answer: nobody flipped anything, but the host's configuration
    /// dropped the monitor's endpoint. The seed genuinely moved, so it wins — reporting the leaf as still
    /// connected would claim a capability this host no longer has.
    /// </summary>
    [Fact]
    public async Task ConfigSeedChange_WinsOverTheStoredFlag()
    {
        string db = Path.Combine(Path.GetTempPath(), $"kgsm-api-leaf-seed-{Guid.NewGuid():N}.db");
        try
        {
            using (var configured = new LeafTestFactory(db, "/run/kgsm-monitor/metrics.sock"))
                Assert.True(await ServiceProvisioned(Client(configured, Persona.Owner), MonitorUnitless));

            // The endpoint is gone from config now — and no runtime flip ever recorded a contrary intent.
            using var deconfigured = new LeafTestFactory(db);
            HttpClient owner = Client(deconfigured, Persona.Owner);
            Assert.False(await ServiceProvisioned(owner, MonitorUnitless));
            Assert.False(await MetricsProvisioned(owner));
        }
        finally { try { File.Delete(db); } catch { /* best effort */ } }
    }

    /// <summary>
    /// A host deployed before the seed was recorded already has the table, so neither EnsureCreated nor the
    /// CREATE TABLE IF NOT EXISTS adds the column — the registry has to ALTER it in. The stored flag must
    /// survive that: an existing row's seed is unknown, which is not a licence to overwrite the row.
    /// </summary>
    [Fact]
    public async Task DeployedDbWithoutTheSeedColumn_IsMigratedInPlace()
    {
        string db = Path.Combine(Path.GetTempPath(), $"kgsm-api-leaf-legacy-{Guid.NewGuid():N}.db");
        try
        {
            // A fully-populated deployed DB (every other table present), rewound to the older leaf_registry
            // shape: the column gone, and a row that says connected with no seed recorded.
            using (var deployed = new LeafTestFactory(db))
                deployed.CreateClient();

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
            {
                await conn.OpenAsync();
                using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText =
                    """
                    ALTER TABLE leaf_registry DROP COLUMN "ConfigSeed";
                    UPDATE leaf_registry SET "Provisioned" = 1 WHERE "LeafId" = 'monitor';
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            // Config does not provide the monitor, but the deployed row says connected and carries no seed to
            // contradict it — so it stands, and the column is now there for the next start to compare against.
            using var factory = new LeafTestFactory(db);
            Assert.True(await ServiceProvisioned(Client(factory, Persona.Owner), MonitorUnitless));
        }
        finally { try { File.Delete(db); } catch { /* best effort */ } }
    }

    // ---- helpers ----------------------------------------------------------------------------------
    private static HttpClient Client(LeafTestFactory factory, Persona? persona)
    {
        HttpClient c = factory.CreateClient();
        if (persona is { } t)
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
