using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The authorization matrix: the right tier reaches the right endpoint, the wrong one is refused —
/// proven in-process against the real JwtBearer pipeline + tier policies, with Discord faked.
/// 401 (no/invalid bearer) vs 403 (authenticated, tier too low) is the load-bearing split.
/// </summary>
public sealed class TierMatrixTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Client(string? token = null)
    {
        HttpClient c = factory.CreateClient();
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static HttpRequestMessage Command(string id) =>
        new(HttpMethod.Post, $"/api/v1/servers/{id}/commands")
        {
            Content = new StringContent("""{"verb":"start"}""", System.Text.Encoding.UTF8, "application/json"),
        };

    // --- No bearer -> 401 everywhere protected (and the open endpoints stay open) ------------------
    [Theory]
    [InlineData("/api/v1/hosts")]
    [InlineData("/api/v1/servers")]
    [InlineData("/api/v1/stream")]
    public async Task NoToken_Protected_401(string path)
    {
        HttpResponseMessage resp = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoToken_PostCommand_401()
    {
        HttpResponseMessage resp = await Client().SendAsync(Command("anything"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/v1")]
    public async Task OpenEndpoints_NoToken_200(string path)
    {
        HttpResponseMessage resp = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // --- Viewer: reads pass, the mutation is forbidden (403, not 401 — it IS authenticated) --------
    [Fact]
    public async Task Viewer_Reads_200()
    {
        HttpClient c = Client(factory.AccessToken(AuthTier.Viewer));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/hosts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/servers")).StatusCode);
    }

    [Fact]
    public async Task Viewer_PostCommand_403()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Viewer)).SendAsync(Command("anything"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("\"code\":\"forbidden\"", await resp.Content.ReadAsStringAsync());
    }

    // --- Operator / Admin clear the command gate (404 = no such server => authorization PASSED) ----
    [Theory]
    [InlineData(AuthTier.Operator)]
    [InlineData(AuthTier.Admin)]
    public async Task OperatorAndAdmin_PostCommand_PassAuthz_404(AuthTier tier)
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(tier)).SendAsync(Command("no-such-server"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // got past authz to the controller
    }

    [Fact]
    public async Task Admin_Reads_200() =>
        Assert.Equal(HttpStatusCode.OK, (await Client(factory.AccessToken(AuthTier.Admin)).GetAsync("/api/v1/hosts")).StatusCode);

    // --- Host logs (GET /hosts/{id}/logs): OPERATOR-gated, stricter than the viewer-gated audit feed -----
    // (raw journald can carry secrets). The reader shells real journalctl; content is irrelevant to the gate
    // — a 200 (lines or honest-empty) means authorization passed, a 403 means it didn't.
    private static string LogsPath => $"/api/v1/hosts/{AuthTestFactory.HostId}/logs?limit=1";

    [Fact]
    public async Task NoToken_Logs_401()
    {
        HttpResponseMessage resp = await Client().GetAsync(LogsPath);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_Logs_403()
    {
        // A viewer can read the audit log but NOT raw host logs — the deliberate one-tier-up gate.
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Viewer)).GetAsync(LogsPath);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("\"code\":\"forbidden\"", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(AuthTier.Operator)]
    [InlineData(AuthTier.Admin)]
    public async Task OperatorAndAdmin_Logs_200(AuthTier tier)
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(tier)).GetAsync(LogsPath);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // authorization passed -> the page (lines or empty)
    }

    [Fact]
    public async Task Operator_Logs_UnknownHost_404()
    {
        // Past authz (operator) but a foreign host id -> 404, consistent with the rest of the hosts surface.
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Operator))
            .GetAsync("/api/v1/hosts/not-this-host/logs?limit=1");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Operator_Logs_UnknownSource_400()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Operator))
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/logs?source=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- Services board (GET /hosts/{id}/services): OPERATOR-gated, same host-internals sensitivity as the
    // host logs (unit names / pids / memory / enablement). The reader shells real systemctl; content is
    // irrelevant to the gate — a 200 (real rows or honest 'unknown'/'not-installed') means authz passed.
    private static string ServicesPath => $"/api/v1/hosts/{AuthTestFactory.HostId}/services";

    [Fact]
    public async Task NoToken_Services_401()
    {
        HttpResponseMessage resp = await Client().GetAsync(ServicesPath);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_Services_403()
    {
        // Mirrors the host-log gate: a viewer cannot read host service internals — operator and up only.
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Viewer)).GetAsync(ServicesPath);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("\"code\":\"forbidden\"", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(AuthTier.Operator)]
    [InlineData(AuthTier.Admin)]
    public async Task OperatorAndAdmin_Services_200(AuthTier tier)
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(tier)).GetAsync(ServicesPath);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // authorization passed -> the board (real or honest-unknown)
        // The catalog always yields a row per leaf, even on a host where none are installed (state:"not-installed").
        Assert.Contains("\"data\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Operator_Services_UnknownHost_404()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Operator))
            .GetAsync("/api/v1/hosts/not-this-host/services");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // --- Hardening: none-tier, refresh-as-access, wrong signature, garbage -------------------------
    [Fact]
    public async Task NoneTier_Reads_403()
    {
        // Authenticated (valid signature) but tier 'none' -> forbidden, never a default grant.
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.None)).GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_AsAccessBearer_401()
    {
        // A refresh token must never authenticate a protected call (OnTokenValidated rejects it).
        HttpResponseMessage resp = await Client(factory.RefreshToken(AuthTier.Admin)).GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WrongSignature_401()
    {
        string forged = TestTokens.MintAccessWithKey("a-totally-different-signing-key", AuthTestFactory.HostId, AuthTier.Admin);
        HttpResponseMessage resp = await Client(forged).GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task GarbageToken_401()
    {
        HttpResponseMessage resp = await Client("not-a-jwt").GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // --- /stream: fetch-based SSE — bearer rides a header, not a WS handshake. A plain viewer GET (no
    // topics) is a valid 200 SSE connection (no more "not a WS upgrade" 400 — that check is gone from
    // StreamController), so this also covers what used to be Viewer_PlainGetStream_400_NotAuthError. ---
    [Fact]
    public async Task Stream_Sse_WithBearerHeader_Connects()
    {
        string token = factory.AccessToken(AuthTier.Viewer);
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(Client(), "/api/v1/stream", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/event-stream", resp.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Stream_Sse_NoToken_Returns401()
    {
        HttpResponseMessage resp = await SseTestHelpers.OpenStream(Client(), "/api/v1/stream");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // --- the single most important regression test in this file: the old ?access_token= hack is GONE ---
    // (a query token with no Authorization header must never authenticate — SSE sends the bearer only as
    // a header through the normal JwtBearer pipeline; see sse-migration-plan.md §1).
    [Fact]
    public async Task Stream_Sse_QueryTokenIgnored()
    {
        string token = factory.AccessToken(AuthTier.Viewer);
        HttpResponseMessage resp = await SseTestHelpers.OpenStream(Client(), $"/api/v1/stream?access_token={token}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // --- operator-only topics are silently dropped for a non-operator; never a 403 on the whole stream ---
    [Fact]
    public async Task Stream_Sse_OperatorTopic_SilentlyDroppedForViewer()
    {
        string token = factory.AccessToken(AuthTier.Viewer);
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            Client(), $"/api/v1/stream?topics=hosts/{AuthTestFactory.HostId}/logs", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // connects — the drop is silent, not a 403

        using SseFrameReader frames = await SseTestHelpers.Frames(resp);
        // The logs topic was filtered out server-side (non-operator caller) — nothing arrives on this
        // connection within a short bounded wait, proving silence rather than merely an untriggered
        // event (same pattern as PlayerRosterWsTests.Reset_AlreadyEmptyRoster_PublishesNothing).
        JsonElement? frame = await frames.WaitForFrame(_ => true, TimeSpan.FromSeconds(1));
        Assert.Null(frame);
    }
}
