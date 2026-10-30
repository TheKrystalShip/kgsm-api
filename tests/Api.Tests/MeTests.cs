using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TheKrystalShip.KGSM.Sessions.Testing;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>GET /me</c> — the caller's identity projected from the access token, proven in-process against the
/// real pipeline. The load-bearing honesty facts: <c>/me</c> is <c>[Authorize]</c> (any authenticated
/// caller) rather than gated on an action, so somebody holding nothing reaches it; no bearer → the frozen
/// <c>401</c> envelope; and a token the provider did not sign, or that is for another audience, is refused.
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
    public async Task Reader_200_ProjectsTheTokensIdentity()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.Reader)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        JsonElement body = await Json(resp);
        JsonElement user = body.GetProperty("user");
        Assert.Equal(TestIdentity.Person.Subject, user.GetProperty("id").GetString());
        Assert.Equal(TestIdentity.Person.Name, user.GetProperty("username").GetString());
        Assert.Equal(TestIdentity.Person.Name, user.GetProperty("display").GetString());
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("scopes").EnumerateArray());
        Assert.Empty(body.GetProperty("recentLogins").EnumerateArray());
    }

    // /me is [Authorize], gated on no action: somebody whose token lists nothing reaches it and reads who
    // they are — the "who am I / why am I refused elsewhere" surface.
    [Fact]
    public async Task HoldingNothing_200_StillReadsTheirOwnIdentity()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.None)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("active", (await Json(resp)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ASecondPerson_IsNamedByTheirOwnToken()
    {
        TestPerson other = TestIdentity.PersonFor("me-other");

        HttpResponseMessage resp = await Client(factory.AccessTokenFor(other, Persona.None)).GetAsync("/api/v1/me");

        Assert.Equal(other.Subject, (await Json(resp)).GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task WrongSignature_401()
    {
        string forged = AuthTestFactory.Provider.Mint(
            TestIdentity.Person.Subject, actions: Personas.AllActions, signedBy: new TestIssuer());

        HttpResponseMessage resp = await Client(forged).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task AnotherAudience_401()
    {
        string token = AuthTestFactory.Provider.Mint(TestIdentity.Person.Subject, audience: "someone-else");

        HttpResponseMessage resp = await Client(token).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task AnExpiredToken_401()
    {
        string token = factory.AccessTokenWith(TestIdentity.Person, [], lifetime: TimeSpan.FromMinutes(-5));

        HttpResponseMessage resp = await Client(token).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
