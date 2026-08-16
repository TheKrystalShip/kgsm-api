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
    /// The production graph on a host wired to a provider — no fake anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <c>Api:UsersDbPath</c> defaults to <c>/var/lib/kgsm/auth/users.db</c> — the HOST's real account
    /// store, shared with every KGSM service on the box — and resolving <c>UserDirectory</c> opens it,
    /// which creates it. A graph test must not hand the operator a live accounts file nobody made, so
    /// it is redirected.
    /// </para>
    /// <para>
    /// The application and the redirect URI are pinned because a host with neither offers no provider
    /// at all, which is a real state with its own test below — but not the one that proves the real
    /// provider is constructible. Everything else is built exactly as production builds it.
    /// </para>
    /// </remarks>
    private static WebApplicationFactory<Program> RealGraph(
        string? usersDbPath = null, bool wired = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Api:UsersDbPath"] = usersDbPath
                        ?? Path.Combine(Path.GetTempPath(), $"kgsm-api-graph-users-{Guid.NewGuid():N}.db"),
                    // ⚠ Never the default. This factory is deliberately fake-free, so everything it does
                    // not override is the real host's — and booting the app now writes a leaf_ready to
                    // whatever journal it resolves. Measured: a test run put twenty-five ready/stopping
                    // pairs into this machine's live API journal, which reads as an API that restarted
                    // twenty-five times when systemd shows two.
                    ["Api:EventJournalDir"] =
                        Path.Combine(Path.GetTempPath(), $"kgsm-api-graph-events-{Guid.NewGuid():N}"),
                    ["Api:JournalStateRoot"] =
                        Path.Combine(Path.GetTempPath(), $"kgsm-api-graph-state-{Guid.NewGuid():N}"),
                    ["Api:KgsmJournalDir"] =
                        Path.Combine(Path.GetTempPath(), $"kgsm-api-graph-journal-{Guid.NewGuid():N}"),
                    ["Api:DbPath"] =
                        Path.Combine(Path.GetTempPath(), $"kgsm-api-graph-{Guid.NewGuid():N}.db"),
                    ["KgsmAuth:Providers:discord:ClientId"] = wired ? "graph-client" : "",
                    ["KgsmAuth:Providers:discord:ClientSecret"] = wired ? "graph-secret" : "",
                    ["Api:DiscordRedirectUri"] =
                        wired ? "https://host.test/auth/discord/callback" : "",
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

        ISignInService signIn = Assert.IsType<SignInService>(
            scope.ServiceProvider.GetRequiredService<IAuthProviderCatalog>()
                .SignIn(KgsmActorProvider.Discord));

        Assert.Equal(KgsmActorProvider.Discord, signIn.Provider);
        // Exercise it far enough to touch every injected dependency; building the URL reads the
        // application, the endpoints and nothing over the network.
        Assert.StartsWith("https://discord.com/api/oauth2/authorize", signIn.BuildAuthorizeUrl("s", "c", "none"));
    }

    [Fact]
    public void TheTwoFlowsGoToTwoDifferentCallbacks()
    {
        // A sign-in and an attach end differently, so they run against different redirect URIs — and
        // every provider requires the URI at the exchange to match the one at the bounce. Two flows
        // that named one address would fail at the provider, where no log here would see it.
        using WebApplicationFactory<Program> factory = RealGraph();
        using IServiceScope scope = factory.Services.CreateScope();
        IAuthProviderCatalog catalog = scope.ServiceProvider.GetRequiredService<IAuthProviderCatalog>();

        string login = catalog.SignIn(KgsmActorProvider.Discord)!.BuildAuthorizeUrl("s", "c", "none");
        string link = catalog.Link(KgsmActorProvider.Discord)!.BuildAuthorizeUrl("s", "c", "consent");

        Assert.Contains(Uri.EscapeDataString("https://host.test/auth/discord/callback"), login);
        Assert.Contains(Uri.EscapeDataString("https://host.test/auth/identities/discord/callback"), link);
    }

    [Fact]
    public void TheTwoHalvesOfTheSignInComeFromTwoDifferentPlaces()
    {
        // The whole shape of the model, in two lines. A provider says who someone is; the account
        // store says what they may do, and it is the only thing that ever does. Resolving each on its
        // own also proves the registrations are satisfiable — a half nothing supplies is a 500 on the
        // first login, and no other test constructs these.
        using WebApplicationFactory<Program> factory = RealGraph();
        using IServiceScope scope = factory.Services.CreateScope();

        Assert.IsType<DiscordDirectory>(
            scope.ServiceProvider.GetRequiredService<IAuthProviderCatalog>().Link(KgsmActorProvider.Discord));
        Assert.IsType<DirectoryAuthority>(scope.ServiceProvider.GetRequiredService<IAuthorityProvider>());
    }

    [Fact]
    public void AHostWiredToNoProviderOffersNone()
    {
        // Not a failure to start: password sign-in still works, and the endpoints that would bounce a
        // browser answer 503 rather than 500. A provider with no application and one nothing has ever
        // registered are the same answer here.
        using WebApplicationFactory<Program> factory = RealGraph(wired: false);
        using IServiceScope scope = factory.Services.CreateScope();
        IAuthProviderCatalog catalog = scope.ServiceProvider.GetRequiredService<IAuthProviderCatalog>();

        Assert.Empty(catalog.Configured);
        Assert.Null(catalog.SignIn(KgsmActorProvider.Discord));
        Assert.Null(catalog.SignIn("nobody-has-heard-of-this"));
        // Still registered, though — what this host COULD offer is a different question from what it
        // does, and the Settings page draws on the difference.
        Assert.Equal([KgsmActorProvider.Discord], catalog.Registered);
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
    public void TheSignInGraphIsBuiltFresh()
    {
        // A typed HttpClient held for the life of the process pins one handler, so HttpClientFactory
        // stops rotating and a DNS change never lands. This is the assertion that keeps someone from
        // "optimising" the catalog into handing out one cached provider later.
        using WebApplicationFactory<Program> factory = RealGraph();
        using IServiceScope scope = factory.Services.CreateScope();
        IAuthProviderCatalog catalog = scope.ServiceProvider.GetRequiredService<IAuthProviderCatalog>();

        Assert.NotSame(
            catalog.SignIn(KgsmActorProvider.Discord),
            catalog.SignIn(KgsmActorProvider.Discord));
    }
}
