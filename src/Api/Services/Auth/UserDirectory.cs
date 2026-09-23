using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Where an identity stands on this host right now: the account it proves, what that account may do,
/// and what state it is in.
/// </summary>
/// <param name="AccountId">
/// The account's id, or <see langword="null"/> when the identity proves none. It is the id and not a
/// handle because it is the one name every credential on an account shares — a person signed in
/// through a linked provider and the same person signed in with a password are one account here.
/// </param>
/// <param name="Tier">
/// The tier to authorize on — the account's <em>effective</em> tier, so one awaiting approval is
/// <see cref="KgsmTier.None"/> whatever its record carries.
/// </param>
/// <param name="Status">
/// The account's state in wire form (<c>active</c>, <c>pending</c>, <c>disabled</c>), or
/// <see cref="UnknownStatus"/> when the identity proves no account here or the store could not be
/// read. <c>none</c> and <c>unknown</c> together are what let a panel tell somebody waiting on an
/// admin from somebody this host has never heard of.
/// </param>
public sealed record AccountStanding(string? AccountId, KgsmTier Tier, string Status)
{
    /// <summary>The status of an account this host cannot name — never a guessed one.</summary>
    public const string UnknownStatus = "unknown";
}

/// <summary>
/// This node's replica of the cluster's accounts, and whether it can be read at all.
/// </summary>
/// <remarks>
/// <para>
/// The accounts belong to the auth anchor, which is their only writer. This node holds a read-only
/// copy in the shared host file (<c>/var/lib/kgsm/auth/users.db</c>), kept current by replication, and
/// resolves every request's authority from it — so a demotion lands on the next request and an anchor
/// outage costs nothing this node serves. On a machine that is a cluster of one the anchor's store and
/// this replica are the same file. It is deliberately not on <c>AppDbContext</c>: this API's own
/// database is operational state and is wiped whenever its schema changes, and accounts cannot be.
/// </para>
/// <para>
/// <b>Opening it can fail, and it is the whole of authorization when it does.</b> A missing
/// directory, a permission problem, or — the one worth naming — a file written by a newer build
/// sharing this host leaves this API unable to say what anyone may do. The failure is captured here
/// rather than thrown at startup, because refusing to start would let a sibling's deploy order decide
/// whether the Control Panel exists: <c>/health</c> and the unauthenticated surface are still worth
/// serving, and an operator needs a running service to read the reason off. Every authenticated
/// request answers <c>502</c> for as long as it lasts — never a denial and never a grant, because "we
/// could not ask" is neither.
/// </para>
/// <para>
/// A caller checks <see cref="Available"/> first. The accessors throw rather than returning null,
/// because a use that skipped the check is a bug in the caller and should read like one.
/// </para>
/// </remarks>
public sealed class UserDirectory : IReplicatedAccounts, IMemberAccounts
{
    private readonly SqliteUserStore? _store;
    private readonly UserStoreAuthority? _authority;
    private readonly AccountReplica? _replica;

    public UserDirectory(ApiOptions options, ILogger<UserDirectory> logger)
    {
        try
        {
            _store = new SqliteUserStore(new UserStoreOptions { Path = options.UsersDbPath });
            _authority = new UserStoreAuthority(
                _store, TimeSpan.FromSeconds(options.AuthorityCacheSeconds));
            // The counter that orders what replication delivers. Built here rather than registered
            // separately so it inherits this type's whole answer to an unreadable store: a node that
            // cannot read accounts reports it once, as a capability, instead of failing a replication
            // message with an exception the sender would retry.
            _replica = new AccountReplica(_store, new SqliteAccountVersions(
                new UserStoreOptions { Path = options.UsersDbPath }));
            logger.LogInformation("KGSM account replica opened at {Path}.", options.UsersDbPath);
        }
        catch (UserStoreSchemaException e)
        {
            // The loud case. Another KGSM service on this host wrote a schema this build does not
            // understand, so reading it would mean guessing at accounts — an error, not a warning.
            UnavailableReason = e.Message;
            logger.LogError(e,
                "KGSM account replica at {Path} is a schema this build does not understand. Nobody's " +
                "authority can be resolved here until this service is brought up to the same version as " +
                "the rest of the host.", options.UsersDbPath);
        }
        catch (Exception e)
        {
            UnavailableReason = $"The KGSM account replica at '{options.UsersDbPath}' could not be opened.";
            logger.LogError(e,
                "KGSM account replica at {Path} could not be opened. Nobody's authority can be resolved " +
                "here until it can.", options.UsersDbPath);
        }
    }

    /// <summary>Whether the replica can be read at all.</summary>
    public bool Available => _store is not null;

    /// <summary>Why not, when <see cref="Available"/> is <see langword="false"/>. Safe to show an admin.</summary>
    public string? UnavailableReason { get; }

    /// <summary>
    /// The accounts a member-acting call is resolved against, absent rather than throwing.
    /// </summary>
    /// <remarks>
    /// Explicit, because the two contracts differ where it matters: reaching for the store in ordinary
    /// code is a bug when there is none, while a member-acting call has to tell an unreadable store apart
    /// from a person this node does not have — one is an outage and the other is a refusal, and an
    /// exception would collapse them.
    /// </remarks>
    IUserStore? IMemberAccounts.Store => _store;

    /// <summary>
    /// What a verified identity may do here — the host's only answer to that question. Only valid
    /// while <see cref="Available"/>.
    /// </summary>
    public UserStoreAuthority Authority =>
        _authority ?? throw new InvalidOperationException("The KGSM account store is unavailable.");

    /// <summary>
    /// This node's own copy of the cluster's accounts, or <see langword="null"/> when the store
    /// cannot be read.
    /// </summary>
    /// <remarks>
    /// Null rather than throwing, unlike its neighbours, because its caller is a message handler
    /// rather than a request: an exception there is a <c>500</c>, which the sender reads as transient
    /// and retries forever against a node whose store will not become readable by being asked again.
    /// </remarks>
    public AccountReplica? Replica => _replica;

    /// <summary>
    /// Where <paramref name="identity"/> stands on this host — the one answer <c>GET /me</c> and the
    /// <c>me</c> stream topic both speak, so the two can never describe the same person differently.
    /// </summary>
    /// <remarks>
    /// Goes through the cached authority resolution, which is the same read every request already
    /// makes, so asking again on a request that has just authenticated costs nothing.
    /// <para>
    /// A store that cannot be read <b>throws</b> <see cref="KgsmAuthProviderException"/> rather than
    /// answering <see cref="KgsmTier.None"/>. "We could not ask" is a third answer here as everywhere
    /// else in this model, and flattening it into a tier would have an outage report every live
    /// session as demoted — a fabricated standing, and the one thing this ecosystem never emits.
    /// Each caller decides what to do with the silence: <c>/me</c> renders
    /// <see cref="AccountStanding.UnknownStatus"/>, a live stream ends and lets the redial re-run the
    /// full authentication pipeline, which is the authority.
    /// </para>
    /// </remarks>
    public async Task<AccountStanding> StandingAsync(KgsmIdentity identity, CancellationToken ct = default)
    {
        if (_authority is null)
        {
            throw new KgsmAuthProviderException(
                UnavailableReason ?? "The KGSM account store is unavailable on this host.");
        }

        AuthorityAnswer answer = await _authority.ResolveAsync(identity, ct);
        return answer.User is { } account
            ? new AccountStanding(account.UserId, answer.Tier, UserStatuses.ToWire(account.Status))
            : new AccountStanding(null, answer.Tier, AccountStanding.UnknownStatus);
    }

    /// <summary>
    /// Drop every cached authority answer for an account and return it as the replica holds it now,
    /// or <see langword="null"/> when it is gone or the replica cannot be read.
    /// </summary>
    /// <remarks>
    /// What replication calls once it has applied a change, so the next request on any of the
    /// account's sessions reads the new record rather than waiting out the cache. Every way the
    /// account can be proved is dropped — its own local handle and each linked identity — because a
    /// session keyed by one of them caches under that handle and not under the account id.
    /// </remarks>
    public async Task<KgsmUser?> ReloadAsync(string userId, CancellationToken ct = default)
    {
        if (_store is null || _authority is null)
            return null;

        _authority.Forget(KgsmActor.Format(KgsmActorProvider.Local, userId));
        foreach (UserCredential credential in await _store.ListCredentialsAsync(userId, ct))
        {
            if (credential.Kind == CredentialKind.Identity)
                _authority.Forget(credential.Handle);
        }

        return await _store.FindByIdAsync(userId, ct);
    }
}
