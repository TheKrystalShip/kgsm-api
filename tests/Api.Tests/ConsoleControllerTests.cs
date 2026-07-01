using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// #8 coverage for the REST scrollback endpoint <c>GET /api/v1/servers/{id}/console?tail=N</c>, proven
/// through the real pipeline. Load-bearing: viewer-gated (401 no bearer / 403 tier 'none'); the <c>?tail=</c>
/// happy path returns <c>{ lines: [...] }</c> from the watchdog tail; the watchdog being ABSENT (the
/// AuthTestFactory default — unprovisioned) degrades to <c>{ lines: [] }</c>, NEVER a 500; the watchdog
/// being DOWN (a transport throw) likewise degrades to <c>{ lines: [] }</c>.
/// </summary>
public sealed class ConsoleControllerTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Client(string? token = null)
    {
        HttpClient c = factory.CreateClient();
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    // A factory variant that registers a fake IWatchdogClient (the base leaves it unprovisioned/absent).
    private HttpClient ClientWithWatchdog(IWatchdogClient watchdog, AuthTier tier)
    {
        HttpClient c = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IWatchdogClient>();
                s.AddSingleton(watchdog);
            })).CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(tier));
        return c;
    }

    [Fact]
    public async Task NoToken_401()
    {
        HttpResponseMessage resp = await Client().GetAsync("/api/v1/servers/mc/console");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoneTier_403()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.None)).GetAsync("/api/v1/servers/mc/console");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_WatchdogAbsent_200_EmptyLines_NotA500()
    {
        // The AuthTestFactory leaves the watchdog unprovisioned → no IWatchdogClient → honest empty, never 500.
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Viewer)).GetAsync("/api/v1/servers/mc/console");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(Array.Empty<string>(), await ReadLines(resp));
    }

    [Fact]
    public async Task Viewer_Tail_HappyPath_ReturnsLines()
    {
        var wd = new FakeTailWatchdog(["[server] starting", "[server] ready", "player joined"]);
        HttpResponseMessage resp = await ClientWithWatchdog(wd, AuthTier.Viewer)
            .GetAsync("/api/v1/servers/mc/console?tail=3");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new[] { "[server] starting", "[server] ready", "player joined" }, await ReadLines(resp));
        Assert.Equal("mc", wd.LastInstance);
        Assert.Equal(3, wd.LastLines); // ?tail= forwarded
    }

    [Fact]
    public async Task Viewer_NoTailParam_DefaultsTo200()
    {
        var wd = new FakeTailWatchdog([]);
        HttpResponseMessage resp = await ClientWithWatchdog(wd, AuthTier.Viewer).GetAsync("/api/v1/servers/mc/console");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(200, wd.LastLines); // default tail when ?tail= omitted
    }

    [Fact]
    public async Task Viewer_WatchdogDown_DegradesToEmptyLines_NotA500()
    {
        // Provisioned but unreachable: GetConsoleTailAsync throws (transport) → controller degrades to empty.
        var wd = new FakeTailWatchdog(throws: true);
        HttpResponseMessage resp = await ClientWithWatchdog(wd, AuthTier.Viewer).GetAsync("/api/v1/servers/mc/console?tail=50");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(Array.Empty<string>(), await ReadLines(resp));
    }

    private static async Task<string[]> ReadLines(HttpResponseMessage resp)
    {
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("lines").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    // A fake IWatchdogClient whose console tail returns a canned set (or throws like a down daemon).
    private sealed class FakeTailWatchdog(string[]? lines = null, bool throws = false) : IWatchdogClient
    {
        private readonly string[] _lines = lines ?? Array.Empty<string>();
        public string? LastInstance { get; private set; }
        public int LastLines { get; private set; }

        public Task<IReadOnlyList<string>> GetConsoleTailAsync(string instanceName, int lines, CancellationToken cancellationToken = default)
        {
            LastInstance = instanceName;
            LastLines = lines;
            if (throws) throw new HttpRequestException("watchdog unreachable (test)");
            return Task.FromResult<IReadOnlyList<string>>(_lines);
        }

        // Unused by the scrollback controller — satisfy the interface.
        public IAsyncEnumerable<string> FollowConsoleAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<WatchdogReadyState?> GetReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult<WatchdogReadyState?>(null);
        public Task<WatchdogActionResult> StartAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogActionResult> StopAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogInstanceState?> GetStatusAsync(string instanceName, CancellationToken cancellationToken = default) => Task.FromResult<WatchdogInstanceState?>(null);
        public Task<IReadOnlyList<WatchdogInstanceState>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WatchdogInstanceState>>(Array.Empty<WatchdogInstanceState>());
        public Task<IReadOnlyDictionary<string, IReadOnlyList<WatchdogPlayer>>?> GetAllPlayersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<WatchdogPlayer>>?>(null);
        public void Dispose() { }
    }
}
