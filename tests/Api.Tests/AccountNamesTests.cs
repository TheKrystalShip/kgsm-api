using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>GET /api/v1/accounts/names</c>: the username behind an account id a surface showed, from this
/// node's replica, and nothing for an id it does not hold.
/// </summary>
public sealed class AccountNamesTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Client(Persona? persona)
    {
        HttpClient c = factory.CreateClient();
        if (persona is { } p)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(p));
        return c;
    }

    [Fact]
    public async Task NoToken_401()
    {
        HttpResponseMessage resp = await Client(null).GetAsync("/api/v1/accounts/names?id=local:usr_x");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task AknownId_IsNamed_AndAnUnknownOne_IsAbsent()
    {
        // Somebody holding nothing may ask: being shown an author is not an action.
        HttpClient c = Client(Persona.None);
        KgsmUser me = TestAuthority.For(factory.Services).AccountOf(TestIdentity.Identity)!;

        HttpResponseMessage resp = await c.GetAsync(
            $"/api/v1/accounts/names?id={Uri.EscapeDataString(me.UserId)}&id=local:usr_nobody");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement names = doc.RootElement.GetProperty("names");
        Assert.Equal(me.Username, names.GetProperty(me.UserId).GetString());
        Assert.False(names.TryGetProperty("local:usr_nobody", out _));
    }

    [Fact]
    public async Task TooManyIds_400()
    {
        string query = string.Join("&", Enumerable.Range(0, 51).Select(i => "id=local:usr_" + i));
        HttpResponseMessage resp = await Client(Persona.Reader).GetAsync("/api/v1/accounts/names?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
