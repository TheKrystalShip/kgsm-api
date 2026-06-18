using System.Net;
using System.Net.Http.Headers;
using System.Text;

using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The M7 assistant turn relay (<c>POST /api/v1/assistant/turn</c>). The factory leaves the assistant
/// UNPROVISIONED (no base URL), so these prove the gates that run BEFORE any upstream call — auth, the
/// honest degrade-gracefully capability gate (absent ⇒ 404, never a 500), and prompt validation. The
/// happy-path stream (a live/stub assistant) is a smoke/live concern, like M2/M3's streaming halves.
/// </summary>
public sealed class AssistantRelayTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Client(string? token = null)
    {
        HttpClient c = factory.CreateClient();
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static HttpRequestMessage Turn(string json) =>
        new(HttpMethod.Post, "/api/v1/assistant/turn")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task NoToken_401()
    {
        HttpResponseMessage resp = await Client().SendAsync(Turn("""{"prompt":"hi"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoneTier_403()
    {
        // Authenticated but below viewer → forbidden (the load-bearing 401-vs-403 split).
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.None)).SendAsync(Turn("""{"prompt":"hi"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_AssistantAbsent_404()
    {
        // Viewer clears authz; the unprovisioned assistant degrades to an honest 404, never a 500.
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Viewer)).SendAsync(Turn("""{"prompt":"hi"}"""));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Viewer_EmptyPrompt_400()
    {
        // Prompt validation precedes the capability gate — a whitespace prompt is a 400 envelope.
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Viewer)).SendAsync(Turn("""{"prompt":"   "}"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }
}
