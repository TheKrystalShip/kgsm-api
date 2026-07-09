using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M4·c Increment 8 — the session GC worker (<see cref="SessionCleanupWorker"/>) + its core delete
/// (<see cref="SessionStore.DeleteExpiredAsync"/>). Two layers tested separately: the store method
/// directly (deterministic, no timing) for the "what gets deleted" rule (expired-regardless-of-revoked,
/// in-window survives, correct count), and the worker's startup catch-up pass (a brief wait, not the
/// 10-min production timer) for "the worker actually runs the delete and honors the master switch."
/// </summary>
public sealed class SessionCleanupTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    // Insert a session row with an explicit Expires (past or future) via the store's real CreateAsync
    // path, then optionally flip Revoked directly (CreateAsync always inserts Revoked=false — the
    // revoked-and-expired case needs a follow-up write, same pattern as SessionValidatorTests.RevokeRow).
    private static async Task<string> SeedRowAsync(
        IServiceProvider services, DateTimeOffset expires, bool revoked = false)
    {
        var store = services.GetRequiredService<SessionStore>();
        var opts = services.GetRequiredService<ApiOptions>();
        string sid = "sid_gc_" + Guid.NewGuid().ToString("N");
        await store.CreateAsync(sid, "discord:gc-test-user", opts.HostId,
            DateTimeOffset.UtcNow, expires, userAgent: null, initialJti: null, CancellationToken.None);

        if (revoked)
        {
            using IServiceScope scope = services.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SessionEntry row = await db.Sessions.FirstAsync(s => s.Id == sid);
            row.Revoked = true;
            row.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        return sid;
    }

    private static async Task<bool> RowExistsAsync(IServiceProvider services, string sid)
    {
        using IServiceScope scope = services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sid);
    }

    // --- SessionStore.DeleteExpiredAsync — the core rule, no worker/timing involved -----------------

    [Fact]
    public async Task DeleteExpiredAsync_RemovesExpiredRevokedRow_KeepsInWindowRow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string expiredRevoked = await SeedRowAsync(factory.Services, now.AddSeconds(-30), revoked: true);
        string inWindow = await SeedRowAsync(factory.Services, now.AddDays(30), revoked: false);

        var store = factory.Services.GetRequiredService<SessionStore>();
        await store.DeleteExpiredAsync(now, CancellationToken.None);

        Assert.False(await RowExistsAsync(factory.Services, expiredRevoked));
        Assert.True(await RowExistsAsync(factory.Services, inWindow));
    }

    [Fact]
    public async Task DeleteExpiredAsync_RemovesExpiredNonRevokedRow_ExpiredRegardlessOfRevoked()
    {
        // The plan's explicit rule: a NOT-revoked row past its Expires is just as dead as a revoked one
        // (the 30-day absolute cap already killed it) — this pins that half of the "expired regardless
        // of revoked" contract (the sibling test above pins the revoked half).
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string expiredNotRevoked = await SeedRowAsync(factory.Services, now.AddSeconds(-1), revoked: false);

        var store = factory.Services.GetRequiredService<SessionStore>();
        await store.DeleteExpiredAsync(now, CancellationToken.None);

        Assert.False(await RowExistsAsync(factory.Services, expiredNotRevoked));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ReturnsAccurateDeletedCount()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // A fresh, uniquely-timestamped baseline: count only the rows THIS test adds (the shared
        // per-class DB may carry rows from other tests in the fixture) by diffing before/after.
        var store = factory.Services.GetRequiredService<SessionStore>();
        int deletedBefore = await store.DeleteExpiredAsync(now, CancellationToken.None); // sweep any stragglers

        await SeedRowAsync(factory.Services, now.AddSeconds(-10), revoked: false);
        await SeedRowAsync(factory.Services, now.AddSeconds(-5), revoked: true);
        await SeedRowAsync(factory.Services, now.AddDays(1), revoked: false); // survivor, not counted

        int deleted = await store.DeleteExpiredAsync(now, CancellationToken.None);

        Assert.Equal(2, deleted);
        _ = deletedBefore; // sweep result unasserted — only establishes a clean baseline
    }

    // --- SessionCleanupWorker — the startup catch-up pass ------------------------------------------

    [Fact]
    public async Task Worker_StartupCatchUpPass_DeletesExpiredRow_KeepsInWindowRow()
    {
        // A dedicated derived factory (own random DB) so this test's rows can't collide with the
        // shared-fixture rows the store-level tests above write, and so the app's OWN DI-registered
        // SessionCleanupWorker (which also runs a catch-up pass on host startup, before this test seeds
        // anything) can't race the assertion — it has nothing to delete yet when it fires.
        WebApplicationFactory<Program> derived = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["KGSM_API_SESSIONS_GC_MS"] = "60000" })));
        IServiceProvider services = derived.Services; // forces host build + start (the real worker's own catch-up pass runs here, on an empty table)

        string expired = await SeedRowAsync(services, DateTimeOffset.UtcNow.AddMinutes(-1));
        string inWindow = await SeedRowAsync(services, DateTimeOffset.UtcNow.AddDays(1));

        // A second, manually-driven worker instance (same SessionStore/ApiOptions the app uses) so the
        // test controls start/stop directly rather than waiting on the real hosted service's internal
        // timing — StartAsync kicks off ExecuteAsync's startup catch-up pass, which is what's under test.
        var store = services.GetRequiredService<SessionStore>();
        var options = services.GetRequiredService<ApiOptions>();
        var logger = services.GetRequiredService<ILogger<SessionCleanupWorker>>();
        var worker = new SessionCleanupWorker(store, options, logger);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(300); // let the async startup catch-up pass complete
        await worker.StopAsync(CancellationToken.None);

        Assert.False(await RowExistsAsync(services, expired));
        Assert.True(await RowExistsAsync(services, inWindow));
    }

    [Fact]
    public async Task Worker_InertWhenSessionsDisabled_ExpiredRowSurvives()
    {
        // KGSM_API_SESSIONS_DISABLED=1 → ExecuteAsync logs + returns immediately, no timer, no delete.
        // Seed the expired row BEFORE constructing/starting the worker (CreateAsync itself doesn't care
        // about the master switch — it's a plain table write) and confirm the disabled worker leaves it.
        WebApplicationFactory<Program> disabled = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["KGSM_API_SESSIONS_DISABLED"] = "1" })));
        IServiceProvider services = disabled.Services;

        string expired = await SeedRowAsync(services, DateTimeOffset.UtcNow.AddMinutes(-1));

        var store = services.GetRequiredService<SessionStore>();
        var options = services.GetRequiredService<ApiOptions>();
        var logger = services.GetRequiredService<ILogger<SessionCleanupWorker>>();
        var worker = new SessionCleanupWorker(store, options, logger);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(await RowExistsAsync(services, expired));
    }
}
