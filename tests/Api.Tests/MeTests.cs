using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using TheKrystalShip.Auth;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>GET /me</c> — the caller's identity and scopes projected from the bearer, and the status of the
/// account behind it read from the replica, proven in-process against the real JwtBearer pipeline. The
/// load-bearing honesty facts: <c>/me</c> is <c>[Authorize]</c> (any authenticated caller) rather than
/// gated on an action, so somebody holding nothing reaches it and honestly reads their own status; no
/// bearer → the frozen <c>401</c> envelope.
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
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.Reader)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        JsonElement body = await Json(resp);
        JsonElement user = body.GetProperty("user");
        Assert.Equal("discord:198772043", user.GetProperty("id").GetString());   // the prefixed handle
        Assert.Equal("haru", user.GetProperty("username").GetString());
        Assert.Equal("haru", user.GetProperty("display").GetString());
        Assert.Equal("https://cdn.discordapp.com/avatars/198772043/abc.png",
            user.GetProperty("avatarUrl").GetString());
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("tier", out _));
        Assert.Equal(
            new[] { "identify", "guilds" },
            body.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToArray());
    }

    // /me is [Authorize], gated on no action: somebody whose account holds nothing reaches it and reads
    // who they are — the "who am I / why am I refused elsewhere" surface.
    [Fact]
    public async Task HoldingNothing_200_StillReadsTheirOwnAccount()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.None)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("active", (await Json(resp)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task PendingAccount_200_ReportsPending()
    {
        KgsmIdentity waiting = TestIdentity.IdentityFor("me-pending");
        string token = factory.AccessTokenFor(waiting, Persona.None, UserStatus.Pending);

        HttpResponseMessage resp = await Client(token).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("pending", (await Json(resp)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Stranger_200_ReportsUnknown()
    {
        string token = AuthTestFactory.MintAccess(TestIdentity.IdentityFor("me-stranger"));

        HttpResponseMessage resp = await Client(token).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("unknown", (await Json(resp)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task RefreshToken_AsAccessBearer_401()
    {
        // A refresh token must never authenticate a protected call (the pipeline rejects tkn != access).
        HttpResponseMessage resp = await Client(factory.RefreshToken(Persona.Owner)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WrongSignature_401()
    {
        string forged = TestTokens.MintByAnUnpublishedAnchor();
        HttpResponseMessage resp = await Client(forged).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
