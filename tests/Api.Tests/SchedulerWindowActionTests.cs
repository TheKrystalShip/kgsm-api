using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>POST /hosts/{id}/services/scheduler/windows/{verb}</c> — the route, its gate, and what it refuses
/// before it dials anything.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler's control socket carries no identity, so the operator gate here is the only one there is
/// — which is why it runs before the socket is reached rather than being left to a daemon with no way to
/// apply it. These tests point the client at a socket that is not there, so everything up to the dial is
/// proven and the dial itself honestly fails.
/// </para>
/// <para>
/// ⚠ The route token is <c>verb</c> rather than <c>action</c>: MVC reserves that name for the action
/// method, and a segment spelled <c>{action}</c> binds to the method's own name so the route never
/// matches. A 404 from any of these is that mistake coming back.
/// </para>
/// </remarks>
public sealed class SchedulerWindowActionTests : IClassFixture<SchedulerWindowActionTests.SchedulerFactory>
{
    private readonly SchedulerFactory _factory;

    public SchedulerWindowActionTests(SchedulerFactory factory) => _factory = factory;

    [Theory]
    [InlineData("postpone")]
    [InlineData("skip")]
    [InlineData("run-now")]
    public async Task Each_verb_reaches_the_scheduler(string verb)
    {
        HttpResponseMessage resp = await Post(KgsmTier.Operator, verb,
            "{\"instance\":\"factorio-1\",\"window\":\"daily@05:00\"}");

        // The socket is not there, so the daemon's absence is what answers — never a 404, which would mean
        // the request never reached the controller at all.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("scheduler", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Viewer_403()
    {
        HttpResponseMessage resp = await Post(KgsmTier.Viewer, "postpone",
            "{\"instance\":\"factorio-1\",\"window\":\"daily@05:00\"}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task NoToken_401()
    {
        HttpResponseMessage resp = await Post(null, "postpone",
            "{\"instance\":\"factorio-1\",\"window\":\"daily@05:00\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task An_unknown_verb_400s_naming_the_ones_there_are()
    {
        HttpResponseMessage resp = await Post(KgsmTier.Operator, "nuke",
            "{\"instance\":\"factorio-1\",\"window\":\"daily@05:00\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("postpone", body);
        Assert.Contains("run-now", body);
    }

    [Fact]
    public async Task An_instruction_naming_no_window_400s()
    {
        // One instance holds several appointments; the daemon refuses rather than guessing, and so does
        // this — moving the wrong one is worse than refusing.
        HttpResponseMessage resp = await Post(KgsmTier.Operator, "postpone", "{\"instance\":\"factorio-1\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("window is required", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_postponement_past_twelve_hours_400s()
    {
        // Past that it is a schedule change, and a schedule change belongs in the instance's own config
        // where it survives a restart of the daemon.
        HttpResponseMessage resp = await Post(KgsmTier.Operator, "postpone",
            "{\"instance\":\"factorio-1\",\"window\":\"daily@05:00\",\"minutes\":900}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("720", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Another_host_404s()
    {
        HttpResponseMessage resp = await Client(KgsmTier.Operator).PostAsync(
            "/api/v1/hosts/somewhere-else/services/scheduler/windows/postpone",
            new StringContent("{\"instance\":\"factorio-1\",\"window\":\"daily@05:00\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private HttpClient Client(KgsmTier? tier)
    {
        HttpClient c = _factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.AccessToken(t));
        return c;
    }

    private Task<HttpResponseMessage> Post(KgsmTier? tier, string verb, string json) =>
        Client(tier).PostAsync($"/api/v1/hosts/{_factory.HostIdForTests}/services/scheduler/windows/{verb}",
            new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>
    /// A host wired to a scheduler whose sockets are not there. Registering the client is what the
    /// configured path decides, so this is enough to exercise the whole controller path.
    /// </summary>
    public sealed class SchedulerFactory : AuthTestFactory
    {
        public string HostIdForTests => "scheduler-test-host";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:HostId"] = HostIdForTests,
                    ["Api:SchedulerSocketPath"] = "/tmp/kgsm-api-tests-scheduler-status.sock",
                    ["Api:SchedulerControlSocketPath"] = "/tmp/kgsm-api-tests-scheduler-control.sock",
                });
            });
        }
    }
}
