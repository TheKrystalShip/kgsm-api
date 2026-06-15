using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>Mints tokens with an arbitrary signing key/host — used to forge a validly-shaped but
/// wrong-signature token, proving the pipeline rejects it.</summary>
internal static class TestTokens
{
    public static string MintAccessWithKey(string signingKey, string hostId, AuthTier tier)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KGSM_API_HOST_ID"] = hostId,
                ["KGSM_API_AUTH_SIGNING_KEY"] = signingKey,
            })
            .Build();
        var tokens = new SessionTokenService(ApiOptions.FromConfiguration(config), NullLogger<SessionTokenService>.Instance);
        return tokens.MintAccess(FakeDiscordResolver.Identity, tier);
    }
}
