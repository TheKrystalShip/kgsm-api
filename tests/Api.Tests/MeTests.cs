using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M8 coverage for <c>GET /me</c> — the caller's identity + tier + scopes projected from the bearer
/// claims, proven in-process against the real JwtBearer pipeline (Discord faked). The load-bearing
/// honesty facts: the <c>tier</c> is reflected from the bearer verbatim (never a default/elevated grant);
/// <c>/me</c> is <c>[Authorize]</c> (any authenticated caller), NOT viewer-gated, so a <c>none</c>-tier
/// caller reaches it and honestly reads <c>"none"</c> (the who-am-I surface, contrast the viewer-gated
/// reads that 403 a none-tier); no bearer → the frozen <c>401</c> envelope.
/// </summary>
public sealed class MeTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Client(string? token = null)
    {
        HttpClient c = factory.CreateClient();
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task NoToken_401_Envelope()
    {
        HttpResponseMessage resp = await Client().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Viewer_200_ProjectsTheIdentitySnapshotAndScopes()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Viewer)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        JsonElement body = await Json(resp);
        JsonElement user = body.GetProperty("user");
        Assert.Equal("discord:198772043", user.GetProperty("id").GetString());   // the prefixed handle
        Assert.Equal("haru", user.GetProperty("username").GetString());
        Assert.Equal("haru", user.GetProperty("display").GetString());
        Assert.Equal("https://cdn.discordapp.com/avatars/198772043/abc.png",
            user.GetProperty("avatarUrl").GetString());
        Assert.Equal("viewer", body.GetProperty("tier").GetString());
        Assert.Equal(
            new[] { "identify", "guilds" },
            body.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToArray());
    }

    // The tier is the honest delta /me adds over /auth/session — reflected verbatim from the bearer.
    [Theory]
    [InlineData(AuthTier.Viewer, "viewer")]
    [InlineData(AuthTier.Operator, "operator")]
    [InlineData(AuthTier.Admin, "admin")]
    public async Task Tier_ReflectedVerbatim(AuthTier tier, string wire)
    {
        JsonElement body = await Json(await Client(factory.AccessToken(tier)).GetAsync("/api/v1/me"));
        Assert.Equal(wire, body.GetProperty("tier").GetString());
    }

    // /me is [Authorize], NOT viewer-gated: a none-tier caller (verified identity, no role here) reaches it
    // and honestly reads tier:"none" — the "who am I / why am I 403 elsewhere" surface. Contrast the
    // viewer-gated reads, which 403 a none-tier (TierMatrixTests.NoneTier_Reads_403).
    [Fact]
    public async Task NoneTier_200_HonestlyReportsNone()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.None)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("none", (await Json(resp)).GetProperty("tier").GetString());
    }

    [Fact]
    public async Task RefreshToken_AsAccessBearer_401()
    {
        // A refresh token must never authenticate a protected call (the pipeline rejects tkn != access).
        HttpResponseMessage resp = await Client(factory.RefreshToken(AuthTier.Admin)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WrongSignature_401()
    {
        string forged = TestTokens.MintAccessWithKey(
            "a-totally-different-signing-key", AuthTestFactory.HostId, AuthTier.Admin);
        HttpResponseMessage resp = await Client(forged).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
