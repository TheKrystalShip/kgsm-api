using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>/auth/users</c> — the account surface. With the store as the sole authority, this is the only
/// way anyone's authority on this host ever changes, so it is on the critical path rather than being
/// a convenience.
/// </summary>
public sealed class UsersControllerTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private const string Password = "correct-horse-battery-staple";

    private UserDirectory Users => factory.Services.GetRequiredService<UserDirectory>();

    private HttpClient Admin() => Client(KgsmTier.Admin);
    private HttpClient Client(KgsmTier tier)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.AccessToken(tier));
        return client;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static CreateUserRequest New(
        string username, string tier = KgsmTiers.Viewer, string? password = null, string? status = null) =>
        new(username, username, tier, password, status);

    /// <summary>
    /// Keep at least one other active admin around, so the last-admin guard never fires in a test
    /// that is about something else.
    /// </summary>
    private async Task<KgsmUser> SeedSpareAdmin()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        KgsmUser admin = new(
            UserIds.NewUserId(), Unique("spare"), "Spare", KgsmTier.Admin,
            TierSource.Granted, UserStatus.Active, now, now);

        await Users.Store.CreateAsync(admin);
        return admin;
    }

    // ── the gate ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(KgsmTier.Viewer)]
    [InlineData(KgsmTier.Operator)]
    public async Task OnlyAnAdminSeesOrChangesAccounts(KgsmTier tier)
    {
        HttpClient client = Client(tier);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/auth/users")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/auth/users", New(Unique("nope")))).StatusCode);
    }

    [Fact]
    public async Task WithNoBearerTheAccountSurfaceIs401NotJust403()
    {
        // The load-bearing split: no session at all is a challenge, a session at the wrong tier is a
        // refusal. Collapsing them tells an anonymous caller the endpoint exists and is merely gated.
        using HttpResponseMessage response = await factory.CreateClient().GetAsync("/auth/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── creating ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAdminCreatesAnAccountThatCanThenSignIn()
    {
        string name = Unique("haru");
        using HttpResponseMessage created = await Admin()
            .PostAsJsonAsync("/auth/users", New(name, KgsmTiers.Operator, Password));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;

        Assert.StartsWith(UserIds.UserPrefix, record.Id, StringComparison.Ordinal);
        Assert.Equal(KgsmTiers.Operator, record.Tier);
        Assert.Equal(UserStatuses.Active, record.Status);
        Assert.True(record.HasPassword);
        Assert.Empty(record.Identities);

        using HttpResponseMessage login = await factory.CreateClient()
            .PostAsJsonAsync("/auth/login", new LoginRequest(name, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task AnAdminsChoiceOfTierIsRecordedAsDeliberate()
    {
        // The compensating control for the store being sole authority: an access review has to be
        // able to tell a tier somebody chose from one seeded by a mapping and never looked at since.
        using HttpResponseMessage created = await Admin()
            .PostAsJsonAsync("/auth/users", New(Unique("granted"), KgsmTiers.Operator));

        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;
        Assert.Equal(TierSources.Granted, record.TierSource);
    }

    [Fact]
    public async Task AnAccountCanBeCreatedWithNoPasswordAtAll()
    {
        // The shape an invite or a link-only account starts as. It must not be signable-into.
        string name = Unique("nopass");
        using HttpResponseMessage created = await Admin().PostAsJsonAsync("/auth/users", New(name));

        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;
        Assert.False(record.HasPassword);

        using HttpResponseMessage login = await factory.CreateClient()
            .PostAsJsonAsync("/auth/login", new LoginRequest(name, Password));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task ARecordNeverCarriesASecretInAnyForm()
    {
        using HttpResponseMessage created = await Admin()
            .PostAsJsonAsync("/auth/users", New(Unique("secret"), KgsmTiers.Viewer, Password));

        string json = await created.Content.ReadAsStringAsync();

        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AQAAAA", json, StringComparison.Ordinal); // the hash's own prefix
    }

    [Fact]
    public async Task AUsernameAlreadyTakenIsAConflictNotAnError()
    {
        string name = Unique("twice");
        using HttpResponseMessage first = await Admin().PostAsJsonAsync("/auth/users", New(name));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using HttpResponseMessage second = await Admin().PostAsJsonAsync("/auth/users", New(name.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("username_taken", await ErrorCode(second));
    }

    [Theory]
    [InlineData("ab")]            // too short
    [InlineData("_leading")]      // does not begin with a letter or digit
    [InlineData("has space")]
    [InlineData("someone@example.com")]
    public async Task AnUnusableUsernameIsRefusedWithAReason(string username)
    {
        using HttpResponseMessage response = await Admin().PostAsJsonAsync("/auth/users", New(username));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_request", await ErrorCode(response));
    }

    [Fact]
    public async Task AMisspeltTierIsRefusedRatherThanSilentlyMeaningNone()
    {
        // KgsmTiers.Parse is fail-closed, which is right for reading a token and wrong here: it would
        // turn "opreator" into an account that can do nothing, with nobody told why.
        using HttpResponseMessage response = await Admin()
            .PostAsJsonAsync("/auth/users", New(Unique("typo"), "opreator"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("opreator", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ── changing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATierChangeTakesEffectOnTheNextSignIn()
    {
        string name = Unique("promoted");
        using HttpResponseMessage created = await Admin()
            .PostAsJsonAsync("/auth/users", New(name, KgsmTiers.Viewer, Password));
        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;

        using HttpResponseMessage patched = await Admin().PatchAsJsonAsync(
            $"/auth/users/{record.Id}", new UpdateUserRequest(null, null, KgsmTiers.Operator, null));
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        using HttpResponseMessage login = await factory.CreateClient()
            .PostAsJsonAsync("/auth/login", new LoginRequest(name, Password));
        LoginResult body = (await login.Content.ReadFromJsonAsync<LoginResult>())!;

        Assert.Equal(KgsmTiers.Operator, body.Tier);
    }

    [Fact]
    public async Task DisablingAnAccountStopsItSigningIn()
    {
        string name = Unique("disabled");
        using HttpResponseMessage created = await Admin()
            .PostAsJsonAsync("/auth/users", New(name, KgsmTiers.Operator, Password));
        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;

        using HttpResponseMessage patched = await Admin().PatchAsJsonAsync(
            $"/auth/users/{record.Id}", new UpdateUserRequest(null, null, null, UserStatuses.Disabled));
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        using HttpResponseMessage login = await factory.CreateClient()
            .PostAsJsonAsync("/auth/login", new LoginRequest(name, Password));
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task ApprovingAPendingAccountGivesItTheTierItWasCreatedWith()
    {
        string name = Unique("awaiting");
        using HttpResponseMessage created = await Admin().PostAsJsonAsync(
            "/auth/users", New(name, KgsmTiers.Operator, Password, UserStatuses.Pending));
        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;

        using HttpResponseMessage before = await factory.CreateClient()
            .PostAsJsonAsync("/auth/login", new LoginRequest(name, Password));
        Assert.Equal(KgsmTiers.None, (await before.Content.ReadFromJsonAsync<LoginResult>())!.Tier);

        using HttpResponseMessage _ = await Admin().PatchAsJsonAsync(
            $"/auth/users/{record.Id}", new UpdateUserRequest(null, null, null, UserStatuses.Active));

        using HttpResponseMessage after = await factory.CreateClient()
            .PostAsJsonAsync("/auth/login", new LoginRequest(name, Password));
        Assert.Equal(KgsmTiers.Operator, (await after.Content.ReadFromJsonAsync<LoginResult>())!.Tier);
    }

    [Fact]
    public async Task AnAbsentFieldIsLeftAloneRatherThanCleared()
    {
        // What makes this safe to call from a form that edits one thing.
        string name = Unique("partial");
        using HttpResponseMessage created = await Admin()
            .PostAsJsonAsync("/auth/users", New(name, KgsmTiers.Operator, Password));
        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;

        using HttpResponseMessage patched = await Admin().PatchAsJsonAsync(
            $"/auth/users/{record.Id}", new UpdateUserRequest(null, "A New Name", null, null));

        UserRecord updated = (await patched.Content.ReadFromJsonAsync<UserRecord>())!;
        Assert.Equal("A New Name", updated.DisplayName);
        Assert.Equal(name, updated.Username);
        Assert.Equal(KgsmTiers.Operator, updated.Tier);
        Assert.True(updated.HasPassword);
    }

    [Fact]
    public async Task AMisspeltStatusIsRefusedRatherThanDisablingSomebody()
    {
        // UserStatuses.Parse is fail-closed and reads anything unknown as disabled — correct when
        // reading the file, catastrophic here, where a typo would lock someone out silently.
        using HttpResponseMessage created = await Admin().PostAsJsonAsync("/auth/users", New(Unique("typo")));
        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;

        using HttpResponseMessage patched = await Admin().PatchAsJsonAsync(
            $"/auth/users/{record.Id}", new UpdateUserRequest(null, null, null, "actve"));

        Assert.Equal(HttpStatusCode.BadRequest, patched.StatusCode);

        UserRecord after = (await Admin().GetFromJsonAsync<UserRecord>($"/auth/users/{record.Id}"))!;
        Assert.Equal(UserStatuses.Active, after.Status);
    }

    [Fact]
    public async Task AnUnknownAccountIsANotFound()
    {
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Admin().GetAsync("/auth/users/usr_nothing")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Admin().DeleteAsync("/auth/users/usr_nothing")).StatusCode);
    }

    // ── the last admin ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOnlyActiveAdminCannotBeDemotedDisabledOrDeleted()
    {
        // The one change nobody can undo from inside the panel. Both a demotion and a disable get
        // there, so both are guarded — and so is deletion.
        //
        // The account under test is the CALLER'S OWN, which is not a shortcut: authority comes from
        // the store now, so anyone making an admin request is by definition an active admin. "The only
        // active admin" therefore always means the person holding the mouse.
        HttpClient admin = Admin();
        KgsmUser caller = factory.AccountOf(FakeDiscordResolver.Identity)!;
        foreach (KgsmUser other in await Users.Store.ListAsync())
        {
            if (other.UserId != caller.UserId && other.Tier == KgsmTier.Admin && other.Status == UserStatus.Active)
                await Users.Store.UpdateAsync(other with { Status = UserStatus.Disabled });
        }

        using HttpResponseMessage demote = await admin.PatchAsJsonAsync(
            $"/auth/users/{caller.UserId}", new UpdateUserRequest(null, null, KgsmTiers.Operator, null));
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("last_admin", await ErrorCode(demote));

        using HttpResponseMessage disable = await admin.PatchAsJsonAsync(
            $"/auth/users/{caller.UserId}", new UpdateUserRequest(null, null, null, UserStatuses.Disabled));
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);

        using HttpResponseMessage delete = await admin.DeleteAsync($"/auth/users/{caller.UserId}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        // With a second admin in place the same demotion goes through.
        await SeedSpareAdmin();
        using HttpResponseMessage nowFine = await admin.PatchAsJsonAsync(
            $"/auth/users/{caller.UserId}", new UpdateUserRequest(null, null, KgsmTiers.Operator, null));
        Assert.Equal(HttpStatusCode.OK, nowFine.StatusCode);
    }

    // ── passwords ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAdminResetsAPasswordAndTheOldOneStopsWorking()
    {
        string name = Unique("forgot");
        using HttpResponseMessage created = await Admin()
            .PostAsJsonAsync("/auth/users", New(name, KgsmTiers.Viewer, Password));
        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;

        const string replacement = "an-entirely-different-one";
        using HttpResponseMessage reset = await Admin().PostAsJsonAsync(
            $"/auth/users/{record.Id}/password", new SetPasswordRequest(replacement));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        HttpClient anonymous = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/auth/login", new LoginRequest(name, Password))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/auth/login", new LoginRequest(name, replacement))).StatusCode);
    }

    [Fact]
    public async Task AShortPasswordIsRefused()
    {
        using HttpResponseMessage created = await Admin().PostAsJsonAsync("/auth/users", New(Unique("short")));
        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;

        using HttpResponseMessage response = await Admin().PostAsJsonAsync(
            $"/auth/users/{record.Id}/password", new SetPasswordRequest("hunter2"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangingYourOwnPasswordNeedsTheCurrentOne()
    {
        // A session can be a borrowed laptop. Letting one change the password that would take the
        // account back turns a temporary compromise into a permanent one.
        string name = Unique("self");
        using HttpResponseMessage created = await Admin()
            .PostAsJsonAsync("/auth/users", New(name, KgsmTiers.Viewer, Password));

        using HttpResponseMessage login = await factory.CreateClient()
            .PostAsJsonAsync("/auth/login", new LoginRequest(name, Password));
        LoginResult session = (await login.Content.ReadFromJsonAsync<LoginResult>())!;

        HttpClient self = factory.CreateClient();
        self.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);

        using HttpResponseMessage guessed = await self.PostAsJsonAsync(
            "/auth/password", new ChangePasswordRequest("not-it", "a-brand-new-password"));
        Assert.Equal(HttpStatusCode.Forbidden, guessed.StatusCode);

        using HttpResponseMessage proper = await self.PostAsJsonAsync(
            "/auth/password", new ChangePasswordRequest(Password, "a-brand-new-password"));
        Assert.Equal(HttpStatusCode.NoContent, proper.StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await factory.CreateClient().PostAsJsonAsync(
                "/auth/login", new LoginRequest(name, "a-brand-new-password"))).StatusCode);
    }

    [Fact]
    public async Task AProviderSessionHasNoKgsmPasswordToChange()
    {
        // The fake sign-in seam mints a discord identity, so this is the ordinary OAuth caller. It
        // must be told plainly rather than getting a 404 for an account that was never there.
        using HttpResponseMessage response = await Admin().PostAsJsonAsync(
            "/auth/password", new ChangePasswordRequest("whatever", "a-brand-new-password"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_a_local_session", await ErrorCode(response));
    }

    // ── the trail ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryPrivilegeChangeLeavesItsOwnAuditRow()
    {
        // One row per fact, not a single "updated": an access review reads for a tier change or for a
        // disable, and a combined row makes both of those a text search.
        await SeedSpareAdmin();
        string name = Unique("audited");
        using HttpResponseMessage created = await Admin()
            .PostAsJsonAsync("/auth/users", New(name, KgsmTiers.Viewer, Password));
        UserRecord record = (await created.Content.ReadFromJsonAsync<UserRecord>())!;

        using HttpResponseMessage _ = await Admin().PatchAsJsonAsync(
            $"/auth/users/{record.Id}",
            new UpdateUserRequest(null, null, KgsmTiers.Admin, UserStatuses.Disabled));

        string audit = await (await Admin().GetAsync("/api/v1/audit?limit=200")).Content.ReadAsStringAsync();

        Assert.Contains("user.provisioned", audit, StringComparison.Ordinal);
        Assert.Contains("user.tier_changed", audit, StringComparison.Ordinal);
        Assert.Contains("user.disabled", audit, StringComparison.Ordinal);
        // The account acted upon rides in meta; the actor is whoever did it.
        Assert.Contains(record.Id, audit, StringComparison.Ordinal);
        // And never the password, in any form.
        Assert.DoesNotContain(Password, audit, StringComparison.Ordinal);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetProperty("code").GetString();
    }
}
