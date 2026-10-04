using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using TheKrystalShip.Api;

using TheKrystalShip.Auth;
using TheKrystalShip.Auth.Users;

using TheKrystalShip.Auth.Cluster;
using TheKrystalShip.Auth.Minting;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Boots the real API in-process with auth ON, standing in for the cluster's auth anchor with a signer
/// of its own and an authority store of its own (<see cref="TestAuthority"/>). Everything else is the
/// production pipeline — the JwtBearer validation against the anchor's published key, the ended-session
/// check, the account read from the replica, the action gates, the controllers — so the access matrix
/// exercises the real wiring. The engine/monitor are left unprovisioned so reads degrade to 200 (empty
/// roster / null capacity) with no external dependency.
/// </summary>
public class AuthTestFactory : WebApplicationFactory<Program>
{
    public const string HostId = "test-host";

    /// <summary>The audience every session the stand-in anchor mints carries: the cluster.</summary>
    public const string ClusterId = "test-cluster";

    /// <summary>The issuer the stand-in anchor stamps, which is the anchor's rather than this API's.</summary>
    public const string AnchorIssuer = "kgsm";

    /// <summary>
    /// The stand-in anchor's private key. One for the whole run: a node verifies against what the anchor
    /// publishes, and every factory is told the same thing, so a session minted through one factory's
    /// helper verifies on any derived factory that inherits the registration.
    /// </summary>
    public static readonly EcdsaSessionSigner AnchorSigner = EcdsaSessionSigner.Generate();

    /// <summary>The stand-in anchor's token service: asymmetric, audienced to the cluster.</summary>
    public static readonly SessionTokenService Anchor = new(
        new SessionTokenOptions(
            Audience: ClusterId,
            AccessLifetime: TimeSpan.FromMinutes(15),
            RefreshLifetime: TimeSpan.FromDays(30),
            Issuer: AnchorIssuer),
        AnchorSigner);

    /// <summary>What gossip would have delivered from the member holding the accounts.</summary>
    public sealed record PublishedAnchor(string? Audience, string? Issuer, IReadOnlyList<SecurityKey> Keys)
        : IClusterSessionKeys
    {
        /// <summary>The stand-in anchor, as every factory is told about it.</summary>
        public static PublishedAnchor Default { get; } =
            new(ClusterId, AnchorIssuer, SessionKeys.VerificationKeysFrom(AnchorSigner.PublicKeys));

        /// <summary>A node that has not heard from any anchor.</summary>
        public static PublishedAnchor Nothing { get; } = new(null, null, []);
    }

    /// <summary>
    /// A database in a state directory of its own, for one factory, so the files the API keeps beside
    /// its database are never shared between factories in the run.
    /// </summary>
    public static string NewDbPath(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "kgsm-api.db");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:HostId"] = HostId,
                // No engine / monitor — reads degrade to 200, no external dependency.
                ["Api:KgsmPath"] = "",
                ["Api:MonitorSocketPath"] = "/tmp/kgsm-api-tests-no-monitor.sock",
                ["Api:WatchdogSocketPath"] = "",
                ["Api:DbPath"] = NewDbPath("kgsm-api-tests"),
                // Its own journal per factory. The default puts events/ beside the database, which
                // would work now that each factory has a state directory to itself — it is named
                // anyway so the isolation does not silently depend on that.
                ["Api:EventJournalDir"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-events-{Guid.NewGuid():N}"),
                // Scan a directory that has no journals in it. The default is the machine's real
                // /var/lib, so a test host would otherwise merge THIS machine's watchdog and monitor
                // history into every assertion about what a test just did.
                ["Api:JournalStateRoot"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-state-{Guid.NewGuid():N}"),
                // The engine's journal is named explicitly rather than scanned, so isolating the state
                // root above does not cover it. Left at its default, every test asserting what the feed
                // holds would be reading THIS machine's real engine history.
                ["Api:KgsmJournalDir"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-journal-{Guid.NewGuid():N}"),
                // Same rule again, for the directory that says which leaves are installed. The default
                // is the machine's real /var/lib/kgsm/leaves, and an unconfigured leaf endpoint resolves
                // against it — so a developer's own host would decide whether "the assistant is absent"
                // is true in a test that never mentioned the assistant.
                ["Api:LeafDescriptorDir"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-leaves-{Guid.NewGuid():N}"),
                // And the directory that says which components are ANCHORS here. The default is the
                // machine's real /var/lib/kgsm/anchors, and a component described there is subtracted from
                // the services board — so a developer's own host would decide which leaves a test sees.
                ["Api:AnchorDescriptorDir"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-anchors-{Guid.NewGuid():N}"),
                // Never the default. /run holds the sockets of the leaves ACTUALLY RUNNING on this
                // machine, and a leaf that answers for itself is relayed to rather than read from
                // disk — so an unpinned run would read the developer's own live daemons.
                ["Api:LeafSurfaceRoot"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-surfaces-{Guid.NewGuid():N}"),
                // Never the default. /var/lib/kgsm/auth/users.db is the MACHINE's real replica, shared
                // with every KGSM service on the box, and this API creates it — so an unpinned test run
                // would hand the operator a live authority file that nobody made.
                ["Api:UsersDbPath"] = Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-users-{Guid.NewGuid():N}.db"),
                // Never the default. http://127.0.0.1:8098 is where the machine running the suite keeps
                // its real auth anchor, and a clustered test node with an empty roster would introduce
                // itself to it. A test about the local join names its own target.
                ["Api:LocalAnchorUrl"] = "",
                // Never the default. /var/lib/kgsm/cluster/auth-provider.json is what the leaves on the
                // machine running the suite verify sessions against, and a test node knowing a stand-in
                // anchor would rewrite it — or, knowing none, remove it.
                ["Api:HostProviderFilePath"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-provider-{Guid.NewGuid():N}.json"),
                // Never the default either: /etc/kgsm/cluster-founded describes the machine running the
                // suite. A test about the local join writes its own.
                ["Cluster:FoundedPath"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-founded-{Guid.NewGuid():N}"),
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // What gossip would have delivered: the stand-in anchor's key, audience and issuer.
            services.RemoveAll<IClusterSessionKeys>();
            services.AddSingleton<IClusterSessionKeys>(PublishedAnchor.Default);
        });
    }

    /// <summary>
    /// A session the stand-in anchor minted for the one standing identity, whose account on this node
    /// holds <paramref name="persona"/>.
    /// </summary>
    public string AccessToken(Persona persona) => MintAccessOn(Services, persona);

    /// <summary>
    /// A refresh token the stand-in anchor genuinely signed, for proving one is never accepted as a
    /// bearer: a refresh token is spent at the anchor and nowhere else.
    /// </summary>
    public string RefreshToken(Persona persona)
    {
        GiveTheFakeIdentityAnAccount(Services, persona);
        return Anchor.MintRefresh(TestIdentity.Identity, NewSessionId()).Token;
    }

    /// <summary>
    /// A session the stand-in anchor minted for <paramref name="identity"/>, with an account on this
    /// node holding <paramref name="persona"/> at <paramref name="status"/> — the whole setup for one
    /// person.
    /// </summary>
    /// <remarks>
    /// <see cref="AccessToken"/> mints for the one standing identity, so every token it hands out is
    /// the same person holding whichever persona was asked for last. A test about who a frame reaches,
    /// or about one account changing while another watches, needs two people, and this is how it gets
    /// them: <see cref="TestIdentity.IdentityFor"/> names one, and this gives them a session
    /// and an account of their own.
    /// </remarks>
    public string AccessTokenFor(KgsmIdentity identity, Persona persona, UserStatus status = UserStatus.Active)
    {
        SetAccount(identity, persona, status);
        return MintAccess(identity);
    }

    /// <summary>A session the stand-in anchor minted for <paramref name="identity"/>, and nothing else.</summary>
    public static string MintAccess(KgsmIdentity identity) =>
        Anchor.MintAccess(identity, NewSessionId()).Token;

    private static string NewSessionId() => "sid_test_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// A session for the standing identity, whose account is set through <paramref name="services"/>.
    /// </summary>
    /// <remarks>
    /// Takes an <see cref="IServiceProvider"/> so a factory built via <c>WithWebHostBuilder</c> — a
    /// DERIVED factory with its OWN random database, replica and service provider — gets the account
    /// written where its own requests will read it.
    /// </remarks>
    internal static string MintAccessOn(IServiceProvider services, Persona persona)
    {
        // A token names who; the replica says what they may do, and the replica is what every gate
        // reads. So a token only means anything once the account behind it holds the persona — which
        // is the production rule, not a test convenience.
        GiveTheFakeIdentityAnAccount(services, persona);
        return MintAccess(TestIdentity.Identity);
    }

    /// <summary>Create or move the account behind the fake identity to <paramref name="persona"/>.</summary>
    internal static void GiveTheFakeIdentityAnAccount(IServiceProvider services, Persona persona)
    {
        // A factory pointed at a replica that will not open is testing exactly that, and minting a
        // token for it must not be the thing that fails.
        if (services.GetRequiredService<IReplicatedAuthority>().Replica is not null)
            SetAccountOn(services, TestIdentity.Identity, persona);
    }

    /// <summary>
    /// Give an identity an account on this node holding a persona at a status of the test's choosing —
    /// what replication from the anchor would have delivered.
    /// </summary>
    public KgsmUser SetAccount(KgsmIdentity identity, Persona persona, UserStatus status = UserStatus.Active) =>
        SetAccountOn(Services, identity, persona, status);

    /// <summary>The account an identity proves here, or <see langword="null"/>.</summary>
    public KgsmUser? AccountOf(KgsmIdentity identity) => TestAuthority.For(Services).AccountOf(identity);

    /// <summary>The replica file a factory's node reads, opened directly.</summary>
    internal static SqliteAuthorityStore ReplicaOf(IServiceProvider services) => TestAuthority.For(services).Replica;

    /// <summary>
    /// Set the account at the stand-in anchor, deliver it to the node's replica, and tell the node the
    /// replica changed — the three steps replication performs.
    /// </summary>
    internal static KgsmUser SetAccountOn(
        IServiceProvider services, KgsmIdentity identity, Persona persona,
        UserStatus status = UserStatus.Active)
    {
        KgsmUser account = TestAuthority.For(services).Set(identity, persona, status);
        services.GetService<AuthorityChangeNotifier>()?.NotifyAsync(CancellationToken.None).GetAwaiter().GetResult();
        return account;
    }

    /// <summary>Remove the account an identity proves, at the stand-in anchor and on the node.</summary>
    internal static void RemoveAccountOn(IServiceProvider services, KgsmIdentity identity)
    {
        TestAuthority.For(services).Remove(identity);
        services.GetService<AuthorityChangeNotifier>()?.NotifyAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Seed one row straight into the local audit table.
    /// </summary>
    /// <remarks>
    /// That table holds this host's pre-cutover history and nothing appends to it any more — every
    /// producer records what it did in its own journal. Tests that exercise the LOCAL half of the merged
    /// read (keyset order, filters, the redaction) seed it directly, which is what that half reads.
    /// </remarks>
    public async Task SeedAuditAsync(AuditWrite write)
    {
        using IServiceScope scope = Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Audit.Add(AuditMapping.ToEntity(write, "evt_" + Guid.NewGuid().ToString("N")[..10]));
        await db.SaveChangesAsync();
    }
}
