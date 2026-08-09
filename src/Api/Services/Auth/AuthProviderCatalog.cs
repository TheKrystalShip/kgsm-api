using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// How one identity provider is built. Registered once per provider at composition, which is the
/// only place in this API a provider is named — a controller is handed a name off the route and asks
/// the catalog, so adding a provider is a registration here and nothing anywhere else.
/// </summary>
/// <param name="Provider">The <see cref="KgsmActorProvider"/> value this entry speaks for.</param>
/// <param name="Create">
/// Builds the provider against one application and one redirect URI. A factory rather than an
/// instance because these wrap typed <see cref="HttpClient"/>s: holding one for the process lifetime
/// pins its handler and silently stops the factory rotating it, so DNS changes never land.
/// </param>
public sealed record AuthProviderRegistration(
    string Provider,
    Func<IServiceProvider, KgsmOAuthApplication, string, IIdentityProvider> Create);

/// <summary>
/// The identity providers this host can sign somebody in through, and how to reach each one for
/// either of the two flows that use it.
/// </summary>
/// <remarks>
/// <para>
/// A sign-in and an attach end differently — one mints a session, the other adds a credential to an
/// account that already exists — so they run against separate callback addresses, and the provider
/// is told which at both the bounce and the exchange because every provider requires the two to
/// match. That is the whole reason this hands out two shapes rather than one.
/// </para>
/// <para>
/// <see cref="SignIn"/> composes the identity half with the authority half; <see cref="Link"/> is the
/// identity half alone, because attaching an account asks who somebody is and never what they may do.
/// </para>
/// </remarks>
public interface IAuthProviderCatalog
{
    /// <summary>
    /// The providers this host is wired to, in the order they were registered. Deliberately ordered:
    /// it is the order a login page draws its buttons in.
    /// </summary>
    IReadOnlyList<string> Configured { get; }

    /// <summary>
    /// Every provider this build can speak to, wired up here or not — what an account <em>could</em>
    /// attach if an admin supplied an application, which is a different question from what it can
    /// attach today.
    /// </summary>
    IReadOnlyList<string> Registered { get; }

    /// <summary>Whether this host can sign somebody in through <paramref name="provider"/>.</summary>
    bool IsConfigured(string provider);

    /// <summary>
    /// The whole sign-in for <paramref name="provider"/>, or <see langword="null"/> when this host
    /// does not offer it — which is the same answer for a provider nobody wired up and one nobody has
    /// heard of.
    /// </summary>
    ISignInService? SignIn(string provider);

    /// <summary>The identity half alone, pointed at the link callback. <see langword="null"/> as above.</summary>
    IIdentityProvider? Link(string provider);
}

/// <inheritdoc cref="IAuthProviderCatalog"/>
public sealed class AuthProviderCatalog : IAuthProviderCatalog
{
    private readonly IServiceProvider _services;
    private readonly ApiOptions _options;
    private readonly List<AuthProviderRegistration> _registrations;

    public AuthProviderCatalog(
        IServiceProvider services,
        IEnumerable<AuthProviderRegistration> registrations,
        ApiOptions options)
    {
        _services = services;
        _options = options;
        _registrations = [.. registrations];
    }

    public IReadOnlyList<string> Configured =>
        [.. _registrations.Select(r => r.Provider).Where(IsConfigured)];

    public IReadOnlyList<string> Registered => [.. _registrations.Select(r => r.Provider)];

    public bool IsConfigured(string provider) =>
        Find(provider) is not null && _options.ProviderConfigured(provider);

    public ISignInService? SignIn(string provider) =>
        Identity(provider, _options.LoginRedirectUri(provider)) is { } identity
            ? new SignInService(identity, _services.GetRequiredService<IAuthorityProvider>())
            : null;

    public IIdentityProvider? Link(string provider) =>
        Identity(provider, _options.LinkRedirectUri(provider));

    private IIdentityProvider? Identity(string provider, string redirectUri) =>
        IsConfigured(provider) && Find(provider) is { } registration
            ? registration.Create(_services, _options.OAuth.For(provider), redirectUri)
            : null;

    // Case-insensitive because the name arrives off a route, and a route value is whatever the
    // browser sent. The credential handles it ends up in are the provider's own spelling, which the
    // provider states rather than the caller.
    private AuthProviderRegistration? Find(string provider) =>
        _registrations.FirstOrDefault(
            r => string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase));
}
