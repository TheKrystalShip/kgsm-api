using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.TestHost;
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
    [InlineData("/api/v1/ping")]
    public async Task OpenEndpoints_NoToken_200(string path)
    {
        HttpResponseMessage resp = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // The SPA's latency target: open + minimal {pong:true} body (it's a timing target, not a data feed).
    [Fact]
    public async Task Ping_NoToken_PongBody()
    {
        HttpResponseMessage resp = await Client().GetAsync("/api/v1/ping");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"pong\":true", await resp.Content.ReadAsStringAsync());
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

    // --- /stream: viewer admits the endpoint (then the WS-required check 400s a plain GET) ---------
    [Fact]
    public async Task Viewer_PlainGetStream_400_NotAuthError()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Viewer)).GetAsync("/api/v1/stream");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // authz passed; not a WS upgrade
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- /stream WS: the browser bearer rides ?access_token= (can't set an Authorization header) ---
    [Fact]
    public async Task Stream_WebSocket_WithQueryToken_Connects()
    {
        string token = factory.AccessToken(AuthTier.Viewer);
        WebSocketClient ws = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, $"api/v1/stream?access_token={token}");
        WebSocket socket = await ws.ConnectAsync(uri, CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Stream_WebSocket_NoToken_HandshakeFails()
    {
        WebSocketClient ws = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, "api/v1/stream");
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(uri, CancellationToken.None));
    }
}
