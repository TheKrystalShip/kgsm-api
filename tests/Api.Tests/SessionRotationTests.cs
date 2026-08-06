using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M4·c rolling-refresh rotation (the off-plan intermediate step — supersedes the plan's D8 "no
/// sliding" / D9 "no rotation"): every <c>POST /auth/session/refresh</c> rotates BOTH tokens, slides
/// the session row's <c>Expires</c> forward (the rolling 30-day window — a user who opens the panel
/// once inside the window stays logged in), bumps <c>LastSeen</c>, and rotates <c>CurrentJti</c>. The
/// presented refresh's <c>jti</c> is checked against the row's stored <c>CurrentJti</c> — a stale jti
/// (an OLD/reused refresh token) → <c>401</c> (reuse detection). Logout revokes the row server-side so
/// the session's tokens stop authorizing. Each test uses a unique sid → parallel-safe on the shared DB.
/// </summary>
public sealed class SessionRotationTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    private HttpClient Bearer(string token)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static Task<HttpResponseMessage> PostRefresh(HttpClient c) =>
        c.PostAsync("/auth/session/refresh", content: null);

    private SessionEntry? Row(string sid)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.Sessions.AsNoTracking().FirstOrDefault(s => s.Id == sid);
    }

    // --- Rotation + the rolling (sliding) window ---------------------------------------------------

    [Fact]
    public async Task Refresh_RotatesTokens_SlidesExpires_BumpsLastSeen()
    {
        // Set up a session whose Expires is deliberately SHORT (now + 10d) so the slide to ~now + 30d
        // is unambiguous (a same-instant re-mint would move Expires only microseconds). Mint the refresh
        // token with a known sid + seed the row's CurrentJti to that token's jti so reuse-detection passes.
        var tokens = factory.Services.GetRequiredService<ISessionTokenService>();
        var store = factory.Services.GetRequiredService<SessionStore>();
        var opts = factory.Services.GetRequiredService<ApiOptions>();
        string sid = "sid_rot_" + Guid.NewGuid().ToString("N");
        MintedToken r0 = tokens.MintRefresh(FakeDiscordResolver.Identity, KgsmTier.Operator, sid);
        DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-1);   // LastSeen must visibly advance
        DateTimeOffset shortExpires = DateTimeOffset.UtcNow.AddDays(10);
        await store.CreateAsync(sid, $"discord:{FakeDiscordResolver.Identity.UserId}", opts.HostId,
            created, shortExpires, userAgent: null, initialJti: r0.Jti, CancellationToken.None);

        HttpResponseMessage resp = await PostRefresh(Bearer(r0.Token));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonElement body = await Json(resp);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));   // new access
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refresh").GetString()));  // rotated refresh
        Assert.Equal("operator", body.GetProperty("tier").GetString());

        // The row slid forward to ~now + 30d (from +10d), LastSeen advanced past `created`, jti rotated.
        SessionEntry? row = Row(sid);
        Assert.NotNull(row);
        Assert.True(row!.Expires > DateTimeOffset.UtcNow.AddDays(opts.SessionsRefreshAbsoluteDays - 1),
            $"Expires should slide to ~now+{opts.SessionsRefreshAbsoluteDays}d, was {row.Expires:O}");
        Assert.True(row.LastSeen > created, "LastSeen should advance on refresh");
        Assert.NotNull(row.CurrentJti);
        Assert.NotEqual(r0.Jti, row.CurrentJti);   // rotated away from the presented jti
    }

    [Fact]
    public async Task Refresh_NewRefreshToken_ChainsAcrossRotations()
    {
        // The rotated refresh token from one call is itself a valid refresh token for the next call —
        // the SPA can keep refreshing indefinitely (the rolling window), adopting the new refresh each time.
        string r0 = factory.RefreshToken(KgsmTier.Operator);
        string r1 = (await Json(await PostRefresh(Bearer(r0)))).GetProperty("refresh").GetString()!;
        HttpResponseMessage second = await PostRefresh(Bearer(r1));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(string.IsNullOrEmpty((await Json(second)).GetProperty("refresh").GetString()));
    }

    // --- Reuse detection --------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_OldRefreshToken_AfterRotation_Rejected_401()
    {
        // Present r0 once (rotates the row to r1's jti), then present the SAME r0 again: its jti is now
        // stale vs the row's CurrentJti → 401 (reuse detection — an old/stolen refresh token can't mint).
        string r0 = factory.RefreshToken(KgsmTier.Operator);
        HttpResponseMessage first = await PostRefresh(Bearer(r0));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        HttpResponseMessage reused = await PostRefresh(Bearer(r0));
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        // The legitimately-rotated token from the first call still works — only the stale one is dead.
        string r1 = (await Json(first)).GetProperty("refresh").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await PostRefresh(Bearer(r1))).StatusCode);
    }

    // --- Logout revokes server-side ---------------------------------------------------------------

    [Fact]
    public async Task Logout_RevokesSession_SubsequentAccess_401()
    {
        // An access token with a live session row authorizes /me. After logout revokes + evicts the
        // session, the SAME token 401s — the server-side revoke the milestone exists for (not just a
        // client-side token drop). Uses the production-default 5s cache TTL; the Evict makes it instant.
        string token = factory.AccessToken(KgsmTier.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await Bearer(token).GetAsync("/api/v1/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Bearer(token).PostAsync("/auth/logout", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(token).GetAsync("/api/v1/me")).StatusCode);
    }
}
