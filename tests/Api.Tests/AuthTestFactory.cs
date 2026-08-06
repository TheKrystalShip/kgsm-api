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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:HostId"] = HostId,
                ["Api:SigningKey"] = SigningKey,
                // DiscordConfigured = true so the callback path runs; the FAKE replaces the real HTTP.
                // The app, guild and role-lookup token live in the ecosystem's shared KgsmAuth section;
                // the redirect URI is this surface's own and stays on Api.
                ["KgsmAuth:ClientId"] = "test-client",
                ["KgsmAuth:ClientSecret"] = "test-secret",
                ["KgsmAuth:BotToken"] = "test-bot-token",
                ["KgsmAuth:GuildId"] = "1234567890",
                ["Api:DiscordRedirectUri"] = "https://host.test/auth/discord/callback",
                // Callback returns JSON by default (the contract these tests assert). The fragment-
                // handoff variant overrides this per-test. Pin it empty so a dev appsettings value
                // (Api__AuthFrontendUrl) can't flip the base suite to redirect.
                ["Api:AuthFrontendUrl"] = "",
                // No engine / monitor — reads degrade to 200, no external dependency.
                ["Api:KgsmPath"] = "",
                ["Api:MonitorSocketPath"] = "/tmp/kgsm-api-tests-no-monitor.sock",
                ["Api:WatchdogSocketPath"] = "",
                ["Api:DbPath"] = Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-{Guid.NewGuid():N}.db"),
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDiscordDirectory>();
            services.AddSingleton<IDiscordDirectory, FakeDiscordResolver>();
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
        store.CreateAsync(sid, $"discord:{FakeDiscordResolver.Identity.UserId}", opts.HostId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(opts.SessionsRefreshAbsoluteDays),
            userAgent: null, initialJti: minted.Jti, CancellationToken.None).GetAwaiter().GetResult();
        return minted.Token;
    }
}
