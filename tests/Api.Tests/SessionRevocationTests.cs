using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M4·c Increment 6 — the revocation endpoints (<c>docs/session-management-plan.md</c> §5/§8):
/// <c>GET /auth/sessions</c>, <c>POST /auth/session/revoke</c> (self), <c>POST
/// /auth/sessions/{sid}/revoke</c> (admin cross-user), <c>POST /auth/users/{userId}/sessions/revoke-all</c>
/// (admin). Every test uses fresh sid(s) minted through the shared <see cref="AuthTestFactory"/> DB, so
/// tests stay parallel-safe within the class (xUnit runs a class's own test methods sequentially against
/// the one shared factory instance — the same posture <see cref="SessionRotationTests"/> and
/// <see cref="SessionRegistryTests"/> already rely on).
/// <para>
/// <see cref="FakeDiscordResolver.Identity"/> (<c>discord:198772043</c>, username <c>haru</c>) is the
/// ONE identity <see cref="AuthTestFactory.AccessToken"/>/<c>RefreshToken</c> mint against — good enough
/// for the self-revoke matrix. The admin cross-user tests need a SECOND, distinct user id, which
/// <see cref="MintForUser"/> mints directly (bypassing the Discord seam entirely — a session row + a
/// validly-signed token for an arbitrary <c>discord:</c> handle, the same "mint through the server's own
/// token service" posture <see cref="AuthTestFactory.MintTokenWithRow"/> uses, just parameterized by user).
/// </para>
/// </summary>
public sealed class SessionRevocationTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private const string SelfUserId = "discord:198772043"; // FakeDiscordResolver.Identity

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    private HttpClient Bearer(string token)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    // Base64url-decode a JWT's payload (no signing key needed) to read the `sid` claim back off a
    // token minted via factory.AccessToken/RefreshToken — same helper shape as SessionRegistryTests.
    private static string SidFromToken(string jwt)
    {
        string payload = jwt.Split('.')[1];
        payload = payload.Replace('-', '+').Replace('_', '/')
            .PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        byte[] bytes = Convert.FromBase64String(payload);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        return doc.RootElement.GetProperty("sid").GetString()!;
    }

    /// <summary>
    /// Mint an access token + session row for an ARBITRARY <c>discord:</c> user id, bypassing
    /// <see cref="FakeDiscordResolver"/> entirely (which only ever resolves the one fixed identity).
    /// Same "mint through the server's own token service, insert the row directly" posture as
    /// <see cref="AuthTestFactory.MintTokenWithRow"/> — needed here because the admin cross-user tests
    /// require a SECOND, genuinely distinct user, not just a second sid for the same user.
    /// </summary>
    private (string Token, string Sid) MintForUser(AuthTier tier, string discordUserId)
    {
        var tokens = factory.Services.GetRequiredService<ISessionTokenService>();
        var store = factory.Services.GetRequiredService<SessionStore>();
        var opts = factory.Services.GetRequiredService<ApiOptions>();
        string sid = "sid_test_other_" + Guid.NewGuid().ToString("N");
        var identity = new DiscordIdentity(discordUserId, discordUserId, discordUserId, null, []);
        MintedToken minted = tokens.MintAccess(identity, tier, sid);
        store.CreateAsync(sid, $"discord:{discordUserId}", opts.HostId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(opts.SessionsRefreshAbsoluteDays),
            userAgent: null, initialJti: minted.Jti, CancellationToken.None).GetAwaiter().GetResult();
        return (minted.Token, sid);
    }

    private static Task<HttpResponseMessage> GetMe(HttpClient c) => c.GetAsync("/api/v1/me");

    // ── GET /auth/sessions ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsOnlyActiveRows_WithCurrentFlagSet()
    {
        string tokenA = factory.AccessToken(AuthTier.Viewer);
        string sidA = SidFromToken(tokenA);
        string tokenB = factory.AccessToken(AuthTier.Viewer);
        string sidB = SidFromToken(tokenB);
        // A third session, revoked up front — must NOT appear in the active list.
        string tokenC = factory.AccessToken(AuthTier.Viewer);
        string sidC = SidFromToken(tokenC);
        Assert.Equal(HttpStatusCode.NoContent,
            (await Bearer(tokenC).PostAsJsonAsync("/auth/session/revoke", new { sid = sidC })).StatusCode);

        JsonElement body = await Json(await Bearer(tokenA).GetAsync("/auth/sessions"));
        JsonElement[] rows = body.GetProperty("data").EnumerateArray().ToArray();
        string[] sids = rows.Select(r => r.GetProperty("sid").GetString()!).ToArray();

        Assert.Contains(sidA, sids);
        Assert.Contains(sidB, sids);
        Assert.DoesNotContain(sidC, sids);

        JsonElement rowA = rows.First(r => r.GetProperty("sid").GetString() == sidA);
        JsonElement rowB = rows.First(r => r.GetProperty("sid").GetString() == sidB);
        Assert.True(rowA.GetProperty("current").GetBoolean());   // sidA is the calling bearer's own sid
        Assert.False(rowB.GetProperty("current").GetBoolean());  // a sibling session, not the caller
        Assert.Equal(SelfUserId, rowA.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task List_Admin_UserIdOverride_ReturnsAnotherUsersActiveSet()
    {
        (string otherToken, string otherSid) = MintForUser(AuthTier.Viewer, "555000111");
        string otherUserId = "discord:555000111";
        string adminToken = factory.AccessToken(AuthTier.Admin);

        JsonElement body = await Json(await Bearer(adminToken).GetAsync($"/auth/sessions?userId={otherUserId}"));
        JsonElement[] rows = body.GetProperty("data").EnumerateArray().ToArray();
        Assert.Contains(rows, r => r.GetProperty("sid").GetString() == otherSid);
        Assert.All(rows, r => Assert.Equal(otherUserId, r.GetProperty("userId").GetString()));
        // None of the target's rows are "current" — the calling bearer (admin) owns none of them.
        Assert.All(rows, r => Assert.False(r.GetProperty("current").GetBoolean()));
        _ = otherToken; // minted only to create the row; not otherwise exercised here
    }

    [Fact]
    public async Task List_Viewer_UserIdOverride_IsIgnored_ScopedToSelf()
    {
        (_, string otherSid) = MintForUser(AuthTier.Viewer, "555000222");
        string viewerToken = factory.AccessToken(AuthTier.Viewer);

        JsonElement body = await Json(
            await Bearer(viewerToken).GetAsync("/auth/sessions?userId=discord:555000222"));
        JsonElement[] rows = body.GetProperty("data").EnumerateArray().ToArray();
        // A non-admin's ?userId= is ignored — the response is still scoped to the caller (SelfUserId),
        // never the other user's session.
        Assert.DoesNotContain(rows, r => r.GetProperty("sid").GetString() == otherSid);
        Assert.All(rows, r => Assert.Equal(SelfUserId, r.GetProperty("userId").GetString()));
    }

    [Fact]
    public async Task List_NoBearer_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/auth/sessions")).StatusCode);
    }

    // ── POST /auth/session/revoke (self) ────────────────────────────────────────────────────────

    [Fact]
    public async Task SelfRevoke_BySid_KillsThatSession_SiblingUnaffected_AuditInfo()
    {
        string token1 = factory.AccessToken(AuthTier.Viewer);
        string sid1 = SidFromToken(token1);
        string token2 = factory.AccessToken(AuthTier.Viewer); // sibling session, same user

        Assert.Equal(HttpStatusCode.OK, (await GetMe(Bearer(token1))).StatusCode);

        HttpResponseMessage revoke = await Bearer(token1).PostAsJsonAsync("/auth/session/revoke", new { sid = sid1 });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(Bearer(token1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetMe(Bearer(token2))).StatusCode); // sibling still alive

        // The audit row lands as auth.session.revoke / info, meta.sid == the revoked sid.
        JsonElement audit = await Json(await Bearer(token2).GetAsync("/api/v1/audit?actor=haru"));
        JsonElement row = audit.GetProperty("data").EnumerateArray()
            .First(r => r.GetProperty("action").GetString() == "auth.session.revoke"
                && r.GetProperty("meta").TryGetProperty("sid", out JsonElement s) && s.GetString() == sid1);
        Assert.Equal("info", row.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task SelfRevoke_BySid_NotOwnedByCaller_404()
    {
        (_, string otherSid) = MintForUser(AuthTier.Viewer, "555000333");
        string callerToken = factory.AccessToken(AuthTier.Viewer);

        // A viewer must not be able to revoke another user's session by sid — 404, not 403 (never
        // confirms the sid exists for someone else).
        HttpResponseMessage resp =
            await Bearer(callerToken).PostAsJsonAsync("/auth/session/revoke", new { sid = otherSid });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task SelfRevoke_NeitherSidNorAll_RevokesCallingSession()
    {
        string token = factory.AccessToken(AuthTier.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await GetMe(Bearer(token))).StatusCode);

        HttpResponseMessage revoke = await Bearer(token).PostAsync("/auth/session/revoke", content: null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(Bearer(token))).StatusCode);
    }

    [Fact]
    public async Task SelfRevoke_BothSidAndAll_400()
    {
        string token = factory.AccessToken(AuthTier.Viewer);
        string sid = SidFromToken(token);
        HttpResponseMessage resp =
            await Bearer(token).PostAsJsonAsync("/auth/session/revoke", new { sid, all = true });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // The caller's own session must survive an invalid request — no partial effect.
        Assert.Equal(HttpStatusCode.OK, (await GetMe(Bearer(token))).StatusCode);
    }

    [Fact]
    public async Task SelfRevokeAll_KillsAllOwnSessions_IncludingCaller_AuditInfo()
    {
        string token1 = factory.AccessToken(AuthTier.Viewer);
        string token2 = factory.AccessToken(AuthTier.Viewer);
        string token3 = factory.AccessToken(AuthTier.Viewer);
        foreach (string t in new[] { token1, token2, token3 })
            Assert.Equal(HttpStatusCode.OK, (await GetMe(Bearer(t))).StatusCode);

        HttpResponseMessage revoke = await Bearer(token1).PostAsJsonAsync("/auth/session/revoke", new { all = true });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // All three die, including the calling one (token1).
        foreach (string t in new[] { token1, token2, token3 })
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(Bearer(t))).StatusCode);

        // Prove via a fresh session (mint AFTER the revoke-all, so it isn't itself wiped) that the
        // audit row landed as auth.session.revoke.all / info.
        string freshToken = factory.AccessToken(AuthTier.Viewer);
        JsonElement audit = await Json(await Bearer(freshToken).GetAsync("/api/v1/audit?actor=haru"));
        JsonElement row = audit.GetProperty("data").EnumerateArray()
            .First(r => r.GetProperty("action").GetString() == "auth.session.revoke.all");
        Assert.Equal("info", row.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task SelfRevoke_NoBearer_401()
    {
        HttpResponseMessage resp =
            await factory.CreateClient().PostAsJsonAsync("/auth/session/revoke", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── POST /auth/sessions/{sid}/revoke (admin cross-user) ────────────────────────────────────

    [Fact]
    public async Task AdminRevoke_BySid_KillsTargetSession_AuditWarn()
    {
        (string targetToken, string targetSid) = MintForUser(AuthTier.Viewer, "555000444");
        string adminToken = factory.AccessToken(AuthTier.Admin);

        Assert.Equal(HttpStatusCode.OK, (await GetMe(Bearer(targetToken))).StatusCode);

        HttpResponseMessage revoke = await Bearer(adminToken).PostAsync($"/auth/sessions/{targetSid}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(Bearer(targetToken))).StatusCode);

        JsonElement audit = await Json(await Bearer(adminToken).GetAsync("/api/v1/audit"));
        JsonElement row = audit.GetProperty("data").EnumerateArray()
            .First(r => r.GetProperty("action").GetString() == "auth.session.revoke.admin"
                && r.GetProperty("meta").TryGetProperty("sid", out JsonElement s) && s.GetString() == targetSid);
        Assert.Equal("warn", row.GetProperty("severity").GetString());
        Assert.Equal("discord:555000444", row.GetProperty("meta").GetProperty("userId").GetString());
    }

    [Fact]
    public async Task AdminRevoke_BySid_UnknownSid_404()
    {
        string adminToken = factory.AccessToken(AuthTier.Admin);
        HttpResponseMessage resp =
            await Bearer(adminToken).PostAsync("/auth/sessions/sid_does_not_exist/revoke", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AdminRevoke_BySid_ViewerForbidden_403()
    {
        (_, string targetSid) = MintForUser(AuthTier.Viewer, "555000555");
        string viewerToken = factory.AccessToken(AuthTier.Viewer);

        HttpResponseMessage resp = await Bearer(viewerToken).PostAsync($"/auth/sessions/{targetSid}/revoke", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── POST /auth/users/{userId}/sessions/revoke-all (admin) ──────────────────────────────────

    [Fact]
    public async Task AdminRevokeAllForUser_KillsAllTargetSessions()
    {
        (string t1, _) = MintForUser(AuthTier.Viewer, "555000666");
        (string t2, _) = MintForUser(AuthTier.Operator, "555000666");
        string adminToken = factory.AccessToken(AuthTier.Admin);

        Assert.Equal(HttpStatusCode.OK, (await GetMe(Bearer(t1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetMe(Bearer(t2))).StatusCode);

        HttpResponseMessage revoke =
            await Bearer(adminToken).PostAsync("/auth/users/discord:555000666/sessions/revoke-all", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(Bearer(t1))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(Bearer(t2))).StatusCode);

        JsonElement audit = await Json(await Bearer(adminToken).GetAsync("/api/v1/audit"));
        JsonElement row = audit.GetProperty("data").EnumerateArray()
            .First(r => r.GetProperty("action").GetString() == "auth.session.revoke.admin"
                && r.GetProperty("meta").TryGetProperty("userId", out JsonElement u)
                && u.GetString() == "discord:555000666"
                && !r.GetProperty("meta").TryGetProperty("sid", out _));
        Assert.Equal("warn", row.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task AdminRevokeAllForUser_NoActiveSessions_StillNoContent()
    {
        string adminToken = factory.AccessToken(AuthTier.Admin);
        HttpResponseMessage resp =
            await Bearer(adminToken).PostAsync("/auth/users/discord:no-such-user/sessions/revoke-all", null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task AdminRevokeAllForUser_ViewerForbidden_403()
    {
        string viewerToken = factory.AccessToken(AuthTier.Viewer);
        HttpResponseMessage resp =
            await Bearer(viewerToken).PostAsync("/auth/users/discord:555000666/sessions/revoke-all", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task AdminRevokeAllForUser_NoBearer_401()
    {
        HttpResponseMessage resp =
            await factory.CreateClient().PostAsync("/auth/users/discord:555000666/sessions/revoke-all", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
