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
/// <b>Opening it can fail, and that must not take the panel down.</b> A missing directory, a
/// permission problem, or — the one worth naming — a file written by a newer build sharing this host
/// all leave local accounts unreachable while Discord sign-in, every domain read and every command
/// keep working. So the failure is captured here and reported as a capability, exactly like a leaf
/// being down: the account endpoints answer <c>503</c> with the reason, and nothing else notices.
/// Refusing to start instead would let a sibling's deploy order decide whether the Control Panel
/// exists.
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

    public UserDirectory(ApiOptions options, ILogger<UserDirectory> logger)
    {
        try
        {
            _store = new SqliteUserStore(new UserStoreOptions { Path = options.UsersDbPath });
            _signIn = new LocalSignInService(_store, new IdentityPasswordHasher(), new UserStoreAuthority(_store));
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
}
