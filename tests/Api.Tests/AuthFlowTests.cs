using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The login flow end-to-end through the FAKE Discord seam: the callback verdict (ok / denied /
/// invalid / upstream-error), the stateless refresh rotation, and the profile-snapshot session.
/// The real discord.com exchange is the M4·b live half — everything here is deterministic.
/// </summary>
public sealed class AuthFlowTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    // --- /auth/discord/callback verdicts -----------------------------------------------------------
    [Fact]
    public async Task Callback_Authorized_200_MintsWorkingToken()
    {
        HttpResponseMessage resp = await factory.CreateClient().GetAsync("/auth/discord/callback?code=operator");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonElement body = await Json(resp);
        Assert.Equal("ok", body.GetProperty("verdict").GetString());
        Assert.Equal("operator", body.GetProperty("tier").GetString());
        Assert.Equal("discord:198772043", body.GetProperty("userId").GetString());

        // The minted access token actually authorizes an operator action (past the gate -> 404 no server).
        string token = body.GetProperty("token").GetString()!;
        HttpClient authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage cmd = await authed.PostAsync("/api/v1/servers/nope/commands",
            new StringContent("""{"verb":"start"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, cmd.StatusCode);
    }

    [Fact]
    public async Task Callback_NoRole_403_Denied()
    {
        HttpResponseMessage resp = await factory.CreateClient().GetAsync("/auth/discord/callback?code=none");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("denied", (await Json(resp)).GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Callback_BadCode_401()
    {
        HttpResponseMessage resp = await factory.CreateClient().GetAsync("/auth/discord/callback?code=bad");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"login_required\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Callback_DiscordUnreachable_502()
    {
        HttpResponseMessage resp = await factory.CreateClient().GetAsync("/auth/discord/callback?code=boom");
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Contains("\"code\":\"auth_provider_error\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Callback_MissingCode_400()
    {
        HttpResponseMessage resp = await factory.CreateClient().GetAsync("/auth/discord/callback");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // --- /auth/session — the login-time profile snapshot behind the bearer -------------------------
    [Fact]
    public async Task Session_WithBearer_ReturnsProfileSnapshot()
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(AuthTier.Viewer));
        HttpResponseMessage resp = await c.GetAsync("/auth/session");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonElement body = await Json(resp);
        Assert.Equal("discord:198772043", body.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("haru", body.GetProperty("user").GetProperty("username").GetString());
        Assert.Contains("identify", body.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Session_NoBearer_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/auth/session")).StatusCode);

    // --- /auth/session/refresh — stateless rotation from a refresh token, no Discord round-trip ----
    [Fact]
    public async Task Refresh_FromRefreshToken_MintsUsableAccess()
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.RefreshToken(AuthTier.Operator));
        HttpResponseMessage resp = await c.PostAsync("/auth/session/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string access = (await Json(resp)).GetProperty("token").GetString()!;

        // The rotated access token carries the operator tier through to a protected mutation.
        HttpClient authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        HttpResponseMessage cmd = await authed.PostAsync("/api/v1/servers/nope/commands",
            new StringContent("""{"verb":"start"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, cmd.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithAccessToken_Rejected_401()
    {
        // An access token is not a refresh token — refresh must reject it.
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(AuthTier.Operator));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/auth/session/refresh", content: null)).StatusCode);
    }

    [Fact]
    public async Task Refresh_NoToken_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync("/auth/session/refresh", content: null)).StatusCode);

    // --- /auth/discord/start — the authorize bounce (302, no auto-follow to the IdP) ---------------
    [Fact]
    public async Task Start_Redirects_ToAuthorizeUrl()
    {
        HttpClient c = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        HttpResponseMessage resp = await c.GetAsync("/auth/discord/start");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith("https://discord.test/authorize", resp.Headers.Location!.ToString());
    }

    // --- /auth/logout — stateless, idempotent 204 --------------------------------------------------
    [Fact]
    public async Task Logout_204() =>
        Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().PostAsync("/auth/logout", content: null)).StatusCode);
}
