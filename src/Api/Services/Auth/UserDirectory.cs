using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// This host's KGSM accounts, and whether they can be reached at all.
/// </summary>
/// <remarks>
/// <para>
/// The store is a <em>shared host file</em> (<c>/var/lib/kgsm/auth/users.db</c>) that the assistant beside
/// this API opens directly too. It is deliberately not on <c>AppDbContext</c>: this API's own database
/// is operational state and is wiped whenever its schema changes, and accounts cannot be.
/// </para>
/// <para>
/// <b>Opening it can fail, and it is the whole of authorization when it does.</b> A missing
/// directory, a permission problem, or — the one worth naming — a file written by a newer build
/// sharing this host leaves this API unable to say what anyone may do. The failure is captured here
/// rather than thrown at startup, because refusing to start would let a sibling's deploy order decide
/// whether the Control Panel exists: <c>/health</c>, the unauthenticated surface and the account
/// endpoints' <c>503</c> are all still worth serving, and an operator needs a running service to read
/// the reason off. Every authenticated request answers <c>502</c> for as long as it lasts — never a
/// denial and never a grant, because "we could not ask" is neither.
/// </para>
/// <para>
/// A caller checks <see cref="Available"/> first. The accessors throw rather than returning null,
/// because a use that skipped the check is a bug in the caller and should read like one.
/// </para>
/// </remarks>
public sealed class UserDirectory
{
    private readonly SqliteUserStore? _store;
    private readonly LocalSignInService? _signIn;
    private readonly UserStoreAuthority? _authority;
    private readonly IdentityLinkService? _linking;

    public UserDirectory(ApiOptions options, ILogger<UserDirectory> logger)
    {
        try
        {
            _store = new SqliteUserStore(new UserStoreOptions { Path = options.UsersDbPath });
            _authority = new UserStoreAuthority(
                _store, TimeSpan.FromSeconds(options.AuthorityCacheSeconds));
            _linking = new IdentityLinkService(_store);
            _signIn = new LocalSignInService(_store, new IdentityPasswordHasher(), _authority);
            logger.LogInformation("KGSM account store opened at {Path}.", options.UsersDbPath);
        }
        catch (UserStoreSchemaException e)
        {
            // The loud case. Another KGSM service on this host wrote a schema this build does not
            // understand, so reading it would mean guessing at accounts — an error, not a warning.
            UnavailableReason = e.Message;
            logger.LogError(e,
                "KGSM account store at {Path} is a schema this build does not understand. Local sign-in " +
                "and account management are unavailable until this service is brought up to the same " +
                "version as the rest of the host.", options.UsersDbPath);
        }
        catch (Exception e)
        {
            UnavailableReason = $"The KGSM account store at '{options.UsersDbPath}' could not be opened.";
            logger.LogError(e,
                "KGSM account store at {Path} could not be opened. Local sign-in and account management " +
                "are unavailable; sign-in through an identity provider is unaffected.", options.UsersDbPath);
        }
    }

    /// <summary>Whether accounts can be read and written at all.</summary>
    public bool Available => _store is not null;

    /// <summary>Why not, when <see cref="Available"/> is <see langword="false"/>. Safe to show an admin.</summary>
    public string? UnavailableReason { get; }

    /// <summary>The accounts. Only valid while <see cref="Available"/>.</summary>
    public IUserStore Store =>
        _store ?? throw new InvalidOperationException("The KGSM account store is unavailable.");

    /// <summary>Username-and-password sign-in. Only valid while <see cref="Available"/>.</summary>
    public LocalSignInService SignIn =>
        _signIn ?? throw new InvalidOperationException("The KGSM account store is unavailable.");

    /// <summary>
    /// What a verified identity may do here — the host's only answer to that question. Only valid
    /// while <see cref="Available"/>.
    /// </summary>
    public UserStoreAuthority Authority =>
        _authority ?? throw new InvalidOperationException("The KGSM account store is unavailable.");

    /// <summary>
    /// Turning a verified external identity into the account it proves. Only valid while
    /// <see cref="Available"/>.
    /// </summary>
    public IdentityLinkService Linking =>
        _linking ?? throw new InvalidOperationException("The KGSM account store is unavailable.");

    /// <summary>
    /// Drop every cached authority answer for an account, so the next request on any of its sessions
    /// re-reads the store.
    /// </summary>
    /// <remarks>
    /// What an admin's own change calls, so it lands here immediately instead of waiting out the
    /// cache. Every way the account can be proved is dropped — its own local handle and each linked
    /// identity — because a session minted through one of them caches under that handle and not under
    /// the account id. Best-effort by nature: another surface on this host holds its own cache and
    /// picks the change up within its own TTL, which is the bound that actually matters.
    /// </remarks>
    public async Task ForgetAsync(string userId, CancellationToken ct = default)
    {
        if (_store is null || _authority is null)
            return;

        _authority.Forget(KgsmActor.Format(KgsmActorProvider.Local, userId));
        foreach (UserCredential credential in await _store.ListCredentialsAsync(userId, ct))
        {
            if (credential.Kind == CredentialKind.Identity)
                _authority.Forget(credential.Handle);
        }
    }
}

/// <summary>
/// The host's authority seam: the account store, or an honest outage.
/// </summary>
/// <remarks>
/// A thin wrapper rather than registering <see cref="UserStoreAuthority"/> directly, for one reason:
/// the store may not have opened, and a service that cannot be constructed takes every endpoint that
/// injects it — including the ones whose whole job is to report the problem — down with a <c>500</c>.
/// Resolving always and failing at the call turns that into the exception every consumer already
/// handles, which is a <c>502</c>: the question could not be answered, so no answer is given.
/// </remarks>
public sealed class DirectoryAuthority(UserDirectory users) : IAuthorityProvider
{
    public Task<KgsmTier> ResolveTierAsync(KgsmIdentity identity, CancellationToken ct) =>
        users.Available
            ? users.Authority.ResolveTierAsync(identity, ct)
            : throw new KgsmAuthProviderException(
                users.UnavailableReason ?? "The KGSM account store is unavailable on this host.");
}
