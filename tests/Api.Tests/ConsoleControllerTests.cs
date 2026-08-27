using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using TheKrystalShip.KGSM.Auth;

using TheKrystalShip.KGSM.Auth.Sessions;

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
    // ⚠ WithWebHostBuilder builds a DERIVED factory with its OWN random Api__DbPath + service provider —
    // different from the base factory. The session row MUST land in the derived factory's DB (the request
    // goes through the derived pipeline, whose SessionValidator queries the derived DB), so the token is
    // minted + inserted via the derived factory's Services (AuthTestFactory.MintTokenWithRow), NOTfactory.AccessToken
    // (which uses the base factory's Services + DB — the row would be invisible to the derived validator → 401).
    private HttpClient ClientWithWatchdog(IWatchdogClient watchdog, KgsmTier tier)
    {
        var derived = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IWatchdogClient>();
                s.AddSingleton(watchdog);
            }));
        HttpClient c = derived.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            AuthTestFactory.MintTokenWithRow(derived.Services, tier, access: true));
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
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.None)).GetAsync("/api/v1/servers/mc/console");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_WatchdogAbsent_200_EmptyLines_NotA500()
    {
        // The AuthTestFactory leaves the watchdog unprovisioned → no IWatchdogClient → honest empty, never 500.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer)).GetAsync("/api/v1/servers/mc/console");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(Array.Empty<string>(), await ReadLines(resp));
    }

    [Fact]
    public async Task Viewer_Tail_HappyPath_ReturnsLines()
    {
        var wd = new FakeTailWatchdog(["[server] starting", "[server] ready", "player joined"]);
        HttpResponseMessage resp = await ClientWithWatchdog(wd, KgsmTier.Viewer)
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
        HttpResponseMessage resp = await ClientWithWatchdog(wd, KgsmTier.Viewer).GetAsync("/api/v1/servers/mc/console");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(200, wd.LastLines); // default tail when ?tail= omitted
    }

    [Fact]
    public async Task Viewer_WatchdogDown_DegradesToEmptyLines_NotA500()
    {
        // Provisioned but unreachable: GetConsoleTailAsync throws (transport) → controller degrades to empty.
        var wd = new FakeTailWatchdog(throws: true);
        HttpResponseMessage resp = await ClientWithWatchdog(wd, KgsmTier.Viewer).GetAsync("/api/v1/servers/mc/console?tail=50");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(Array.Empty<string>(), await ReadLines(resp));
    }

    private static async Task<string[]> ReadLines(HttpResponseMessage resp)
    {
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("lines").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private static async Task<JsonElement> ReadBody(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();

    // ---- reading further back: the cursor, not a line count ----

    [Fact]
    public async Task Viewer_Scrollback_ReportsTheRangeItServed()
    {
        var wd = new FakeTailWatchdog(["a", "b"], start: 4096);
        HttpResponseMessage resp = await ClientWithWatchdog(wd, KgsmTier.Viewer)
            .GetAsync("/api/v1/servers/mc/console?tail=2");

        JsonElement body = await ReadBody(resp);
        Assert.Equal(4096, body.GetProperty("start").GetInt64());
        Assert.Equal(4196, body.GetProperty("end").GetInt64());
        Assert.True(body.GetProperty("hasEarlier").GetBoolean());
    }

    [Fact]
    public async Task Viewer_AtTheStartOfTheRun_SaysThereIsNothingEarlier()
    {
        var wd = new FakeTailWatchdog(["first line"], start: 0);
        HttpResponseMessage resp = await ClientWithWatchdog(wd, KgsmTier.Viewer)
            .GetAsync("/api/v1/servers/mc/console?tail=200");

        Assert.False((await ReadBody(resp)).GetProperty("hasEarlier").GetBoolean());
    }

    [Fact]
    public async Task Viewer_Before_ForwardsTheCursor_AndItsAbsenceMeansTheEnd()
    {
        var wd = new FakeTailWatchdog(["x"]);
        HttpClient client = ClientWithWatchdog(wd, KgsmTier.Viewer);

        await client.GetAsync("/api/v1/servers/mc/console?tail=50");
        Assert.Equal(-1, wd.LastEndOffset);   // no ?before= → read from the end of the log

        await client.GetAsync("/api/v1/servers/mc/console?tail=50&before=8192");
        Assert.Equal(8192, wd.LastEndOffset);
    }

    [Fact]
    public async Task Viewer_TailIsClampedButPagingStillReachesEverything()
    {
        // The clamp bounds ONE response. It is not a limit on how far back a caller can read — that is
        // what the cursor is for — so this asserts the clamp without implying a ceiling on the history.
        var wd = new FakeTailWatchdog([]);
        await ClientWithWatchdog(wd, KgsmTier.Viewer).GetAsync("/api/v1/servers/mc/console?tail=999999");

        Assert.Equal(5000, wd.LastLines);
    }

    // ---- the whole log ----

    [Fact]
    public async Task Viewer_Download_StreamsTheWholeLogAsAnAttachment()
    {
        var wd = new FakeTailWatchdog(download: "boot\nplayer joined\ncrash\n");
        HttpResponseMessage resp = await ClientWithWatchdog(wd, KgsmTier.Viewer)
            .GetAsync("/api/v1/servers/mc/console/download");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("mc-console.log", resp.Content.Headers.ContentDisposition?.FileNameStar
            ?? resp.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("boot\nplayer joined\ncrash\n", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Viewer_Download_NoConsole_404_NotAnEmptyFile()
    {
        // An empty file would say the server printed nothing. A 404 says there is no console here —
        // a container, or a watchdog that cannot answer.
        var wd = new FakeTailWatchdog(download: null);
        HttpResponseMessage resp = await ClientWithWatchdog(wd, KgsmTier.Viewer)
            .GetAsync("/api/v1/servers/mc/console/download");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_Download_WatchdogDown_404_NotA500()
    {
        var wd = new FakeTailWatchdog(throws: true, download: "unreachable");
        HttpResponseMessage resp = await ClientWithWatchdog(wd, KgsmTier.Viewer)
            .GetAsync("/api/v1/servers/mc/console/download");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Download_NoToken_401()
    {
        HttpResponseMessage resp = await Client().GetAsync("/api/v1/servers/mc/console/download");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // A fake IWatchdogClient whose console window returns a canned set (or throws like a down daemon).
    // `start` is the byte offset the window claims to begin at — 0 means the run starts there and there
    // is nothing earlier, which is what the controller turns into hasEarlier.
    private sealed class FakeTailWatchdog(string[]? lines = null, bool throws = false, long start = 0, string? download = null) : IWatchdogClient
    {
        private readonly string[] _lines = lines ?? Array.Empty<string>();
        public string? LastInstance { get; private set; }
        public int LastLines { get; private set; }
        public long LastEndOffset { get; private set; } = long.MinValue;
        public int LastRun { get; private set; } = -1;

        public Task<WatchdogConsoleWindow> GetConsoleWindowAsync(string instanceName, int lines, int run, long endOffset, CancellationToken cancellationToken = default)
        {
            LastInstance = instanceName;
            LastLines = lines;
            LastEndOffset = endOffset;
            LastRun = run;
            if (throws) throw new HttpRequestException("watchdog unreachable (test)");
            return Task.FromResult(new WatchdogConsoleWindow(_lines, start, start + 100));
        }

        public async Task<IReadOnlyList<string>> GetConsoleTailAsync(string instanceName, int lines, CancellationToken cancellationToken = default) =>
            (await GetConsoleWindowAsync(instanceName, lines, 0, -1, cancellationToken)).Lines;

        public Task<WatchdogConsoleDownload?> OpenConsoleDownloadAsync(string instanceName, int run, CancellationToken cancellationToken = default)
        {
            LastInstance = instanceName;
            LastRun = run;
            if (throws) throw new HttpRequestException("watchdog unreachable (test)");
            if (download is null) return Task.FromResult<WatchdogConsoleDownload?>(null);

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(download);
            return Task.FromResult<WatchdogConsoleDownload?>(
                new WatchdogConsoleDownload(new MemoryStream(bytes), bytes.Length, new NoopDisposable()));
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }

        // Unused by the scrollback controller — satisfy the interface.
        public IAsyncEnumerable<string> FollowConsoleAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WatchdogConsoleRun>> GetConsoleRunsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetConsoleRunTailAsync(string instanceName, int run, int lines, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<WatchdogReadyState?> GetReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult<WatchdogReadyState?>(null);
        public Task<WatchdogActionResult> StartAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogActionResult> StopAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogActionResult> BeginMaintenanceAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogActionResult> EndMaintenanceAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogActionResult> RestartAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogActionResult> EnableAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogActionResult> DisableAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogActionResult> ForgetAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogActionResult> SetCpuPriorityAsync(string instanceName, string priority, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetEnabledNamesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<WatchdogInstanceState?> GetStatusAsync(string instanceName, CancellationToken cancellationToken = default) => Task.FromResult<WatchdogInstanceState?>(null);
        public Task<IReadOnlyList<WatchdogInstanceState>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WatchdogInstanceState>>(Array.Empty<WatchdogInstanceState>());
        public Task<IReadOnlyList<WatchdogRunTimes>> GetRunTimesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchdogRunTimes>>(Array.Empty<WatchdogRunTimes>());
        public Task<IReadOnlyDictionary<string, WatchdogInstancePresence>?> GetPlayerPresenceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, WatchdogInstancePresence>?>(null);
        public Task<WatchdogUpnpList?> GetUpnpAsync(string instanceName, CancellationToken cancellationToken = default) => Task.FromResult<WatchdogUpnpList?>(null);
        public void Dispose() { }
    }
}
