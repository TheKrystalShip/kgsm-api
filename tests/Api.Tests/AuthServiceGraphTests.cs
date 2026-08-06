using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth.Discord;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The auth graph as PRODUCTION builds it. Every other test in this project replaces
/// <see cref="IDiscordDirectory"/> with a fake, which is what makes the tier matrix testable — and
/// also means none of them ever constructs the real one. A dependency the real implementation needs
/// and the container cannot supply is therefore invisible to the whole suite and surfaces as a 500 on
/// the first login attempt against a deployed host.
/// </summary>
public sealed class AuthServiceGraphTests
{
    [Fact]
    public void TheRealDiscordSeamCanBeConstructed()
    {
        using var factory = new WebApplicationFactory<Program>();
        using IServiceScope scope = factory.Services.CreateScope();

        IDiscordDirectory directory = scope.ServiceProvider.GetRequiredService<IDiscordDirectory>();

        Assert.IsType<DiscordDirectory>(directory);
        // Exercise it far enough to touch every injected dependency; building the URL reads the
        // options, the endpoints and nothing over the network.
        Assert.StartsWith("https://discord.com/api/oauth2/authorize", directory.BuildAuthorizeUrl("s", "c", "none"));
    }
}
