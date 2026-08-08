using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The <c>GET /servers/{id}/metrics/history</c> and <c>GET /hosts/{id}/metrics/history</c> endpoints,
/// now a <b>verbatim proxy</b> to kgsm-monitor. Exercises the viewer auth gate, the 404 existence
/// checks, the monitor-absent empty-degrade (the base factory leaves the monitor unprovisioned), and
/// the verbatim relay of a monitor response (via a fake <see cref="IMonitorHistoryClient"/>).
/// </summary>
public sealed class MetricsHistoryEndpointTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Viewer()
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.AccessToken(KgsmTier.Viewer));
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // A fake monitor history client that answers with a fixed body (the verbatim-relay seam). The stats
    // relay shares the interface but not these tests' subject, so it answers null — the honest "this
    // monitor wouldn't say", which is what a fake with nothing to report should be.
    private sealed class FakeMonitorHistory(string? json) : IMonitorHistoryClient
    {
        public Task<string?> GetHistoryJsonAsync(string kind, string id, string? range, CancellationToken ct) =>
            Task.FromResult(json);

        public Task<string?> GetStatsJsonAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

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

    // --- Unknown id → 404 (existence checks survive the proxy rewrite) ---

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

    // --- Monitor absent (unprovisioned) → honest empty 200, correct shape ---

    [Fact]
    public async Task HostHistory_MonitorAbsent_200_EmptySeriesShape()
    {
        HttpResponseMessage r = await Viewer()
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/metrics/history?range=1h");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.Equal(AuthTestFactory.HostId, body.GetProperty("entityId").GetString());
        Assert.Equal("host", body.GetProperty("kind").GetString());
        Assert.Equal("1h", body.GetProperty("range").GetString());
        JsonElement series = body.GetProperty("series");
        Assert.Equal(JsonValueKind.Object, series.ValueKind);
        Assert.Empty(series.EnumerateObject()); // no fabricated curve when the monitor is absent
    }

    // --- Leaf history: same proxy, the `leaf` entity kind ---

    [Fact]
    public async Task LeafHistory_NoToken_401()
    {
        HttpResponseMessage r = await factory.CreateClient()
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/services/monitor/metrics/history");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task LeafHistory_UnknownLeaf_404()
    {
        HttpResponseMessage r = await Viewer()
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/services/not-a-leaf/metrics/history");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task LeafHistory_UnknownHost_404()
    {
        HttpResponseMessage r = await Viewer()
            .GetAsync("/api/v1/hosts/nonexistent/services/monitor/metrics/history");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task LeafHistory_KnownLeafWithNoRows_200_EmptySeries_NotA404()
    {
        // The leaf exists; its history doesn't (never running since the monitor started, or a monitor too
        // old to sample leaves). Those are different facts and the second one is not a missing entity.
        HttpResponseMessage r = await Viewer()
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/services/monitor/metrics/history?range=1h");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.Equal("monitor", body.GetProperty("entityId").GetString());
        Assert.Equal("leaf", body.GetProperty("kind").GetString());
        Assert.Empty(body.GetProperty("series").EnumerateObject());
    }

    [Fact]
    public async Task LeafHistory_RelaysMonitorBodyVerbatim()
    {
        // The monitor stores leaf samples under the SERVER metric names on purpose — same quantities, same
        // units — so one chart component renders either without knowing which it was handed.
        string monitorBody =
            "{\"entityId\":\"watchdog\",\"kind\":\"leaf\",\"range\":\"1h\",\"step\":15,\"tier\":\"raw\"," +
            "\"series\":{\"memBytes\":[" +
            "{\"ts\":\"2026-08-05T00:00:00+00:00\",\"value\":99676160,\"min\":null,\"max\":null,\"n\":null}]}}";

        using WebApplicationFactoryWithFake fakeFactory = new(monitorBody);
        HttpClient client = fakeFactory.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AuthTestFactory.MintTokenWithRow(fakeFactory.Factory.Services, KgsmTier.Viewer, access: true));

        HttpResponseMessage r = await client.GetAsync(
            $"/api/v1/hosts/{AuthTestFactory.HostId}/services/watchdog/metrics/history?range=1h");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.Equal("leaf", body.GetProperty("kind").GetString());
        Assert.Equal(99676160, body.GetProperty("series").GetProperty("memBytes")[0].GetProperty("value").GetInt64());
    }

    // --- Verbatim relay of the monitor's body ---

    [Fact]
    public async Task HostHistory_RelaysMonitorBodyVerbatim()
    {
        string monitorBody =
            "{\"entityId\":\"" + AuthTestFactory.HostId + "\",\"kind\":\"host\",\"range\":\"1h\"," +
            "\"step\":15,\"tier\":\"raw\",\"series\":{\"memUsedKb\":[" +
            "{\"ts\":\"2026-07-18T00:00:00+00:00\",\"value\":8000000,\"min\":null,\"max\":null,\"n\":null}]}}";

        using WebApplicationFactoryWithFake fakeFactory = new(monitorBody);
        HttpClient client = fakeFactory.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AuthTestFactory.MintTokenWithRow(fakeFactory.Factory.Services, KgsmTier.Viewer, access: true));

        HttpResponseMessage r = await client.GetAsync(
            $"/api/v1/hosts/{AuthTestFactory.HostId}/metrics/history?range=1h");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.Equal("raw", body.GetProperty("tier").GetString());
        Assert.Equal(15, body.GetProperty("step").GetInt32());
        JsonElement mem = body.GetProperty("series").GetProperty("memUsedKb");
        Assert.Equal(1, mem.GetArrayLength());
        Assert.Equal(8000000, mem[0].GetProperty("value").GetInt64());
    }

    // Wraps a derived factory whose IMonitorHistoryClient is the fake; disposes both together.
    private sealed class WebApplicationFactoryWithFake : IDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }

        public WebApplicationFactoryWithFake(string monitorBody)
        {
            var baseFactory = new AuthTestFactory();
            Factory = baseFactory.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IMonitorHistoryClient>();
                    services.AddSingleton<IMonitorHistoryClient>(new FakeMonitorHistory(monitorBody));
                }));
        }

        public void Dispose() => Factory.Dispose();
    }
}
