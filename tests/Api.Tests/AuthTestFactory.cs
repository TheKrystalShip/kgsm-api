using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Users;

using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Boots the real API in-process with auth ON and a known signing key, with the Discord seam swapped
/// for <see cref="FakeDiscordResolver"/>. Everything else is the production pipeline — the JwtBearer
/// validation, the tier policies, the controllers — so the tier matrix exercises the real wiring, only
/// the discord.com boundary is faked. The engine/monitor are left unprovisioned so reads degrade to
/// 200 (empty roster / null capacity) with no external dependency.
/// </summary>
public class AuthTestFactory : WebApplicationFactory<Program>
{
    public const string HostId = "test-host";
    public const string SigningKey = "test-signing-key-please-ignore-deterministic";

    /// <summary>
    /// A database in a state directory of its own, for one factory.
    /// </summary>
    /// <remarks>
    /// The directory matters as much as the file. <see cref="ApiOptions.StateDir"/> is the database's directory,
    /// and the API writes host secrets there — the signing key it generates for itself, the first
    /// administrator's password. A database sitting in a bare /tmp would hand every factory in the run
    /// the same two files.
    /// </remarks>
    public static string NewDbPath(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "kgsm-api.db");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:HostId"] = HostId,
                ["Api:SigningKey"] = SigningKey,
                // Discord configured, so the login and linking paths run; the FAKE replaces the real
                // HTTP. The applications live in the ecosystem's shared KgsmAuth section, keyed by
                // provider; the redirect URI is this surface's own and stays on Api.
                ["KgsmAuth:Providers:discord:ClientId"] = "test-client",
                ["KgsmAuth:Providers:discord:ClientSecret"] = "test-secret",
                ["Api:DiscordRedirectUri"] = "https://host.test/auth/discord/callback",
                // Callback returns JSON by default (the contract these tests assert). The fragment-
                // handoff variant overrides this per-test. Pin it empty so a dev appsettings value
                // (Api__AuthFrontendUrl) can't flip the base suite to redirect.
                ["Api:AuthFrontendUrl"] = "",
                // No engine / monitor — reads degrade to 200, no external dependency.
                ["Api:KgsmPath"] = "",
                ["Api:MonitorSocketPath"] = "/tmp/kgsm-api-tests-no-monitor.sock",
                ["Api:WatchdogSocketPath"] = "",
                ["Api:DbPath"] = NewDbPath("kgsm-api-tests"),
                // Its own journal per factory. The default puts events/ beside the database, which
                // would work now that each factory has a state directory to itself — it is named
                // anyway so the isolation does not silently depend on that.
                ["Api:EventJournalDir"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-events-{Guid.NewGuid():N}"),
                // Scan a directory that has no journals in it. The default is the machine's real
                // /var/lib, so a test host would otherwise merge THIS machine's watchdog and monitor
                // history into every assertion about what a test just did.
                ["Api:JournalStateRoot"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-state-{Guid.NewGuid():N}"),
                // The engine's journal is named explicitly rather than scanned, so isolating the state
                // root above does not cover it. Left at its default, every test asserting what the feed
                // holds would be reading THIS machine's real engine history.
                ["Api:KgsmJournalDir"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-journal-{Guid.NewGuid():N}"),
                // Same rule again, for the directory that says which leaves are installed. The default
                // is the machine's real /var/lib/kgsm/leaves, and an unconfigured leaf endpoint resolves
                // against it — so a developer's own host would decide whether "the assistant is absent"
                // is true in a test that never mentioned the assistant.
                ["Api:LeafDescriptorDir"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-leaves-{Guid.NewGuid():N}"),
                // Never the default. /var/lib/kgsm/auth/users.db is the HOST's real account store, shared
                // with every KGSM service on the box, and opening it CREATES it — so an unpinned test
                // run would hand the operator a live accounts file that nobody made. Same rule that
                // keeps AuditJournalRelayTests off the engine's real journal.
                ["Api:UsersDbPath"] = Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-users-{Guid.NewGuid():N}.db"),
                // No authority cache. A test that mints a viewer token and then an admin one asks the
                // same question twice inside any sane TTL, and a cached first answer would make the
                // second silently wrong. The cache has its own tests, where the TTL is the subject.
                ["Api:AuthorityCacheSeconds"] = "0",
                // Effectively no anonymous rate limit. The limiter partitions on the caller's
                // address, and an in-memory test server has none — so every test in the run shares
                // one bucket, and the production ten-a-minute would start refusing whichever class
                // happened to go last. The limiter's own behaviour is asserted by a factory that
                // sets this deliberately low.
                ["Api:AnonymousRateLimit"] = "100000",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // One fake behind the catalog, and the SAME instance for every flow: a test asserts on
            // what the callback presented (LastCodeVerifier), and two instances would record into
            // whichever one the flow under test did not use. Replacing the catalog rather than the
            // seams below it is what keeps a test off discord.com whichever route it takes — the
            // login bounce, the login callback, and both halves of the link all resolve here.
            services.RemoveAll<IAuthProviderCatalog>();
            services.AddSingleton<FakeDiscordResolver>();
            services.AddSingleton<IAuthProviderCatalog>(sp =>
                new FakeAuthProviderCatalog(sp.GetRequiredService<FakeDiscordResolver>()));
        });
    }

    /// <summary>Mint a real access token at <paramref name="tier"/> using the server's own token
    /// service (same key + host the running pipeline validates against). A M4·c token carries a
    /// <c>sid</c> claim; this helper ALSO inserts a <c>SessionEntry</c> row for that sid so the per-request
    /// session validator (Increment 4) passes — the 56 existing tier-matrix call sites exercise the real
    /// production path (valid session → validator passes → tier check is what differs). A fresh random
    /// sid per call keeps the matrix parallel-safe. Sync-over-async in a test helper only — production
    /// never does this.</summary>
    public string AccessToken(KgsmTier tier) => MintTokenWithRow(Services, tier, access: true);

    /// <summary>Mint a real refresh token (30d cap) at <paramref name="tier"/> + insert its session
    /// row (same rationale as <see cref="AccessToken"/> — the validator honors the sid on refresh).</summary>
    public string RefreshToken(KgsmTier tier) => MintTokenWithRow(Services, tier, access: false);

    /// <summary>
    /// Mint a real access token for <paramref name="identity"/>, insert its session row, and give it an
    /// account at <paramref name="tier"/>/<paramref name="status"/> — the whole setup for one person.
    /// </summary>
    /// <remarks>
    /// <see cref="AccessToken"/> mints for the one standing identity, so every token it hands out is
    /// the same person holding whichever tier was asked for last. A test about who a frame reaches, or
    /// about one account changing while another watches, needs two people, and this is how it gets
    /// them: <see cref="FakeDiscordResolver.IdentityFor"/> names one, and this gives them a session
    /// and an account of their own.
    /// </remarks>
    public string AccessTokenFor(KgsmIdentity identity, KgsmTier tier, UserStatus status = UserStatus.Active)
    {
        var tokens = Services.GetRequiredService<ISessionTokenService>();
        var store = Services.GetRequiredService<SessionStore>();
        var opts = Services.GetRequiredService<ApiOptions>();
        string sid = "sid_test_" + Guid.NewGuid().ToString("N");
        MintedToken minted = tokens.MintAccess(identity, tier, sid);
        store.CreateAsync(sid, identity.Handle, opts.HostId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(opts.SessionsRefreshAbsoluteDays),
            userAgent: null, initialJti: minted.Jti, CancellationToken.None).GetAwaiter().GetResult();

        SetAccount(identity, tier, status);
        return minted.Token;
    }

    /// <summary>
    /// Shared mint+insert behind <see cref="AccessToken"/>/<see cref="RefreshToken"/>. Takes an
    /// <see cref="IServiceProvider"/> so a <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
    /// built via <c>WithWebHostBuilder</c> (a DERIVED factory with its OWN random DB + service
    /// provider — different from the base factory's) can mint + insert through the SAME provider the
    /// request will go through. The validator runs on the request's service provider; the row must
    /// land in the request's DB, not the base factory's.
    /// </summary>
    internal static string MintTokenWithRow(IServiceProvider services, KgsmTier tier, bool access)
    {
        var tokens = services.GetRequiredService<ISessionTokenService>();
        var store = services.GetRequiredService<SessionStore>();
        var opts = services.GetRequiredService<ApiOptions>();
        string sid = "sid_test_" + Guid.NewGuid().ToString("N");
        MintedToken minted = access
            ? tokens.MintAccess(FakeDiscordResolver.Identity, tier, sid)
            : tokens.MintRefresh(FakeDiscordResolver.Identity, tier, sid);
        // Insert the session row synchronously (sync-over-async — test-only, production never does
        // this). The row's Expires mirrors SessionsRefreshAbsoluteDays (the same value the controller
        // uses at login), so the validator's `Expires > now` check passes. The row's CurrentJti is the
        // MINTED token's jti — so a refresh token from RefreshToken(tier) passes reuse-detection at
        // /auth/session/refresh (the row's stored jti == the presented refresh's jti).
        store.CreateAsync(sid, FakeDiscordResolver.Identity.Handle, opts.HostId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(opts.SessionsRefreshAbsoluteDays),
            userAgent: null, initialJti: minted.Jti, CancellationToken.None).GetAwaiter().GetResult();

        // A token says what tier it was minted at; the account store says what the holder may do, and
        // the store is what every gate reads. So a token minted at a tier only means anything if the
        // account behind it holds that tier — which is the production rule, not a test convenience,
        // and giving the fake identity a real account here is what keeps these tests exercising the
        // real path rather than a softer one.
        GiveTheFakeIdentityAnAccount(services, tier);
        return minted.Token;
    }

    /// <summary>Create or move the account behind the fake identity to <paramref name="tier"/>.</summary>
    internal static void GiveTheFakeIdentityAnAccount(IServiceProvider services, KgsmTier tier)
    {
        // A factory pointed at a store that will not open is testing exactly that, and minting a
        // token for it must not be the thing that fails.
        if (services.GetRequiredService<UserDirectory>().Available)
            SetAccountOn(services, FakeDiscordResolver.Identity, tier);
    }

    /// <summary>
    /// Give an identity an account on this host at a tier and status of the test's choosing — the
    /// setup a login test does before driving the callback, because what a login yields is decided by
    /// the account and not by anything the provider says.
    /// </summary>
    public KgsmUser SetAccount(KgsmIdentity identity, KgsmTier tier, UserStatus status = UserStatus.Active) =>
        SetAccountOn(Services, identity, tier, status);

    /// <summary>The account an identity proves here, or <see langword="null"/>.</summary>
    public KgsmUser? AccountOf(KgsmIdentity identity) =>
        Services.GetRequiredService<UserDirectory>().Store
            .FindByCredentialAsync(identity.Handle).GetAwaiter().GetResult();

    internal static KgsmUser SetAccountOn(
        IServiceProvider services, KgsmIdentity identity, KgsmTier tier,
        UserStatus status = UserStatus.Active)
    {
        var users = services.GetRequiredService<UserDirectory>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        KgsmUser? existing = users.Store.FindByCredentialAsync(identity.Handle).GetAwaiter().GetResult();

        KgsmUser account;
        if (existing is not null)
        {
            account = existing with { Tier = tier, Status = status, Updated = now };
            users.Store.UpdateAsync(account).GetAwaiter().GetResult();
        }
        else
        {
            account = users.Linking
                .ProvisionAsync(identity, tier, TierSource.Granted, status, now)
                .GetAwaiter().GetResult().User!;
        }

        // The cache is off in this factory, but a derived one may not be, and a stale answer here
        // would look like the gate being wrong rather than the setup being stale.
        users.Authority.ForgetAll();
        return account;
    }

    /// <summary>
    /// Seed one row straight into the local audit table.
    /// </summary>
    /// <remarks>
    /// That table holds this host's pre-cutover history and nothing appends to it any more — every
    /// producer records what it did in its own journal. Tests that exercise the LOCAL half of the merged
    /// read (keyset order, filters, the viewer gate) seed it directly, which is what that half reads.
    /// </remarks>
    public async Task SeedAuditAsync(AuditWrite write)
    {
        using IServiceScope scope = Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Audit.Add(AuditMapping.ToEntity(write, "evt_" + Guid.NewGuid().ToString("N")[..10]));
        await db.SaveChangesAsync();
    }
}
