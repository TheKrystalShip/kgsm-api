using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Boots the real API in-process with auth ON and a known signing key, with the Discord seam swapped
/// for <see cref="FakeDiscordResolver"/>. Everything else is the production pipeline — the JwtBearer
/// validation, the tier policies, the controllers — so the tier matrix exercises the real wiring, only
/// the discord.com boundary is faked. The engine/monitor are left unprovisioned so reads degrade to
/// 200 (empty roster / null capacity) with no external dependency.
/// </summary>
public sealed class AuthTestFactory : WebApplicationFactory<Program>
{
    public const string HostId = "test-host";
    public const string SigningKey = "test-signing-key-please-ignore-deterministic";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KGSM_API_HOST_ID"] = HostId,
                ["KGSM_API_AUTH_SIGNING_KEY"] = SigningKey,
                // DiscordConfigured = true so the callback path runs; the FAKE replaces the real HTTP.
                ["KGSM_API_AUTH_DISCORD_CLIENT_ID"] = "test-client",
                ["KGSM_API_AUTH_DISCORD_CLIENT_SECRET"] = "test-secret",
                ["KGSM_API_AUTH_DISCORD_REDIRECT_URI"] = "https://host.test/auth/discord/callback",
                ["KGSM_API_AUTH_DISCORD_BOT_TOKEN"] = "test-bot-token",
                ["KGSM_API_AUTH_DISCORD_GUILD_ID"] = "1234567890",
                // No engine / monitor — reads degrade to 200, no external dependency.
                ["KGSM_API_KGSM_PATH"] = "",
                ["KGSM_API_MONITOR_SOCKET"] = "/tmp/kgsm-api-tests-no-monitor.sock",
                ["KGSM_API_WATCHDOG_SOCKET"] = "",
                ["KGSM_API_DB"] = Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-{Guid.NewGuid():N}.db"),
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDiscordIdentityResolver>();
            services.AddSingleton<IDiscordIdentityResolver, FakeDiscordResolver>();
        });
    }

    /// <summary>Mint a real access token at <paramref name="tier"/> using the server's own token
    /// service (same key + host the running pipeline validates against).</summary>
    public string AccessToken(AuthTier tier)
    {
        var tokens = Services.GetRequiredService<ISessionTokenService>();
        return tokens.MintAccess(FakeDiscordResolver.Identity, tier);
    }

    /// <summary>Mint a real refresh token (8h cap) at <paramref name="tier"/>.</summary>
    public string RefreshToken(AuthTier tier)
    {
        var tokens = Services.GetRequiredService<ISessionTokenService>();
        return tokens.MintRefresh(FakeDiscordResolver.Identity, tier);
    }
}
