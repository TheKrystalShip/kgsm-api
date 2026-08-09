using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Connected accounts: attaching a Discord account to a KGSM one, detaching it, and the proof both
/// ask for. Guild membership is not in any of this — the same identity attaches to whichever account
/// starts the link, and what that account may do never enters the flow.
/// </summary>
public sealed class IdentityLinkingTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private const string Password = "correct-horse-battery-staple";

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    private static HttpClient Browser(AuthTestFactory f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A signed-in browser for a fresh account, at the tier given.</summary>
    private (HttpClient Client, KgsmUser Account) SignedIn(
        string username, KgsmTier tier = KgsmTier.Operator, bool withPassword = true)
    {
        KgsmIdentity identity = FakeDiscordResolver.IdentityFor(username);
        KgsmUser account = factory.SetAccount(identity, tier);
        var users = factory.Services.GetRequiredService<UserDirectory>();
        if (withPassword)
            users.SignIn.SetPasswordAsync(account.UserId, Password, DateTimeOffset.UtcNow).GetAwaiter().GetResult();

        // Minted the way the tier matrix mints — which deliberately does NOT go through a login, so
        // nothing has proved a credential on this session yet.
        var tokens = factory.Services.GetRequiredService<KGSM.Auth.Sessions.ISessionTokenService>();
        var sessions = factory.Services.GetRequiredService<SessionStore>();
        ApiOptions opts = factory.Services.GetRequiredService<ApiOptions>();
        string sid = "sid_link_" + Guid.NewGuid().ToString("N");
        KGSM.Auth.Sessions.MintedToken minted = tokens.MintAccess(identity, tier, sid);
        sessions.CreateAsync(sid, identity.Handle, opts.HostId, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(opts.SessionsRefreshAbsoluteDays), null, minted.Jti,
            CancellationToken.None).GetAwaiter().GetResult();

        HttpClient client = Browser(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", minted.Token);
        return (client, account);
    }

    /// <summary>
    /// A signed-in browser for an account with a password and no provider attached — what someone who
    /// has never linked anything looks like, and the only account a link can actually land on.
    /// </summary>
    private (HttpClient Client, KgsmUser Account) SignedInLocal(string username)
    {
        var users = factory.Services.GetRequiredService<UserDirectory>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        KgsmUser account = new(
            UserIds.NewUserId(), username, username, KgsmTier.Operator,
            TierSource.Granted, UserStatus.Active, now, now);
        users.Store.CreateAsync(account).GetAwaiter().GetResult();
        users.SignIn.SetPasswordAsync(account.UserId, Password, now).GetAwaiter().GetResult();

        var identity = new KgsmIdentity(
            KgsmActorProvider.Local, account.UserId, username, username, null, []);
        var tokens = factory.Services.GetRequiredService<KGSM.Auth.Sessions.ISessionTokenService>();
        var sessions = factory.Services.GetRequiredService<SessionStore>();
        ApiOptions opts = factory.Services.GetRequiredService<ApiOptions>();
        string sid = "sid_link_" + Guid.NewGuid().ToString("N");
        KGSM.Auth.Sessions.MintedToken minted = tokens.MintAccess(identity, KgsmTier.Operator, sid);
        sessions.CreateAsync(sid, identity.Handle, opts.HostId, now,
            now.AddDays(opts.SessionsRefreshAbsoluteDays), null, minted.Jti,
            CancellationToken.None).GetAwaiter().GetResult();

        HttpClient client = Browser(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", minted.Token);
        return (client, account);
    }

    private static async Task Prove(HttpClient client) =>
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/auth/reauth", new { password = Password })).StatusCode);

    /// <summary>Start a link and return the state the callback must echo back.</summary>
    private static async Task<string> StartLink(HttpClient client)
    {
        HttpResponseMessage start = await client.PostAsync("/auth/identities/discord/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        string url = (await Json(start)).GetProperty("url").GetString()!;
        return url.Split('?', '&').First(kv => kv.StartsWith("state=")).Substring("state=".Length);
    }

    private IReadOnlyList<UserCredential> CredentialsOf(KgsmUser account) =>
        factory.Services.GetRequiredService<UserDirectory>().Store
            .ListCredentialsAsync(account.UserId).GetAwaiter().GetResult();

    // ── The proof both writes ask for ────────────────────────────────────────

    [Fact]
    public async Task StartingALink_WithoutProvingACredential_IsRefused()
    {
        // A live session is not proof that its holder is present: it can be a borrowed unlocked
        // laptop. A link outlives the session that makes it, so it asks again.
        (HttpClient client, _) = SignedIn("link-stale");

        HttpResponseMessage resp = await client.PostAsync("/auth/identities/discord/start", null);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("reauth_required", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheWrongPasswordProvesNothing()
    {
        (HttpClient client, _) = SignedIn("link-wrong-pw");

        HttpResponseMessage resp = await client.PostAsJsonAsync("/auth/reauth", new { password = "not it" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("invalid_credentials", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/auth/identities/discord/start", null)).StatusCode);
    }

    [Fact]
    public async Task AnAccountWithNoPasswordIsToldToSignInAgain_NotLeftGuessing()
    {
        // Someone who only ever arrives through a provider has no password to re-enter. That is not a
        // dead end — signing in stamps the session it mints — so the answer names the way through.
        (HttpClient client, _) = SignedIn("link-no-pw", withPassword: false);

        HttpResponseMessage resp = await client.PostAsJsonAsync("/auth/reauth", new { password = "anything" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("no_password", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SigningInIsItselfTheProof()
    {
        // The common path — arrive, then connect an account — must not ask for a password typed
        // seconds ago. The login stamps the session it mints.
        KgsmIdentity identity = FakeDiscordResolver.IdentityFor("link-just-arrived");
        factory.SetAccount(identity, KgsmTier.Viewer);

        HttpClient c = Browser(factory);
        HttpResponseMessage start = await c.GetAsync("/auth/discord/start");
        string state = start.Headers.Location!.Query.TrimStart('?')
            .Split('&').First(kv => kv.StartsWith("state=")).Substring("state=".Length);
        HttpResponseMessage login = await c.GetAsync(
            $"/auth/discord/callback?code={FakeDiscordResolver.CodeFor("link-just-arrived")}&state={state}");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await Json(login)).GetProperty("token").GetString());

        JsonElement identities = await Json(await c.GetAsync("/auth/identities"));
        Assert.True(identities.GetProperty("reauth").GetProperty("fresh").GetBoolean());
    }

    // ── Attaching ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AVerifiedIdentityAttachesToWhicheverAccountStartedTheLink()
    {
        (HttpClient client, KgsmUser account) = SignedInLocal("link-attach");
        await Prove(client);
        string state = await StartLink(client);

        HttpResponseMessage resp = await client.GetAsync(
            $"/auth/identities/discord/callback?code={FakeDiscordResolver.CodeFor("brand-new-discord")}&state={state}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains(CredentialsOf(account),
            c => c.Kind == CredentialKind.Identity && c.Handle == "discord:brand-new-discord");
    }

    [Fact]
    public async Task AnIdentityOnSomebodyElsesAccountIsRefused_NotMoved()
    {
        // Re-pointing a credential hands one person another's account, and the person on the other end
        // would never learn it had happened.
        (HttpClient client, _) = SignedInLocal("link-thief");
        KgsmIdentity theirs = FakeDiscordResolver.IdentityFor("link-victim");
        KgsmUser victim = factory.SetAccount(theirs, KgsmTier.Admin);

        await Prove(client);
        string state = await StartLink(client);
        HttpResponseMessage resp = await client.GetAsync(
            $"/auth/identities/discord/callback?code={FakeDiscordResolver.CodeFor("link-victim")}&state={state}");

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("identity_taken", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(victim.UserId, factory.AccountOf(theirs)!.UserId);
    }

    [Fact]
    public async Task ACallbackWithNoTicketAttachesNothing()
    {
        // The ticket is what says whose account this is. Without one there is no answer to that
        // question, and guessing at it is how a link lands on the wrong account.
        HttpClient c = Browser(factory);

        HttpResponseMessage resp = await c.GetAsync(
            $"/auth/identities/discord/callback?code={FakeDiscordResolver.CodeFor("nobody")}&state=made-up");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_state", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ATicketIsWorthOneAttempt()
    {
        // A callback URL sits in browser history and in any log that saw it. Replaying it must attach
        // nothing — the second time there is no ticket left to redeem.
        (HttpClient client, _) = SignedInLocal("link-replay");
        await Prove(client);
        string state = await StartLink(client);
        string url = $"/auth/identities/discord/callback?code={FakeDiscordResolver.CodeFor("replay-target")}&state={state}";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        HttpResponseMessage again = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_state", (await Json(again)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASecondDiscordAccountIsRefusedWhileOneIsAttached()
    {
        // Swapping one for another is two deliberate acts, not a silent replacement — otherwise the
        // account that used to get in stops getting in with nothing said about it.
        (HttpClient client, _) = SignedIn("link-two-discords");
        await Prove(client);

        HttpResponseMessage resp = await client.PostAsync("/auth/identities/discord/start", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("already_linked", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    // ── Detaching ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetachingRevokesTheSessionsThatIdentityEstablished()
    {
        // The point of disconnecting an account is that it stops getting in. A session it established
        // that kept working until it happened to expire would be exactly the thing not happening.
        KgsmIdentity identity = FakeDiscordResolver.IdentityFor("link-revoke");
        factory.SetAccount(identity, KgsmTier.Operator);

        HttpClient discordBrowser = Browser(factory);
        HttpResponseMessage start = await discordBrowser.GetAsync("/auth/discord/start");
        string loginState = start.Headers.Location!.Query.TrimStart('?')
            .Split('&').First(kv => kv.StartsWith("state=")).Substring("state=".Length);
        HttpResponseMessage login = await discordBrowser.GetAsync(
            $"/auth/discord/callback?code={FakeDiscordResolver.CodeFor("link-revoke")}&state={loginState}");
        string discordToken = (await Json(login)).GetProperty("token").GetString()!;

        discordBrowser.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", discordToken);
        Assert.Equal(HttpStatusCode.OK, (await discordBrowser.GetAsync("/auth/identities")).StatusCode);

        // The account also has a password, so detaching leaves a way in and is allowed.
        KgsmUser account = factory.AccountOf(identity)!;
        var users = factory.Services.GetRequiredService<UserDirectory>();
        await users.SignIn.SetPasswordAsync(account.UserId, Password, DateTimeOffset.UtcNow);

        UserCredential credential = CredentialsOf(account)
            .Single(c => c.Kind == CredentialKind.Identity && c.Handle == identity.Handle);
        HttpResponseMessage removed = await discordBrowser.DeleteAsync($"/auth/identities/{credential.Id()}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        // The very token that call was made with came in through the identity just detached.
        Assert.Equal(HttpStatusCode.Unauthorized, (await discordBrowser.GetAsync("/auth/identities")).StatusCode);
    }

    [Fact]
    public async Task TheLastWayInIsNotDetachable()
    {
        (HttpClient client, KgsmUser account) = SignedIn("link-last", withPassword: false);
        // Nothing to prove with, so the freshness a detach needs comes from a login instead.
        HttpClient c = Browser(factory);
        HttpResponseMessage start = await c.GetAsync("/auth/discord/start");
        string state = start.Headers.Location!.Query.TrimStart('?')
            .Split('&').First(kv => kv.StartsWith("state=")).Substring("state=".Length);
        HttpResponseMessage login = await c.GetAsync(
            $"/auth/discord/callback?code={FakeDiscordResolver.CodeFor("link-last")}&state={state}");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await Json(login)).GetProperty("token").GetString());

        UserCredential only = Assert.Single(CredentialsOf(account));
        HttpResponseMessage resp = await c.DeleteAsync($"/auth/identities/{only.Id()}");

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("last_credential", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
        Assert.Single(CredentialsOf(account));
        _ = client;
    }

    [Fact]
    public async Task SomebodyElsesCredentialIsNotFound_AndStays()
    {
        // Answering anything else would say whether an id is real, which is all an id needs to be
        // worth probing for.
        (HttpClient client, _) = SignedIn("link-outsider");
        KgsmUser theirs = factory.SetAccount(FakeDiscordResolver.IdentityFor("link-bystander"), KgsmTier.Viewer);
        UserCredential theirCredential = Assert.Single(CredentialsOf(theirs));

        await Prove(client);
        HttpResponseMessage resp = await client.DeleteAsync($"/auth/identities/{theirCredential.Id()}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Single(CredentialsOf(theirs));
    }

    [Fact]
    public async Task DetachingWithoutProvingACredentialIsRefused()
    {
        (HttpClient client, KgsmUser account) = SignedIn("link-detach-stale");
        UserCredential credential = Assert.Single(
            CredentialsOf(account), c => c.Kind == CredentialKind.Identity);

        HttpResponseMessage resp = await client.DeleteAsync($"/auth/identities/{credential.Id()}");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("reauth_required", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    // ── The snapshot ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TheListIsTheCallersOwnMethods_AndWhatElseThisHostOffers()
    {
        (HttpClient client, KgsmUser account) = SignedIn("link-list");

        JsonElement body = await Json(await client.GetAsync("/auth/identities"));

        Assert.Equal(account.UserId, body.GetProperty("userId").GetString());
        Assert.True(body.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("discord",
            body.GetProperty("identities")[0].GetProperty("provider").GetString());
        JsonElement discord = body.GetProperty("providers").EnumerateArray()
            .Single(p => p.GetProperty("provider").GetString() == "discord");
        Assert.True(discord.GetProperty("configured").GetBoolean());
        Assert.True(discord.GetProperty("linked").GetBoolean());
        Assert.False(body.GetProperty("reauth").GetProperty("fresh").GetBoolean());
    }
}

/// <summary>The credential id, spelled the way the wire spells it.</summary>
internal static class CredentialIds
{
    public static string Id(this UserCredential credential) => credential.CredentialId;
}
