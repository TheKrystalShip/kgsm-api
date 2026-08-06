using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M4·c Increment 7 (Group E #11) — <c>/me.recentLogins</c>, the honest login-history read off the
/// existing <c>auth.login</c> audit rows (<see cref="TheKrystalShip.Api.Services.Audit.AuditQueries.RecentByActionAsync"/>,
/// consumed by <c>MeController</c>). It complements, and is deliberately NOT the same surface as,
/// <c>GET /auth/sessions</c> (Increment 6, the live registry) — see <c>MeResponse</c>'s remarks.
/// <para>
/// Split into two classes (each its own <see cref="AuthTestFactory"/> — a fresh temp DB per class,
/// same pattern <c>SessionRegistryTests</c> relies on) rather than one class with two ordered test
/// methods: every fake identity shares the SAME underlying Discord user ("haru"), so a real callback
/// login in one test would contaminate a same-class "no prior login" assertion in another, and xUnit
/// does not guarantee method execution order within a class. Isolating at the class level (own DB)
/// makes both assertions order-independent by construction.
/// </para>
/// </summary>
public sealed class MeRecentLoginsTests_AfterRealLogin(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    // Drive the CSRF state round-trip (the AuthFlowTests/SessionRegistryTests shape), but ALSO set a
    // real User-Agent — the WebApplicationFactory HttpClient sends none by default (SessionRegistryTests
    // observed the session row's UA lands null for that reason), so recentLogins.device would always be
    // null unless a test explicitly supplies one.
    private async Task<(HttpClient Client, string State)> BeginLogin(string userAgent)
    {
        HttpClient c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        HttpResponseMessage start = await c.GetAsync("/auth/discord/start");
        string query = start.Headers.Location!.Query.TrimStart('?');
        string state = query.Split('&').First(kv => kv.StartsWith("state=")).Substring("state=".Length);
        return (c, state);
    }

    [Fact]
    public async Task Me_RecentLogins_AfterRealLogin_ReturnsEntryWithMatchingDevice()
    {
        const string ua = "KgsmWebTest/1.0";
        (HttpClient c, string state) = await BeginLogin(ua);
        HttpResponseMessage callback = await c.GetAsync($"/auth/discord/callback?code=viewer&state={state}");
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        string access = (await Json(callback)).GetProperty("token").GetString()!;

        HttpClient authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        HttpResponseMessage me = await authed.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        JsonElement body = await Json(me);
        JsonElement[] logins = body.GetProperty("recentLogins").EnumerateArray().ToArray();
        Assert.NotEmpty(logins);
        // Newest first (RecentByActionAsync orders by RowId DESC) — the login just performed is [0].
        Assert.Equal(ua, logins[0].GetProperty("device").GetString());
        Assert.True(logins[0].GetProperty("ts").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));
    }
}

public sealed class MeRecentLoginsTests_FreshActor(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Me_RecentLogins_FreshActorWithNoPriorLogin_Empty()
    {
        // factory.AccessToken mints a token + inserts the SessionEntry row directly (bypassing
        // /auth/discord/callback entirely), so it never writes an auth.login audit row for this
        // factory's fresh DB -> the actor honestly has no login history yet.
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(KgsmTier.Admin));
        HttpResponseMessage resp = await c.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty((await Json(resp)).GetProperty("recentLogins").EnumerateArray());
    }
}
