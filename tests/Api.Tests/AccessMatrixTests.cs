using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The authorization matrix: the caller holding an endpoint's action reaches it, the one without it is
/// refused — proven in-process against the real JwtBearer pipeline and action gates, with each persona a
/// real role in the replica. 401 (no/invalid bearer) vs 403 (authenticated, action not held) is the
/// load-bearing split.
/// </summary>
public sealed class AccessMatrixTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Client(string? token = null)
    {
        HttpClient c = factory.CreateClient();
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static HttpRequestMessage Command(string id) =>
        new(HttpMethod.Post, $"/api/v1/servers/{id}/commands")
        {
            Content = new StringContent("""{"verb":"start"}""", System.Text.Encoding.UTF8, "application/json"),
        };

    // --- No bearer -> 401 everywhere protected (and the open endpoints stay open) ------------------
    [Theory]
    [InlineData("/api/v1/hosts")]
    [InlineData("/api/v1/servers")]
    [InlineData("/api/v1/stream")]
    public async Task NoToken_Protected_401(string path)
    {
        HttpResponseMessage resp = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoToken_PostCommand_401()
    {
        HttpResponseMessage resp = await Client().SendAsync(Command("anything"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/v1")]
    public async Task OpenEndpoints_NoToken_200(string path)
    {
        HttpResponseMessage resp = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // --- A reader: reads pass, the mutation is forbidden (403, not 401 — it IS authenticated) --------
    [Fact]
    public async Task Viewer_Reads_200()
    {
        HttpClient c = Client(factory.AccessToken(Persona.Viewer));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/hosts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/servers")).StatusCode);
    }

    [Fact]
    public async Task Viewer_PostCommand_403()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.Viewer)).SendAsync(Command("anything"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("\"code\":\"forbidden\"", await resp.Content.ReadAsStringAsync());
    }

    // --- Holding kgsm:server.start clears the command gate (404 = no such server => authorization PASSED) --
    [Theory]
    [InlineData(Persona.Operator)]
    [InlineData(Persona.Owner)]
    public async Task HoldingTheVerb_PostCommand_PassesTheGate_404(Persona persona)
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(persona)).SendAsync(Command("no-such-server"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // got past the gate to the controller
    }

    [Fact]
    public async Task Owner_Reads_200() =>
        Assert.Equal(HttpStatusCode.OK, (await Client(factory.AccessToken(Persona.Owner)).GetAsync("/api/v1/hosts")).StatusCode);

    // --- An action nobody declared is an Owner's alone ----------------------------------------------
    [Fact]
    public async Task Operator_IntegrationsTheyHoldNoActionFor_403() =>
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client(factory.AccessToken(Persona.Operator)).GetAsync("/api/v1/integrations")).StatusCode);

    [Fact]
    public async Task Owner_Integrations_200() =>
        Assert.Equal(HttpStatusCode.OK,
            (await Client(factory.AccessToken(Persona.Owner)).GetAsync("/api/v1/integrations")).StatusCode);

    // --- Host logs (GET /hosts/{id}/logs): api:logs.read, apart from the audit feed's action (raw journald
    // can carry secrets). The reader shells real journalctl; content is irrelevant to the gate — a 200
    // (lines or honest-empty) means authorization passed, a 403 means it didn't.
    private static string LogsPath => $"/api/v1/hosts/{AuthTestFactory.HostId}/logs?limit=1";

    [Fact]
    public async Task NoToken_Logs_401()
    {
        HttpResponseMessage resp = await Client().GetAsync(LogsPath);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_Logs_403()
    {
        // A reader of the audit log is not thereby a reader of raw host logs.
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.Viewer)).GetAsync(LogsPath);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("\"code\":\"forbidden\"", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(Persona.Operator)]
    [InlineData(Persona.Owner)]
    public async Task HoldingLogsRead_Logs_200(Persona persona)
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(persona)).GetAsync(LogsPath);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // authorization passed -> the page (lines or empty)
    }

    [Fact]
    public async Task Operator_Logs_UnknownHost_404()
    {
        // Past the gate but a foreign host id -> 404, consistent with the rest of the hosts surface.
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.Operator))
            .GetAsync("/api/v1/hosts/not-this-host/logs?limit=1");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Operator_Logs_UnknownSource_400()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.Operator))
            .GetAsync($"/api/v1/hosts/{AuthTestFactory.HostId}/logs?source=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- Services board (GET /hosts/{id}/services): api:services.read — host internals (unit names / pids /
    // memory / enablement). The reader shells real systemctl; content is irrelevant to the gate — a 200
    // (real rows or honest 'unknown'/'not-installed') means authorization passed.
    private static string ServicesPath => $"/api/v1/hosts/{AuthTestFactory.HostId}/services";

    [Fact]
    public async Task NoToken_Services_401()
    {
        HttpResponseMessage resp = await Client().GetAsync(ServicesPath);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_Services_403()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.Viewer)).GetAsync(ServicesPath);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("\"code\":\"forbidden\"", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(Persona.Operator)]
    [InlineData(Persona.Owner)]
    public async Task HoldingServicesRead_Services_200(Persona persona)
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(persona)).GetAsync(ServicesPath);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // authorization passed -> the board (real or honest-unknown)
        // The catalog always yields a row per leaf, even on a host where none are installed (state:"not-installed").
        Assert.Contains("\"data\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Operator_Services_UnknownHost_404()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.Operator))
            .GetAsync("/api/v1/hosts/not-this-host/services");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // --- Hardening: holding nothing, refresh-as-access, wrong signature, garbage ---------------------
    [Fact]
    public async Task HoldingNothing_Reads_403()
    {
        // Authenticated (valid signature) but no role -> forbidden, never a default grant.
        HttpResponseMessage resp = await Client(factory.AccessToken(Persona.None)).GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_AsAccessBearer_401()
    {
        // A refresh token must never authenticate a protected call (OnTokenValidated rejects it).
        HttpResponseMessage resp = await Client(factory.RefreshToken(Persona.Owner)).GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WrongSignature_401()
    {
        string forged = TestTokens.MintByAnUnpublishedAnchor();
        HttpResponseMessage resp = await Client(forged).GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ASymmetricSessionIsRefusedWhateverItsKey_401()
    {
        // This node holds no key to sign or verify one with, so no symmetric token is a session here.
        string own = TestTokens.MintSymmetric("any-key-at-all-of-no-particular-length");
        HttpResponseMessage resp = await Client(own).GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ASessionNothingCouldEnd_401()
    {
        // Genuinely signed by the anchor, but with no sid: nothing could ever end it.
        string sidless = TestTokens.MintAnchorSignedWithoutSid();
        HttpResponseMessage resp = await Client(sidless).GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task GarbageToken_401()
    {
        HttpResponseMessage resp = await Client("not-a-jwt").GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // --- /stream: fetch-based SSE — the bearer rides a header. A plain GET with no topics is a valid 200
    // SSE connection. ---
    [Fact]
    public async Task Stream_Sse_WithBearerHeader_Connects()
    {
        string token = factory.AccessToken(Persona.Viewer);
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(Client(), "/api/v1/stream", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/event-stream", resp.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Stream_Sse_NoToken_Returns401()
    {
        HttpResponseMessage resp = await SseTestHelpers.OpenStream(Client(), "/api/v1/stream");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // --- the single most important regression test in this file: query-string tokens never authenticate ---
    // (a query token with no Authorization header must never authenticate — SSE sends the bearer only as
    // a header through the normal JwtBearer pipeline; protocol: src/Api/Realtime/CLAUDE.md).
    [Fact]
    public async Task Stream_Sse_QueryTokenIgnored()
    {
        string token = factory.AccessToken(Persona.Viewer);
        HttpResponseMessage resp = await SseTestHelpers.OpenStream(Client(), $"/api/v1/stream?access_token={token}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // --- a topic the reader may not see delivers nothing; never a 403 on the whole stream ---
    [Fact]
    public async Task Stream_Sse_TopicTheReaderMayNotSee_DeliversNothing()
    {
        string token = factory.AccessToken(Persona.Viewer);
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            Client(), $"/api/v1/stream?topics=hosts/{AuthTestFactory.HostId}/logs", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // connects — the refusal is silent, not a 403

        using SseFrameReader frames = await SseTestHelpers.Frames(resp);
        // A reader without api:logs.read is sent nothing on this topic within a short bounded wait,
        // proving silence rather than merely an untriggered event.
        JsonElement? frame = await frames.WaitForFrame(_ => true, TimeSpan.FromSeconds(1));
        Assert.Null(frame);
    }
}
