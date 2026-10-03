using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using TheKrystalShip.Api.Contracts;

using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Access evaluated on every request from this node's replica, rather than read off the token.
/// </summary>
/// <remarks>
/// This is what makes disable, narrowing and revoking one mechanism instead of three, and what stops this
/// API and the assistant beside it from disagreeing about the same person for the life of a token. The
/// tests below are the answers that have to stay distinct: an action no longer held, a closed door, and
/// a question that could not be asked.
/// </remarks>
public sealed class LiveAuthorityTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static HttpClient Bearing(WebApplicationFactory<Program> f, string token)
    {
        HttpClient client = f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> StartAServer(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/servers/nope/commands", new { verb = "start" });

    [Fact]
    public async Task LosingAnActionLandsOnTheNextRequestWithTheSameToken()
    {
        // The token names who and nothing else; what the gate reads is the replica.
        string token = factory.AccessToken(Persona.Runner);
        using HttpClient client = Bearing(factory, token);

        // 404 = past the gate, no such server. 403 = refused by the gate.
        Assert.Equal(HttpStatusCode.NotFound, (await StartAServer(client)).StatusCode);

        factory.SetAccount(TestIdentity.Identity, Persona.Reader);

        Assert.Equal(HttpStatusCode.Forbidden, (await StartAServer(client)).StatusCode);
    }

    [Fact]
    public async Task GainingAnActionLandsOnTheNextRequestToo()
    {
        string token = factory.AccessToken(Persona.Reader);
        using HttpClient client = Bearing(factory, token);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartAServer(client)).StatusCode);

        factory.SetAccount(TestIdentity.Identity, Persona.Runner);

        Assert.Equal(HttpStatusCode.NotFound, (await StartAServer(client)).StatusCode);
    }

    [Fact]
    public async Task DisablingAnAccountEndsItsLiveSessionsRatherThanNarrowingThem()
    {
        // A disabled account is a door closing. Left merely holding nothing it would keep reading its own
        // profile and holding a stream open.
        string token = factory.AccessToken(Persona.Owner);
        using HttpClient client = Bearing(factory, token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        factory.SetAccount(TestIdentity.Identity, Persona.Owner, UserStatus.Disabled);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task AnAccountAwaitingApprovalHoldsNothingAndSaysSo()
    {
        // Holding nothing is two different facts, and the panel owes them different sentences: one
        // person is being asked to wait, the other is being told this is not their host.
        string token = factory.AccessToken(Persona.Owner);
        using HttpClient client = Bearing(factory, token);
        factory.SetAccount(TestIdentity.Identity, Persona.Owner, UserStatus.Pending);

        HttpResponseMessage me = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        MeResponse body = (await me.Content.ReadFromJsonAsync<MeResponse>())!;
        Assert.Equal(UserStatuses.Pending, body.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartAServer(client)).StatusCode);
    }

    [Fact]
    public async Task AnIdentityWithNoAccountKeepsItsSessionAndHoldsNothing()
    {
        // A stranger is a real, measured answer — unlike a disabled account, nothing has been taken
        // from them, so their session stands and every gate refuses it.
        string token = factory.AccessToken(Persona.Owner);
        using HttpClient client = Bearing(factory, token);
        AuthTestFactory.RemoveAccountOn(factory.Services, TestIdentity.Identity);

        HttpResponseMessage me = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        MeResponse body = (await me.Content.ReadFromJsonAsync<MeResponse>())!;
        Assert.Equal("unknown", body.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartAServer(client)).StatusCode);
    }

    [Fact]
    public async Task AnUnreadableReplicaIs502AndNeverASilentGrantOrA401()
    {
        // The failure this whole design has to get right. A 401 would send the browser back to a
        // sign-in that reads the same file and fails the same way; allowing anything would hand out
        // access nobody can vouch for for as long as the outage lasts. Neither: the host says it cannot
        // answer.
        using WebApplicationFactory<Program> broken = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    // A path under a file, so the directory can never be created.
                    ["Api:UsersDbPath"] = "/proc/version/nope/users.db",
                })));

        string token = AuthTestFactory.MintAccessOn(broken.Services, Persona.Owner);
        using HttpClient client = Bearing(broken, token);

        HttpResponseMessage resp = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        JsonElement error = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error");
        Assert.Equal("authority_unavailable", error.GetProperty("code").GetString());
    }
}
