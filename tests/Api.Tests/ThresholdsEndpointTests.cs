using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>/hosts/{id}/thresholds</c> — the editor onto kgsm-monitor's threshold policy. This API owns none of
/// the policy and none of its validation, so what is pinned here is the gate (reading configuration is
/// operator, changing what the fleet alerts on is admin), the host scoping, and the honest degrade when
/// there is no monitor to ask.
/// </summary>
public sealed class ThresholdsEndpointTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private const string Host = AuthTestFactory.HostId;

    // --- the gate ------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_NoToken_401()
    {
        HttpResponseMessage r = await factory.CreateClient().GetAsync($"/api/v1/hosts/{Host}/thresholds");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Get_Viewer_403()
    {
        // Authenticated but below the bar — a different answer from "who are you", and the split this
        // suite exists to keep honest.
        HttpResponseMessage r = await Client(KgsmTier.Viewer).GetAsync($"/api/v1/hosts/{Host}/thresholds");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Put_Operator_403()
    {
        // Reading the thresholds is configuration; changing them decides what the whole fleet alerts on,
        // and an operator who can read them still cannot silence a machine.
        HttpResponseMessage r = await Client(KgsmTier.Operator)
            .PutAsync($"/api/v1/hosts/{Host}/thresholds", Body("""{"rules":[]}"""));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Delete_Operator_403()
    {
        HttpResponseMessage r = await Client(KgsmTier.Operator).DeleteAsync($"/api/v1/hosts/{Host}/thresholds");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // --- host scoping --------------------------------------------------------------------------------

    [Fact]
    public async Task UnknownHost_404()
    {
        // Per-host API: the only valid id is this host, and asking about another one is a 404 rather than
        // an attempt to relay somewhere.
        HttpResponseMessage r = await Client(KgsmTier.Operator).GetAsync("/api/v1/hosts/somewhere-else/thresholds");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // --- degrade -------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_NoMonitor_503_WithTheFrozenEnvelope()
    {
        // The test factory leaves the monitor unprovisioned. "The policy could not be read" is not the same
        // as "this host watches nothing", so it degrades rather than answering with an empty rule set.
        HttpResponseMessage r = await Client(KgsmTier.Operator).GetAsync($"/api/v1/hosts/{Host}/thresholds");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.Equal("metrics_unavailable", body.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Put_NoMonitor_503_AndDoesNotClaimSuccess()
    {
        HttpResponseMessage r = await Client(KgsmTier.Admin)
            .PutAsync($"/api/v1/hosts/{Host}/thresholds", Body("""{"rules":[]}"""));

        // Never a 2xx for a policy that reached nothing — an operator told their change landed when it did
        // not is the one outcome worth failing loudly over.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("metrics_unavailable", (await Json(r)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_MalformedBody_StillReachesTheMonitorRatherThanBeingParsedHere()
    {
        // This API deliberately does not validate a policy — the monitor owns the rules. A body it cannot
        // read is therefore the monitor's to refuse, and with no monitor the answer is the same 503 as any
        // other unreachable write, NOT a 400 invented here.
        HttpResponseMessage r = await Client(KgsmTier.Admin)
            .PutAsync($"/api/v1/hosts/{Host}/thresholds", Body("{ not json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    // --- helpers -------------------------------------------------------------------------------------

    private HttpClient Client(KgsmTier tier)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(tier));
        return c;
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
}
