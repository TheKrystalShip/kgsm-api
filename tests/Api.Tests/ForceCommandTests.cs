using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>force</c> on <c>POST /servers/{id}/commands</c> — the panel's route to KGSM's <c>--force</c>,
/// which overrides the engine's node-capacity check.
/// </summary>
/// <remarks>
/// Only <c>start</c> has a capacity check, so only <c>start</c> can override one. Asking on another verb
/// is refused rather than dropped: silently ignoring a safety override somebody deliberately set would
/// leave them believing they had bypassed something. <c>false</c> is the default and passes everywhere,
/// so a client that always sends the field is unaffected.
/// <para>
/// The gate is <b>operator</b>, not admin — the same tier that may start a server. The judgement it
/// takes is "this blueprint's declared figure is wrong for this server", which anyone running these
/// servers day to day is in a position to make.
/// </para>
/// </remarks>
public sealed class ForceCommandTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private HttpClient OperatorClient()
    {
        HttpClient authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.AccessToken(KgsmTier.Operator));
        return authed;
    }

    [Fact]
    public async Task Force_on_stop_is_refused()
    {
        HttpClient authed = OperatorClient();

        HttpResponseMessage resp = await authed.PostAsync("/api/v1/servers/nope/commands",
            Body("""{"verb":"stop","force":true}"""));

        // 400 ahead of the 404 the unknown server would otherwise produce: the request is malformed
        // regardless of which server it names.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("force applies to start only", body);
    }

    [Fact]
    public async Task Force_false_is_accepted_on_any_verb()
    {
        HttpClient authed = OperatorClient();

        // The protection is the default, so a client that always sends the field must not be punished
        // for it — this reaches the unknown-server 404, i.e. it got past every body check.
        HttpResponseMessage resp = await authed.PostAsync("/api/v1/servers/nope/commands",
            Body("""{"verb":"stop","force":false}"""));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Force_on_start_is_accepted_by_an_operator()
    {
        HttpClient authed = OperatorClient();

        // Operator, not admin. Reaching the 404 proves the body and the tier both passed; the server id
        // is what it then failed on.
        HttpResponseMessage resp = await authed.PostAsync("/api/v1/servers/nope/commands",
            Body("""{"verb":"start","force":true}"""));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task An_omitted_force_still_works()
    {
        HttpClient authed = OperatorClient();

        // The field is additive: every client that predates it keeps working, and keeps the protection.
        HttpResponseMessage resp = await authed.PostAsync("/api/v1/servers/nope/commands",
            Body("""{"verb":"start"}"""));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
