using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Authority resolved on every request from the account store, rather than read off the token it was
/// minted into.
/// </summary>
/// <remarks>
/// This is what makes disable, demote and revoke one mechanism instead of three, and what stops this
/// API and the assistant beside it from disagreeing about the same person for the life of a token.
/// The tests below are the three answers that have to stay distinct: a lower tier, a closed door, and
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

    private static Task<HttpResponseMessage> OperatorAction(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/servers/nope/commands", new { verb = "start" });

    [Fact]
    public async Task ADemotionLandsOnTheNextRequestWithTheSameToken()
    {
        // The token still says operator and will keep saying so until it expires. It is not what the
        // gate reads, which is the entire point.
        string token = factory.AccessToken(KgsmTier.Operator);
        using HttpClient client = Bearing(factory, token);

        // 404 = past the gate, no such server. 403 = refused by the gate.
        Assert.Equal(HttpStatusCode.NotFound, (await OperatorAction(client)).StatusCode);

        factory.SetAccount(FakeDiscordResolver.Identity, KgsmTier.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, (await OperatorAction(client)).StatusCode);
    }

    [Fact]
    public async Task APromotionLandsOnTheNextRequestToo()
    {
        string token = factory.AccessToken(KgsmTier.Viewer);
        using HttpClient client = Bearing(factory, token);
        Assert.Equal(HttpStatusCode.Forbidden, (await OperatorAction(client)).StatusCode);

        factory.SetAccount(FakeDiscordResolver.Identity, KgsmTier.Operator);

        Assert.Equal(HttpStatusCode.NotFound, (await OperatorAction(client)).StatusCode);
    }

    [Fact]
    public async Task DisablingAnAccountEndsItsLiveSessionsRatherThanLoweringThem()
    {
        // A disabled account is a door closing, not a demotion. Left merely tierless it would keep
        // reading its own profile and holding a stream open.
        string token = factory.AccessToken(KgsmTier.Admin);
        using HttpClient client = Bearing(factory, token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        factory.SetAccount(FakeDiscordResolver.Identity, KgsmTier.Admin, UserStatus.Disabled);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task AnAccountAwaitingApprovalHoldsNothingAndSaysSo()
    {
        // `none` is two different facts, and the panel owes them different sentences: one person is
        // being asked to wait, the other is being told this is not their host.
        string token = factory.AccessToken(KgsmTier.Admin);
        using HttpClient client = Bearing(factory, token);
        factory.SetAccount(FakeDiscordResolver.Identity, KgsmTier.Admin, UserStatus.Pending);

        HttpResponseMessage me = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        MeResponse body = (await me.Content.ReadFromJsonAsync<MeResponse>())!;
        Assert.Equal(KgsmTiers.None, body.Tier);
        Assert.Equal(UserStatuses.Pending, body.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await OperatorAction(client)).StatusCode);
    }

    [Fact]
    public async Task AnIdentityWithNoAccountKeepsItsSessionAndHoldsNothing()
    {
        // A stranger is a real, measured answer — unlike a disabled account, nothing has been taken
        // from them, so their session stands and every gate refuses it.
        string token = factory.AccessToken(KgsmTier.Admin);
        using HttpClient client = Bearing(factory, token);
        KgsmUser account = factory.AccountOf(FakeDiscordResolver.Identity)!;
        await factory.Services.GetRequiredService<UserDirectory>().Store.DeleteAsync(account.UserId);

        HttpResponseMessage me = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        MeResponse body = (await me.Content.ReadFromJsonAsync<MeResponse>())!;
        Assert.Equal(KgsmTiers.None, body.Tier);
        Assert.Equal("unknown", body.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await OperatorAction(client)).StatusCode);
    }

    [Fact]
    public async Task AnUnreachableAccountStoreIs502AndNeverASilentGrantOrA401()
    {
        // The failure this whole design has to get right. A 401 would send the browser back to a
        // sign-in that reads the same file and fails the same way; trusting the token's own tier
        // would let a demoted admin stay one for as long as the outage lasts. Neither: the host says
        // it cannot answer.
        using WebApplicationFactory<Program> broken = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    // A path under a file, so the directory can never be created.
                    ["Api:UsersDbPath"] = "/proc/version/nope/users.db",
                })));

        string token = AuthTestFactory.MintTokenWithRow(broken.Services, KgsmTier.Admin, access: true);
        using HttpClient client = Bearing(broken, token);

        HttpResponseMessage resp = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        JsonElement error = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error");
        Assert.Equal("authority_unavailable", error.GetProperty("code").GetString());
    }

    // Resolving per request costs a lookup per request, so answers are cached, and that cache is the
    // only staleness left in the model. It has two halves — an answer is reused while it is held, and
    // it is not held forever — and each is proved against a TTL chosen so the *other* half cannot
    // decide the outcome. A window measured in a fraction of a second would make both tests a race
    // against whatever else this machine is running.

    [Fact]
    public async Task ACachedAnswerIsReusedSoAChangeMadeElsewhereIsNotSeenPerRequest()
    {
        // An hour-long window: no request sequence can outrun it, so what this asserts is that the
        // second request read the first one's answer rather than the store.
        using WebApplicationFactory<Program> cached = CachedFor(TimeSpan.FromHours(1));

        using HttpClient client = Bearing(
            cached, AuthTestFactory.MintTokenWithRow(cached.Services, KgsmTier.Operator, access: true));
        Assert.Equal(HttpStatusCode.NotFound, (await OperatorAction(client)).StatusCode);

        await DemoteInTheStoreAsync(cached);

        Assert.Equal(HttpStatusCode.NotFound, (await OperatorAction(client)).StatusCode);
    }

    [Fact]
    public async Task ACachedAnswerAgesOutSoTheTtlBoundsTheDemotionLag()
    {
        // The other half, and the one that makes the TTL a lag rather than a lifetime: the entry the
        // first request wrote expires on its own. A one-second TTL polled for the flip asserts that it
        // happens without asserting when — the entry either ages out or it never does, and only the
        // second is a defect.
        using WebApplicationFactory<Program> cached = CachedFor(TimeSpan.FromSeconds(1));

        using HttpClient client = Bearing(
            cached, AuthTestFactory.MintTokenWithRow(cached.Services, KgsmTier.Operator, access: true));
        Assert.Equal(HttpStatusCode.NotFound, (await OperatorAction(client)).StatusCode);

        await DemoteInTheStoreAsync(cached);

        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        HttpStatusCode status;
        while ((status = (await OperatorAction(client)).StatusCode) != HttpStatusCode.Forbidden
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    /// <summary>A host holding its own account store, resolving authority against a cache of
    /// <paramref name="ttl"/>.</summary>
    private WebApplicationFactory<Program> CachedFor(TimeSpan ttl) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Api:UsersDbPath"] =
                        Path.Combine(Path.GetTempPath(), $"kgsm-api-ttl-users-{Guid.NewGuid():N}.db"),
                    ["Api:AuthorityCacheSeconds"] =
                        ((int)ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                })));

    /// <summary>Lower the account's tier straight in the store, so nothing drops the cache on the way
    /// past — the shape of a change made on another surface sharing this host.</summary>
    private static async Task DemoteInTheStoreAsync(WebApplicationFactory<Program> host)
    {
        var users = host.Services.GetRequiredService<UserDirectory>();
        KgsmUser account = (await users.Store.FindByCredentialAsync(FakeDiscordResolver.Identity.Handle))!;
        await users.Store.UpdateAsync(account with { Tier = KgsmTier.Viewer });
    }
}
