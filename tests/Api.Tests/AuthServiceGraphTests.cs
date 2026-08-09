using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The auth graph as PRODUCTION builds it. Every other test in this project replaces
/// <see cref="ISignInService"/> with a fake, which is what makes the tier matrix testable — and
/// also means none of them ever constructs the real one. A dependency the real implementation needs
/// and the container cannot supply is therefore invisible to the whole suite and surfaces as a 500 on
/// the first login attempt against a deployed host.
/// </summary>
public sealed class AuthServiceGraphTests
{
    [Fact]
    public void TheRealSignInGraphCanBeConstructed()
    {
        using var factory = new WebApplicationFactory<Program>();
        using IServiceScope scope = factory.Services.CreateScope();

        ISignInService signIn = scope.ServiceProvider.GetRequiredService<ISignInService>();

        Assert.Equal(KgsmActorProvider.Discord, signIn.Provider);
        // Exercise it far enough to touch every injected dependency; building the URL reads the
        // options, the endpoints and nothing over the network.
        Assert.StartsWith("https://discord.com/api/oauth2/authorize", signIn.BuildAuthorizeUrl("s", "c", "none"));
    }

    [Fact]
    public void BothHalvesOfTheSignInAreServedByTheDiscordClient()
    {
        // Identity and authority are separate seams deliberately, but on this host one implementation
        // answers both. Resolving each on its own proves the registrations exist and are satisfiable —
        // a half nothing supplies is a 500 on the first login, and no other test constructs these.
        using var factory = new WebApplicationFactory<Program>();
        using IServiceScope scope = factory.Services.CreateScope();

        Assert.IsType<DiscordDirectory>(scope.ServiceProvider.GetRequiredService<IIdentityProvider>());
        Assert.IsType<DiscordDirectory>(scope.ServiceProvider.GetRequiredService<IAuthorityProvider>());
    }

    [Fact]
    public void TheSignInGraphIsTransient()
    {
        // A typed HttpClient held in a singleton pins one handler forever, so HttpClientFactory stops
        // rotating and a DNS change never lands. This is the assertion that keeps someone from
        // "optimising" the sign-in registrations to singletons later.
        using var factory = new WebApplicationFactory<Program>();
        using IServiceScope scope = factory.Services.CreateScope();

        Assert.NotSame(
            scope.ServiceProvider.GetRequiredService<ISignInService>(),
            scope.ServiceProvider.GetRequiredService<ISignInService>());
    }
}
