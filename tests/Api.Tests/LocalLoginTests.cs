using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>POST /auth/login</c> — signing in with a KGSM password.
/// </summary>
/// <remarks>
/// The store is real (a temp file per factory, never the host's), because most of what this endpoint
/// promises is enforced below the controller: the single answer to a bad username or a bad password,
/// the lockout, the tier an account actually holds. A fake store would assert the wiring and none of
/// those.
/// </remarks>
public sealed class LocalLoginTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private const string Password = "correct-horse-battery-staple";

    private UserDirectory Users => factory.Services.GetRequiredService<UserDirectory>();

    /// <summary>Create an account with a password. Usernames are unique, so each test coins its own.</summary>
    private async Task<KgsmUser> Enrol(
        string username, KgsmTier tier = KgsmTier.Operator, UserStatus status = UserStatus.Active,
        string password = Password)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        KgsmUser user = new(
            UserIds.NewUserId(), username, username, tier, TierSource.Granted, status, now, now);

        await Users.Store.CreateAsync(user);
        await Users.SignIn.SetPasswordAsync(user.UserId, password, now);
        return user;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private Task<HttpResponseMessage> Post(string? username, string? password) =>
        factory.CreateClient().PostAsJsonAsync("/auth/login", new LoginRequest(username, password));

    [Fact]
    public async Task ThePasswordDoorNeedsNoIdentityProvider()
    {
        // The point of the whole account store: this host authenticates someone with nothing external
        // reachable. The fake sign-in seam is never touched on this path.
        string name = Unique("haru");
        KgsmUser user = await Enrol(name, KgsmTier.Admin);

        using HttpResponseMessage response = await Post(name, Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        LoginResult? body = await response.Content.ReadFromJsonAsync<LoginResult>();
        Assert.NotNull(body);
        Assert.Equal(KgsmTiers.Admin, body.Tier);
        Assert.Equal($"local:{user.UserId}", body.UserId);
        Assert.Equal(UserStatuses.Active, body.Status);
        Assert.NotEmpty(body.Token);
        Assert.NotEmpty(body.Refresh);
    }

    [Fact]
    public async Task TheMintedTokenAuthorizesAtTheAccountsTier()
    {
        // The end-to-end assertion: a password produces a bearer the real pipeline honours, at the
        // tier the record carries and no higher.
        string name = Unique("kaito");
        await Enrol(name, KgsmTier.Viewer);

        using HttpResponseMessage login = await Post(name, Password);
        LoginResult body = (await login.Content.ReadFromJsonAsync<LoginResult>())!;

        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", body.Token);

        // A viewer reads.
        using HttpResponseMessage read = await client.GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // A viewer does not administer.
        using HttpResponseMessage admin = await client.GetAsync("/auth/users");
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
    }

    [Fact]
    public async Task TheSessionItMintsIsAnOrdinarySessionRow()
    {
        // Same machinery as an OAuth login: a sid the validator can check, so the session is
        // revocable like every other. A token whose sid has no row 401s on its next request.
        string name = Unique("mina");
        await Enrol(name);

        using HttpResponseMessage login = await Post(name, Password);
        LoginResult body = (await login.Content.ReadFromJsonAsync<LoginResult>())!;

        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", body.Token);

        using HttpResponseMessage sessions = await client.GetAsync("/auth/sessions");
        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);

        SessionsPage? page = await sessions.Content.ReadFromJsonAsync<SessionsPage>();
        Assert.Contains(page!.Data, s => s.Current);
    }

    [Fact]
    public async Task TheSessionSnapshotNamesTheLocalAccount()
    {
        string name = Unique("rei");
        KgsmUser user = await Enrol(name);

        using HttpResponseMessage login = await Post(name, Password);
        LoginResult body = (await login.Content.ReadFromJsonAsync<LoginResult>())!;

        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", body.Token);

        SessionResponse? session = await client.GetFromJsonAsync<SessionResponse>("/auth/session");
        Assert.Equal($"local:{user.UserId}", session!.User.Id);
        Assert.Equal(name, session.User.Username);
    }

    [Fact]
    public async Task AnAccountAwaitingApprovalSignsInAndHoldsNothing()
    {
        // A real session with tier none, so the panel can say "awaiting approval" rather than show
        // someone who just proved who they are a bare denial.
        string name = Unique("pending");
        await Enrol(name, KgsmTier.Admin, UserStatus.Pending);

        using HttpResponseMessage response = await Post(name, Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        LoginResult body = (await response.Content.ReadFromJsonAsync<LoginResult>())!;
        Assert.Equal(UserStatuses.Pending, body.Status);
        Assert.Equal(KgsmTiers.None, body.Tier);
    }

    [Fact]
    public async Task AWrongPasswordAndAnUnknownUsernameAreTheSameAnswer()
    {
        // Two answers here is a username oracle. The bodies must match byte for byte, not merely
        // share a status code.
        string name = Unique("haru");
        await Enrol(name);

        using HttpResponseMessage wrongPassword = await Post(name, "not-the-password");
        using HttpResponseMessage noSuchUser = await Post(Unique("ghost"), Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noSuchUser.StatusCode);
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(),
            await noSuchUser.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ADisabledAccountIsAlsoIndistinguishableWithoutThePassword()
    {
        string name = Unique("gone");
        await Enrol(name, KgsmTier.Admin, UserStatus.Disabled);

        using HttpResponseMessage guessed = await Post(name, "not-the-password");
        Assert.Equal(HttpStatusCode.Unauthorized, guessed.StatusCode);

        // Only the password's holder is told what actually happened.
        using HttpResponseMessage known = await Post(name, Password);
        Assert.Equal(HttpStatusCode.Forbidden, known.StatusCode);
        Assert.Equal("account_disabled", await ErrorCode(known));
    }

    [Fact]
    public async Task EnoughWrongPasswordsLockTheAccountAndSayForHowLong()
    {
        string name = Unique("bruteforced");
        await Enrol(name);

        LockoutPolicy policy = LockoutPolicy.Default;
        for (int i = 0; i <= policy.Threshold; i++)
        {
            using HttpResponseMessage _ = await Post(name, "wrong");
        }

        using HttpResponseMessage locked = await Post(name, Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("too_many_attempts", await ErrorCode(locked));
        Assert.NotNull(locked.Headers.RetryAfter);
        Assert.True(locked.Headers.RetryAfter!.Delta > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("haru", null)]
    [InlineData(null, "hunter2")]
    [InlineData("", "")]
    public async Task AMissingFieldIsARefusalAndNotAServerError(string? username, string? password)
    {
        using HttpResponseMessage response = await Post(username, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_request", await ErrorCode(response));
    }

    [Fact]
    public async Task LoginIsReachedWithoutABearerOfItsOwn()
    {
        // The API's fallback policy requires an authenticated caller, so the one door somebody with no
        // session must reach has to opt out explicitly. Both refusals are 401, so the code is what
        // tells them apart: `invalid_credentials` means the endpoint ran, `unauthorized` would mean
        // the gate turned it away before it did.
        using HttpResponseMessage response = await Post(Unique("nobody"), Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", await ErrorCode(response));
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetProperty("code").GetString();
    }
}
