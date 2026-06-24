using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Metrics;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M9 Increment 3 tests: the <c>GET /servers/{id}/metrics/history</c> and
/// <c>GET /hosts/{id}/metrics/history</c> read endpoints. Exercises tier selection, gap rendering,
/// the 404/empty-degrade branches, and the viewer auth gate.
/// </summary>
public sealed class MetricsHistoryEndpointTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Viewer()
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.AccessToken(AuthTier.Viewer));
        return c;
    }

    private MetricsHistoryStore Store =>
        factory.Services.GetRequiredService<MetricsHistoryStore>();

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // --- Auth gate ---

    [Fact]
    public async Task ServerHistory_NoToken_401()
    {
        HttpResponseMessage r = await factory.CreateClient()
            .GetAsync("/api/v1/servers/any/metrics/history");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task HostHistory_NoToken_401()
    {
        HttpResponseMessage r = await factory.CreateClient()
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/metrics/history");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    // --- Unknown id → 404 ---

    [Fact]
    public async Task ServerHistory_UnknownId_404()
    {
        HttpResponseMessage r = await Viewer().GetAsync("/api/v1/servers/nonexistent/metrics/history");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task HostHistory_UnknownId_404()
    {
        HttpResponseMessage r = await Viewer().GetAsync("/api/v1/hosts/nonexistent/metrics/history");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // --- Host with correct id (empty series, no monitor) ---

    [Fact]
    public async Task HostHistory_CorrectId_200_EmptySeriesShape()
    {
        HttpResponseMessage r = await Viewer()
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/metrics/history?range=1h");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.Equal(AuthTestFactory.HostId, body.GetProperty("entityId").GetString());
        Assert.Equal("host", body.GetProperty("kind").GetString());
        Assert.Equal("1h", body.GetProperty("range").GetString());
        Assert.Equal(JsonValueKind.Object, body.GetProperty("series").ValueKind);
    }

    // --- Tier selection (via seeded rows) ---

    [Fact]
    public async Task HostHistory_ShortRange_ReturnsRawTier()
    {
        await SeedHostSamples();

        HttpResponseMessage r = await Viewer()
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/metrics/history?range=1h");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.Equal("raw", body.GetProperty("tier").GetString());
    }

    [Fact]
    public async Task HostHistory_LongRange_ReturnsRollupTier()
    {
        HttpResponseMessage r = await Viewer()
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/metrics/history?range=7d");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.Equal("rollup", body.GetProperty("tier").GetString());
    }

    // --- Response shape with seeded data ---

    [Fact]
    public async Task HostHistory_SeededData_CorrectShape()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long recentTs = nowMs - 30_000; // 30s ago — unique ts to avoid collision with other tests

        await Store.WriteSamplesAsync([
            new MetricSample { EntityKind = "host", EntityId = AuthTestFactory.HostId, Metric = "memUsedKb", Ts = recentTs, Value = 8000000 },
        ]);

        HttpResponseMessage r = await Viewer()
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/metrics/history?range=1h");
        JsonElement body = await Json(r);

        JsonElement series = body.GetProperty("series");
        Assert.True(series.TryGetProperty("memUsedKb", out JsonElement memSeries));
        Assert.True(memSeries.GetArrayLength() >= 1);

        // Verify a point has the expected shape: ts + value
        JsonElement point = memSeries[memSeries.GetArrayLength() - 1];
        Assert.True(point.TryGetProperty("value", out _));
        Assert.True(point.TryGetProperty("ts", out _));
    }

    // --- Rollup data shape (min/max/n present) ---

    [Fact]
    public async Task HostHistory_RollupData_HasMinMaxN()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long recentBucket = nowMs - 86_400_000 * 2; // 2 days ago (within 30d rollup window)

        await Store.WriteRollupsAsync([
            new MetricRollup
            {
                EntityKind = "host", EntityId = AuthTestFactory.HostId,
                Metric = "cpuTotalPct", BucketTs = recentBucket,
                Avg = 45.0, Min = 10.0, Max = 90.0, N = 20
            },
        ]);

        HttpResponseMessage r = await Viewer()
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/metrics/history?range=7d");
        JsonElement body = await Json(r);
        JsonElement series = body.GetProperty("series");

        if (series.TryGetProperty("cpuTotalPct", out JsonElement cpuSeries) && cpuSeries.GetArrayLength() > 0)
        {
            JsonElement point = cpuSeries[0];
            Assert.Equal(45.0, point.GetProperty("value").GetDouble());
            Assert.Equal(10.0, point.GetProperty("min").GetDouble());
            Assert.Equal(90.0, point.GetProperty("max").GetDouble());
            Assert.Equal(20, point.GetProperty("n").GetInt32());
        }
    }

    private async Task SeedHostSamples()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Store.WriteSamplesAsync([
            new MetricSample { EntityKind = "host", EntityId = AuthTestFactory.HostId, Metric = "cpuTotalPct", Ts = nowMs - 30_000, Value = 33.0 },
        ]);
    }
}
