using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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

    // Drive the CSRF state round-trip: GET /start mints the nonce (it rides both the returned
    // Location's `state=` and the Set-Cookie the client now holds), so the returned client can replay
    // a matching callback. HandleCookies defaults on, so the state cookie auto-rides the next request.
    private async Task<(HttpClient Client, string State)> BeginLogin()
    {
        HttpClient c = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        HttpResponseMessage start = await c.GetAsync("/auth/discord/start");
        string query = start.Headers.Location!.Query.TrimStart('?');
        string state = query.Split('&').First(kv => kv.StartsWith("state=")).Substring("state=".Length);
        return (c, state);
    }

    // --- /auth/discord/callback verdicts (each through a real /start CSRF round-trip) --------------
    [Fact]
    public async Task Callback_Authorized_200_MintsWorkingToken()
    {
        (HttpClient c, string state) = await BeginLogin();
        HttpResponseMessage resp = await c.GetAsync($"/auth/discord/callback?code=operator&state={state}");
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
        (HttpClient c, string state) = await BeginLogin();
        HttpResponseMessage resp = await c.GetAsync($"/auth/discord/callback?code=none&state={state}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("denied", (await Json(resp)).GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Callback_BadCode_401()
    {
        (HttpClient c, string state) = await BeginLogin();
        HttpResponseMessage resp = await c.GetAsync($"/auth/discord/callback?code=bad&state={state}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"login_required\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Callback_DiscordUnreachable_502()
    {
        (HttpClient c, string state) = await BeginLogin();
        HttpResponseMessage resp = await c.GetAsync($"/auth/discord/callback?code=boom&state={state}");
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Contains("\"code\":\"auth_provider_error\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Callback_MissingCode_400()
    {
        // State validates; the code is what's missing -> bad_request (NOT invalid_state).
        (HttpClient c, string state) = await BeginLogin();
        HttpResponseMessage resp = await c.GetAsync($"/auth/discord/callback?state={state}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- CSRF state gate: a mismatched or absent state is rejected before any Discord exchange ------
    [Fact]
    public async Task Callback_StateMismatch_400_InvalidState()
    {
        // The cookie is present (we did /start) but the echoed state is wrong -> forged callback.
        (HttpClient c, _) = await BeginLogin();
        HttpResponseMessage resp = await c.GetAsync("/auth/discord/callback?code=operator&state=deadbeefdeadbeef");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"invalid_state\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Callback_NoStateCookie_400_InvalidState()
    {
        // A client that never hit /start has no state cookie -> the callback can't be trusted.
        HttpResponseMessage resp = await factory.CreateClient()
            .GetAsync("/auth/discord/callback?code=operator&state=whatever");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"invalid_state\"", await resp.Content.ReadAsStringAsync());
    }

    // --- /auth/discord/callback — SPA fragment handoff (KGSM_API_AUTH_FRONTEND_URL set) -----------
    // With a frontend URL configured the callback 302s the browser back to the SPA carrying the outcome
    // in the URL FRAGMENT (never the query — tokens must not reach access logs or the Referer header),
    // instead of the JSON contract. The redirect target is the single configured URL (no open-redirect).
    private WebApplicationFactory<Program> FrontendFactory() =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["KGSM_API_AUTH_FRONTEND_URL"] = "https://panel.test" })));

    private static async Task<(HttpClient Client, string State)> BeginLoginOn(WebApplicationFactory<Program> f)
    {
        HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage start = await c.GetAsync("/auth/discord/start");
        string query = start.Headers.Location!.Query.TrimStart('?');
        string state = query.Split('&').First(kv => kv.StartsWith("state=")).Substring("state=".Length);
        return (c, state);
    }

    [Fact]
    public async Task Callback_FrontendConfigured_Authorized_302_TokensInFragment()
    {
        using WebApplicationFactory<Program> f = FrontendFactory();
        (HttpClient c, string state) = await BeginLoginOn(f);
        HttpResponseMessage resp = await c.GetAsync($"/auth/discord/callback?code=operator&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Uri loc = resp.Headers.Location!;
        Assert.StartsWith("https://panel.test", loc.ToString());
        // Tokens ride the FRAGMENT, not the query — the whole point (no leak to logs / Referer).
        Assert.Equal("", loc.Query);
        Assert.Contains("access=", loc.Fragment);
        Assert.Contains("refresh=", loc.Fragment);
    }

    [Fact]
    public async Task Callback_FrontendConfigured_Denied_302_ErrorInFragment()
    {
        using WebApplicationFactory<Program> f = FrontendFactory();
        (HttpClient c, string state) = await BeginLoginOn(f);
        HttpResponseMessage resp = await c.GetAsync($"/auth/discord/callback?code=none&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Uri loc = resp.Headers.Location!;
        Assert.StartsWith("https://panel.test", loc.ToString());
        Assert.Contains("error=denied", loc.Fragment);
        Assert.DoesNotContain("access=", loc.ToString());
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
