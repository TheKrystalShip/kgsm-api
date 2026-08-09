using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Api.Services.Auth;

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
    /// <summary>
    /// The production graph, with exactly one setting pinned.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Api:UsersDbPath</c> defaults to <c>/var/lib/kgsm/auth/users.db</c> — the HOST's real account
    /// store, shared with every KGSM service on the box — and resolving <c>UserDirectory</c> opens it,
    /// which creates it. A graph test must not hand the operator a live accounts file nobody made, so
    /// this one setting is redirected and nothing else is. Everything the tests below assert is built
    /// exactly as production builds it.
    /// </remarks>
    private static WebApplicationFactory<Program> RealGraph(string? usersDbPath = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Api:UsersDbPath"] = usersDbPath
                        ?? Path.Combine(Path.GetTempPath(), $"kgsm-api-graph-users-{Guid.NewGuid():N}.db"),
                })));

    [Fact]
    public void TheRealAccountStoreGraphCanBeConstructed()
    {
        // The other half of the login path, and the one no other test here builds: a store that
        // cannot be constructed is a 503 on every password sign-in against a deployed host.
        using WebApplicationFactory<Program> factory = RealGraph();
        using IServiceScope scope = factory.Services.CreateScope();

        UserDirectory users = scope.ServiceProvider.GetRequiredService<UserDirectory>();

        Assert.True(users.Available, users.UnavailableReason);
        Assert.NotNull(users.SignIn);
    }

    [Fact]
    public void TheAccountStoreIsOneInstanceForTheProcess()
    {
        // Unlike the sign-in seams, which are transient because a typed HttpClient underneath them
        // must keep rotating. This one wraps a file: a second instance would re-run the schema check
        // on every request for nothing.
        using WebApplicationFactory<Program> factory = RealGraph();
        using IServiceScope scope = factory.Services.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<UserDirectory>(),
            scope.ServiceProvider.GetRequiredService<UserDirectory>());
    }

    [Fact]
    public void TheRealSignInGraphCanBeConstructed()
    {
        using WebApplicationFactory<Program> factory = RealGraph();
        using IServiceScope scope = factory.Services.CreateScope();

        ISignInService signIn = scope.ServiceProvider.GetRequiredService<ISignInService>();

        Assert.Equal(KgsmActorProvider.Discord, signIn.Provider);
        // Exercise it far enough to touch every injected dependency; building the URL reads the
        // options, the endpoints and nothing over the network.
        Assert.StartsWith("https://discord.com/api/oauth2/authorize", signIn.BuildAuthorizeUrl("s", "c", "none"));
    }

    [Fact]
    public void TheTwoHalvesOfTheSignInComeFromTwoDifferentPlaces()
    {
        // The whole shape of the model, in two lines. Discord says who someone is; the account store
        // says what they may do, and it is the only thing that ever does. Resolving each on its own
        // also proves the registrations are satisfiable — a half nothing supplies is a 500 on the
        // first login, and no other test constructs these.
        using WebApplicationFactory<Program> factory = RealGraph();
        using IServiceScope scope = factory.Services.CreateScope();

        Assert.IsType<DiscordDirectory>(scope.ServiceProvider.GetRequiredService<IIdentityProvider>());
        Assert.IsType<DirectoryAuthority>(scope.ServiceProvider.GetRequiredService<IAuthorityProvider>());
    }

    [Fact]
    public async Task AnUnreachableAccountStoreIsAnOutageAtTheCallAndNotAMissingService()
    {
        // The authority seam must resolve even when the file behind it will not open, because the
        // endpoints that report the problem inject it too — a service that cannot be constructed
        // takes them down with a 500 and the operator learns nothing.
        using WebApplicationFactory<Program> factory = RealGraph(usersDbPath: "/proc/version/nope/users.db");
        using IServiceScope scope = factory.Services.CreateScope();

        var authority = scope.ServiceProvider.GetRequiredService<IAuthorityProvider>();

        await Assert.ThrowsAsync<KgsmAuthProviderException>(
            () => authority.ResolveTierAsync(FakeDiscordResolver.Identity, default));
    }

    [Fact]
    public void TheSignInGraphIsTransient()
    {
        // A typed HttpClient held in a singleton pins one handler forever, so HttpClientFactory stops
        // rotating and a DNS change never lands. This is the assertion that keeps someone from
        // "optimising" the sign-in registrations to singletons later.
        using WebApplicationFactory<Program> factory = RealGraph();
        using IServiceScope scope = factory.Services.CreateScope();

        Assert.NotSame(
            scope.ServiceProvider.GetRequiredService<ISignInService>(),
            scope.ServiceProvider.GetRequiredService<ISignInService>());
    }
}
