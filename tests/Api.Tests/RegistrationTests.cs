using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// A factory with sign-up open, which the shipped default is not.
/// </summary>
/// <remarks>
/// Overridden through <c>ConfigureAppConfiguration</c> rather than <c>UseSetting</c>. A
/// <c>UseSetting</c> value is a host-builder setting, and <c>kgsm-api.settings.json</c> is a
/// configuration source added after it — so the file's shipped <c>false</c> would win and every case
/// here would read as a closed host. This appends a source after the base factory's, which is the
/// same way it pins the host id and the signing key.
/// </remarks>
public sealed class OpenRegistrationFactory : AuthTestFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Api:AllowSelfRegistration"] = "true" }));
    }
}

/// <summary>
/// <c>POST /auth/register</c> — creating an account for yourself, and everything that refuses one.
/// </summary>
/// <remarks>
/// The store is real (a temp file per factory, never the host's) for the same reason the local-login
/// suite uses one: most of what this endpoint promises is enforced below the controller — the
/// uniqueness of a username, the state an account lands in, and the tier it therefore holds. Against
/// a fake store these would assert the wiring and none of that.
/// </remarks>
public sealed class RegistrationTests(OpenRegistrationFactory factory) : IClassFixture<OpenRegistrationFactory>
{
    private const string GoodPassword = "correct-horse-battery-staple";

    private UserDirectory Users => factory.Services.GetRequiredService<UserDirectory>();

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static HttpContent Body(string? username, string? password, string? displayName = null) =>
        JsonContent.Create(new RegisterRequest(username, displayName, password));

    // ---- the happy path -------------------------------------------------------------------------

    [Fact]
    public async Task SigningUpYieldsARealSessionThatHoldsNothing()
    {
        HttpClient client = factory.CreateClient();
        string username = Unique("newcomer");

        HttpResponseMessage res = await client.PostAsync("/auth/register", Body(username, GoodPassword));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        LoginResult? result = await res.Content.ReadFromJsonAsync<LoginResult>();
        Assert.NotNull(result);
        // Proving who you are and being let in are two different things, and this is the gap.
        Assert.Equal(KgsmTiers.None, result!.Tier);
        Assert.Equal(UserStatuses.Pending, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.False(string.IsNullOrEmpty(result.Refresh));
    }

    /// <summary>
    /// The provenance the pending sweep reads. An account made by hand carries a granted tier and is
    /// spared forever; one that arrived on its own must expire, or the cap fills with a queue nobody
    /// can drain.
    /// </summary>
    [Fact]
    public async Task TheAccountItCreatesIsMarkedAsHavingArrivedOnItsOwn()
    {
        HttpClient client = factory.CreateClient();
        string username = Unique("derived");

        await client.PostAsync("/auth/register", Body(username, GoodPassword));

        KgsmUser? account = await Users.Store.FindByUsernameAsync(username);
        Assert.NotNull(account);
        Assert.Equal(TierSource.Derived, account!.TierSource);
        Assert.Equal(UserStatus.Pending, account.Status);
        Assert.Equal(KgsmTier.None, account.Tier);
    }

    [Fact]
    public async Task TheDisplayNameIsOptionalAndFallsBackToTheUsername()
    {
        HttpClient client = factory.CreateClient();
        string plain = Unique("plain");
        string named = Unique("named");

        await client.PostAsync("/auth/register", Body(plain, GoodPassword));
        await client.PostAsync("/auth/register", Body(named, GoodPassword, displayName: "Walter White"));

        Assert.Equal(plain, (await Users.Store.FindByUsernameAsync(plain))!.DisplayName);
        Assert.Equal("Walter White", (await Users.Store.FindByUsernameAsync(named))!.DisplayName);
    }

    [Fact]
    public async Task ThePasswordItSetsIsTheOneThatSignsInAfterwards()
    {
        HttpClient client = factory.CreateClient();
        string username = Unique("signsin");

        await client.PostAsync("/auth/register", Body(username, GoodPassword));

        HttpResponseMessage login = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(username, GoodPassword));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        LoginResult? result = await login.Content.ReadFromJsonAsync<LoginResult>();
        Assert.Equal(UserStatuses.Pending, result!.Status);
    }

    /// <summary>
    /// The whole point of minting a session for somebody who holds nothing: the panel can say what
    /// they are waiting for. <c>/me</c> is bare-authorized so a tierless caller can read it, and it
    /// reports the two facts that distinguish waiting from not belonging here.
    /// </summary>
    [Fact]
    public async Task TheSessionReachesMeAndSaysItIsWaiting()
    {
        HttpClient client = factory.CreateClient();
        string username = Unique("waiting");

        HttpResponseMessage res = await client.PostAsync("/auth/register", Body(username, GoodPassword));
        LoginResult token = (await res.Content.ReadFromJsonAsync<LoginResult>())!;

        client.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        MeResponse? me = await client.GetFromJsonAsync<MeResponse>("/api/v1/me");

        Assert.Equal(KgsmTiers.None, me!.Tier);
        Assert.Equal(UserStatuses.Pending, me.Status);
    }

    /// <summary>
    /// And nothing else — bar news about themselves. Every read on the host refuses a pending account,
    /// and the stream's per-topic gate leaves it holding <c>me</c> alone: the <c>servers</c> topic it
    /// asked for is dropped at connect, so the connection carries the approval when it comes and
    /// nothing before it.
    /// </summary>
    [Fact]
    public async Task TheSessionReachesItsOwnStandingAndNothingElse()
    {
        HttpClient client = factory.CreateClient();
        HttpResponseMessage res = await client.PostAsync("/auth/register", Body(Unique("nothing"), GoodPassword));
        LoginResult token = (await res.Content.ReadFromJsonAsync<LoginResult>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/servers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/hosts")).StatusCode);

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            client, "/api/v1/stream?topics=servers,me", token.Token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);

        using SseFrameReader frames = await SseTestHelpers.Frames(stream);
        Assert.Null(await frames.WaitForFrame(_ => true, TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Approving is an ordinary tier change, and the account picks it up on the next request rather
    /// than needing a new session — which is what lets a waiting browser find out by polling.
    /// </summary>
    [Fact]
    public async Task ApprovingTheAccountIsVisibleOnTheSessionItAlreadyHolds()
    {
        HttpClient client = factory.CreateClient();
        string username = Unique("approved");
        HttpResponseMessage res = await client.PostAsync("/auth/register", Body(username, GoodPassword));
        LoginResult token = (await res.Content.ReadFromJsonAsync<LoginResult>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);

        Assert.Equal(KgsmTiers.None, (await client.GetFromJsonAsync<MeResponse>("/api/v1/me"))!.Tier);

        KgsmUser account = (await Users.Store.FindByUsernameAsync(username))!;
        using HttpClient admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.AccessToken(KgsmTier.Admin));
        HttpResponseMessage patch = await admin.PatchAsJsonAsync(
            $"/auth/users/{account.UserId}",
            new UpdateUserRequest(null, null, KgsmTiers.Viewer, UserStatuses.Active));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        MeResponse? after = await client.GetFromJsonAsync<MeResponse>("/api/v1/me");
        Assert.Equal(KgsmTiers.Viewer, after!.Tier);
        Assert.Equal(UserStatuses.Active, after.Status);
    }

    // ---- refusals -------------------------------------------------------------------------------

    [Fact]
    public async Task AUsernameAlreadyTakenIsAConflict()
    {
        HttpClient client = factory.CreateClient();
        string username = Unique("twice");

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsync("/auth/register", Body(username, GoodPassword))).StatusCode);

        HttpResponseMessage again = await client.PostAsync("/auth/register", Body(username, GoodPassword));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("username_taken", await CodeOf(again));
    }

    /// <summary>
    /// The client checks the same shape to say so without a round trip, and none of that is trusted:
    /// this is the answer whatever the client did or did not do.
    /// </summary>
    [Theory]
    [InlineData("ab")]                                  // under the floor
    [InlineData("-leading-separator")]                   // must start alphanumeric
    [InlineData("has spaces")]
    [InlineData("wrong@charset")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnUnusableUsernameIsRefusedWithAReason(string? username)
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage res = await client.PostAsync("/auth/register", Body(username, GoodPassword));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("bad_request", await CodeOf(res));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("elevenchars")]   // 11 — one under
    [InlineData("")]
    [InlineData(null)]
    public async Task APasswordUnderTheFloorIsRefused(string? password)
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage res = await client.PostAsync("/auth/register", Body(Unique("weak"), password));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("bad_request", await CodeOf(res));
    }

    [Fact]
    public async Task ExactlyTheFloorIsAccepted()
    {
        HttpClient client = factory.CreateClient();
        string twelve = new('x', Passwords.MinLength);

        HttpResponseMessage res = await client.PostAsync("/auth/register", Body(Unique("floor"), twelve));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    /// <summary>A refused sign-up leaves nothing behind — no half-made account to collide with next time.</summary>
    [Fact]
    public async Task ARefusedSignUpCreatesNoAccount()
    {
        HttpClient client = factory.CreateClient();
        string username = Unique("refused");

        await client.PostAsync("/auth/register", Body(username, "tooshort"));

        Assert.Null(await Users.Store.FindByUsernameAsync(username));
    }

    [Fact]
    public async Task SigningUpNeedsNoBearerOfItsOwn()
    {
        HttpClient client = factory.CreateClient();
        Assert.Null(client.DefaultRequestHeaders.Authorization);

        HttpResponseMessage res = await client.PostAsync("/auth/register", Body(Unique("anon"), GoodPassword));

        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>What is configurable is who may join the queue — a caller may not name their own tier.</summary>
    [Fact]
    public async Task ACallerCannotAskForATier()
    {
        HttpClient client = factory.CreateClient();
        string username = Unique("greedy");

        // The extra fields have nowhere to bind; the point is that they change nothing.
        HttpResponseMessage res = await client.PostAsJsonAsync("/auth/register", new
        {
            username,
            password = GoodPassword,
            tier = "admin",
            status = "active",
        });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        KgsmUser account = (await Users.Store.FindByUsernameAsync(username))!;
        Assert.Equal(KgsmTier.None, account.Tier);
        Assert.Equal(UserStatus.Pending, account.Status);
    }

    internal static async Task<string?> CodeOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<ErrorEnvelope>())?.Error.Code;
}

/// <summary>
/// The shipped default: a host takes no accounts people make for themselves until it is told to.
/// </summary>
public sealed class RegistrationClosedTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    /// <summary>
    /// Refused before anything is read or written, so a closed host cannot be probed for whether a
    /// username is free.
    /// </summary>
    [Fact]
    public async Task AHostThatIsNotTakingSignUpsSaysSo()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage res = await client.PostAsync("/auth/register",
            JsonContent.Create(new RegisterRequest("newcomer", null, "correct-horse-battery-staple")));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("registration_closed", await RegistrationTests.CodeOf(res));
    }

    /// <summary>
    /// The login page reads this to decide whether to draw a sign-up form at all, so it has to be
    /// answerable with no session.
    /// </summary>
    [Fact]
    public async Task TheProvidersAnswerNamesWhetherSignUpIsOpen()
    {
        HttpClient client = factory.CreateClient();

        AuthProvidersResponse? closed = await client.GetFromJsonAsync<AuthProvidersResponse>("/auth/providers");
        Assert.False(closed!.Registration);

        using OpenRegistrationFactory open = new();
        using HttpClient openClient = open.CreateClient();
        AuthProvidersResponse? opened = await openClient.GetFromJsonAsync<AuthProvidersResponse>("/auth/providers");
        Assert.True(opened!.Registration);
    }
}

/// <summary>A factory whose anonymous limiter is set low enough to meet.</summary>
public sealed class ThrottledFactory : AuthTestFactory
{
    public const int Limit = 3;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Api:AllowSelfRegistration"] = "true",
                ["Api:AnonymousRateLimit"] = Limit.ToString(),
            }));
    }
}

/// <summary>
/// The per-caller throttle on the two anonymous doors that touch credentials.
/// </summary>
/// <remarks>
/// Separate from the account store's lockout and both stay: lockout is keyed on the account being
/// guessed at, which protects one person and does nothing about one password sprayed across many
/// usernames, or about creating accounts, which has no account to lock yet.
/// </remarks>
public sealed class AnonymousRateLimitTests(ThrottledFactory factory) : IClassFixture<ThrottledFactory>
{
    [Fact]
    public async Task EnoughAttemptsFromOneCallerAreRefusedWithHowLongToWait()
    {
        HttpClient client = factory.CreateClient();
        HttpResponseMessage? refused = null;

        // One past the limit. Every one of these is itself a refusal (nonexistent account), which is
        // the point: what the limiter counts is attempts, not failures to guess one password.
        for (int i = 0; i <= ThrottledFactory.Limit; i++)
        {
            HttpResponseMessage res = await client.PostAsJsonAsync(
                "/auth/login", new LoginRequest($"nobody{i}", "correct-horse-battery-staple"));
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                refused = res;
                break;
            }
        }

        Assert.NotNull(refused);
        Assert.Equal("too_many_attempts", await RegistrationTests.CodeOf(refused!));
        // The same shape the account lockout answers with, so a client handles one and has both.
        Assert.NotNull(refused!.Headers.RetryAfter);
    }

    /// <summary>
    /// The limiter is on the door, not on the host: reads a signed-in caller makes are unaffected.
    /// </summary>
    [Fact]
    public async Task ThrottlingTheSignInDoorDoesNotThrottleTheRestOfTheApi()
    {
        HttpClient client = factory.CreateClient();
        for (int i = 0; i <= ThrottledFactory.Limit + 2; i++)
            await client.PostAsJsonAsync("/auth/login", new LoginRequest($"nobody{i}", "x"));

        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.AccessToken(KgsmTier.Admin));
        HttpResponseMessage res = await client.GetAsync("/api/v1/servers");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
