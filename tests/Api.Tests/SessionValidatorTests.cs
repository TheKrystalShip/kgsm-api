using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M4·c Increment 4 — the per-request session validator (cached): every authenticated request now
/// checks the <c>SessionEntry</c> registry for <c>Id == sid &amp;&amp; !Revoked &amp;&amp; Expires &gt; now</c>.
/// The 5s in-memory cache is the revocation-lag bound (D2); <c>Evict</c> makes a revoke ~instant, the
/// TTL is the backstop. D10: pre-M4·c tokens (no <c>sid</c> claim) are rejected — the clean break.
/// The escape hatch (<c>KGSM_API_SESSIONS_DISABLED=1</c>) bypasses the whole check.
/// <para>
/// Each test mints a token + inserts a <see cref="SessionEntry"/> row with a KNOWN sid (so the test
/// can revoke/expire/evict that specific row). <c>/api/v1/me</c> is the probe endpoint (viewer-gated,
/// returns 200 on a valid session). The shared per-class DB is fine — each sid is unique per test.
/// </para>
/// </summary>
public sealed class SessionValidatorTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    // Mint a real access token at `tier` with a KNOWN sid + insert its session row against services's
    // own DB. Returns (token, sid) so the test can revoke/expire/evict that specific row. The factory
    // variant (base vs WithWebHostBuilder-derived) determines which DB + service provider the row must
    // land in — the request goes through that factory's pipeline, whose SessionValidator queries that DB.
    private static (string Token, string Sid) MintWithKnownSid(IServiceProvider services, AuthTier tier)
    {
        var tokens = services.GetRequiredService<ISessionTokenService>();
        var store = services.GetRequiredService<SessionStore>();
        var opts = services.GetRequiredService<ApiOptions>();
        string sid = "sid_validator_" + Guid.NewGuid().ToString("N");
        MintedToken minted = tokens.MintAccess(FakeDiscordResolver.Identity, tier, sid);
        store.CreateAsync(sid, $"discord:{FakeDiscordResolver.Identity.UserId}", opts.HostId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(opts.SessionsRefreshAbsoluteDays),
            userAgent: null, CancellationToken.None).GetAwaiter().GetResult();
        return (minted.Token, sid);
    }

    private HttpClient Client(string token)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    // Mark a session row Revoked=true (the production revoke path — Increment 5/6 — does this; the test
    // does it directly via AppDbContext to isolate the validator from the revoke plumbing). Opens its
    // own scope (AppDbContext is scoped — never resolve from the root provider).
    private void RevokeRow(string sid)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SessionEntry? row = db.Sessions.FirstOrDefault(s => s.Id == sid);
        if (row is not null)
        {
            row.Revoked = true;
            row.RevokedAt = DateTimeOffset.UtcNow;
            db.SaveChanges();
        }
    }

    // Move a session row's Expires into the past (simulates the 30-day absolute cap being reached).
    private void ExpireRow(string sid)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SessionEntry? row = db.Sessions.FirstOrDefault(s => s.Id == sid);
        if (row is not null)
        {
            row.Expires = DateTimeOffset.UtcNow.AddSeconds(-1);
            db.SaveChanges();
        }
    }

    private async Task<HttpStatusCode> Me(string token) =>
        (await Client(token).GetAsync("/api/v1/me")).StatusCode;

    // --- The basic happy/reject matrix -----------------------------------------------------------

    [Fact]
    public async Task ValidSession_PassesValidator_Returns200()
    {
        // A freshly-minted token + row → the validator's first check hits the DB, finds the live row,
        // caches true → 200. (The 56 existing tier-matrix tests exercise this implicitly; this test
        // pins the validator's happy path explicitly.)
        (string token, _) = MintWithKnownSid(factory.Services, AuthTier.Viewer);
        Assert.Equal(HttpStatusCode.OK, await Me(token));
    }

    [Fact]
    public async Task RevokedSession_Rejected_Returns401()
    {
        // Mint+insert (row valid, Revoked=false). NO request before the revoke → the cache is cold.
        // Revoke the row directly (Revoked=true), THEN request → validator: cache miss → DB →
        // Revoked=true → false → 401. The cold cache means no stale-true to fight through.
        (string token, string sid) = MintWithKnownSid(factory.Services, AuthTier.Viewer);
        RevokeRow(sid);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(token));
    }

    [Fact]
    public async Task ExpiredSession_RejectedEvenIfNotRevoked_Returns401()
    {
        // The Expires > now check is INDEPENDENT of Revoked — a non-revoked but past-the-30d-cap session
        // is dead. Mint+insert (valid), move Expires to the past, request → 401.
        (string token, string sid) = MintWithKnownSid(factory.Services, AuthTier.Viewer);
        ExpireRow(sid);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(token));
    }

    [Fact]
    public async Task PreM4cToken_NoSidClaim_Rejected_Returns401()
    {
        // D10 — the clean break. A pre-M4·c token is validly signed + carries every M4·a claim EXCEPT
        // `sid`. JwtBearer accepts the signature; OnTokenValidated reaches the `string.IsNullOrEmpty(sid)`
        // check → ctx.Fail("no session id (pre-M4·c token)") → 401. No grandfather path.
        string token = TestTokens.MintAccessWithoutSidClaim(
            AuthTestFactory.SigningKey, AuthTestFactory.HostId, AuthTier.Admin);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(token));
    }

    // --- The escape hatch -----------------------------------------------------------------------

    [Fact]
    public async Task SessionsDisabled_TokenWithoutRow_Passes200()
    {
        // KGSM_API_SESSIONS_DISABLED=1 → the validator + the OnTokenValidated sid check BOTH bypass.
        // A token whose sid has NO row (no SessionStore.CreateAsync call) passes — the pre-M4·c
        // stateless posture, available for debugging. The derived factory overrides the config key.
        WebApplicationFactory<Program> disabled = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["KGSM_API_SESSIONS_DISABLED"] = "1" })));

        // Mint WITHOUT inserting a row — just the token, no SessionStore call. Under sessions-ON this
        // would 401 (no row → validator false); under sessions-OFF the bypass makes it pass.
        var tokens = disabled.Services.GetRequiredService<ISessionTokenService>();
        string token = tokens.MintAccess(FakeDiscordResolver.Identity, AuthTier.Viewer,
            "sid_no_row_" + Guid.NewGuid().ToString("N")).Token;

        HttpClient c = disabled.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode);
    }

    // --- The cache (behavioral — proving the cache works, no DB-hit counter) --------------------

    [Fact]
    public async Task CacheWindow_ServesStaleTrueThenExpiresTo401()
    {
        // Prove the cache IS working (not just re-querying every request): a revoked row returns 200
        // WITHIN the cache window (the stale `true` is served), then 401 AFTER the cache expires (the
        // TTL backstop re-queries → finds Revoked=true → false). Uses a SHORT cache TTL (600ms — above
        // the SessionsCacheTtlMs 500ms floor) so the wait is fast; the production default 5000ms would
        // make this test slow.
        WebApplicationFactory<Program> fast = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["KGSM_API_SESSIONS_CACHE_TTL_MS"] = "600" })));

        (string token, string sid) = MintWithKnownSid(fast.Services, AuthTier.Viewer);
        HttpClient c = fast.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Request 1 → 200. The validator's FIRST check: cache miss → DB → true, cached for 200ms.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode);

        // Revoke the row directly — but do NOT call Evict (simulating a revoke that didn't evict, or a
        // race: the revoke path writes Revoked=true but the cache entry from request 1 is still live).
        using (IServiceScope scope = fast.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SessionEntry? row = db.Sessions.FirstOrDefault(s => s.Id == sid);
            if (row is not null) { row.Revoked = true; row.RevokedAt = DateTimeOffset.UtcNow; db.SaveChanges(); }
        }

        // Request 2 → STILL 200 (within the 200ms cache window, the stale `true` is served — the DB
        // would now return false, but the cache short-circuits). THIS is the proof the cache works:
        // without it, this request would 401 (the row is revoked).
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode);

        // Wait for the cache entry to expire (600ms TTL + margin).
        await Task.Delay(800);

        // Request 3 → 401 (cache expired → DB re-query → Revoked=true → false). The TTL backstop.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Evict_MakesRevokeImmediateWithinCacheWindow_401()
    {
        // Prove Evict is the instant-revoke mechanism: within the cache window (the SECOND request
        // would otherwise serve stale `true`), calling Evict drops the entry so the next request
        // re-queries → finds Revoked=true → 401 ~instantly, no TTL wait. Uses the production-default
        // 5s TTL (the lag Evict collapses) to prove the real default paths are covered.
        (string token, string sid) = MintWithKnownSid(factory.Services, AuthTier.Viewer);
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Request 1 → 200 (cache populated true for 5s — without Evict, the revoke would take ≤5s).
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode);

        // Revoke the row + Evict the cache entry — the production revoke path (Increment 5/6) does both.
        RevokeRow(sid);
        factory.Services.GetRequiredService<ISessionValidator>().Evict(sid);

        // Request 2 → 401 IMMEDIATELY (cache evicted → DB re-query → Revoked=true → false). No 5s wait.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
    }
}