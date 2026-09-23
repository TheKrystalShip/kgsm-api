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
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Boots the real API in-process with auth ON, standing in for the cluster's auth anchor with a signer
/// of its own. Everything else is the production pipeline — the JwtBearer validation against the
/// anchor's published key, the ended-session check, the authority read from the replica, the tier
/// policies, the controllers — so the tier matrix exercises the real wiring. The engine/monitor are
/// left unprovisioned so reads degrade to 200 (empty roster / null capacity) with no external
/// dependency.
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
            HostId: ClusterId,
            SigningKey: "",
            AccessLifetime: TimeSpan.FromMinutes(15),
            RefreshLifetime: TimeSpan.FromDays(30),
            Issuer: AnchorIssuer),
        logger: null,
        signer: AnchorSigner);

    /// <summary>What gossip would have delivered from the member holding the accounts.</summary>
    public sealed record PublishedAnchor(string? Audience, string? Issuer, IReadOnlyList<SecurityKey> Keys)
        : IClusterSessionKeys
    {
        /// <summary>The stand-in anchor, as every factory is told about it.</summary>
        public static PublishedAnchor Default { get; } =
            new(ClusterId, AnchorIssuer, EcdsaSessionSigner.VerificationKeysFrom(AnchorSigner.PublicKeys));

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
                // Never the default. /var/lib/kgsm/auth/users.db is the MACHINE's real account file,
                // shared with every KGSM service on the box, and opening it CREATES it — so an unpinned
                // test run would hand the operator a live accounts file that nobody made.
                ["Api:UsersDbPath"] = Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-users-{Guid.NewGuid():N}.db"),
                // No authority cache. A test that mints a viewer token and then an admin one asks the
                // same question twice inside any sane TTL, and a cached first answer would make the
                // second silently wrong. The cache has its own tests, where the TTL is the subject.
                ["Api:AuthorityCacheSeconds"] = "0",
                // Never the default. http://127.0.0.1:8098 is where the machine running the suite keeps
                // its real auth anchor, and a clustered test node with an empty roster would introduce
                // itself to it. A test about the local join names its own target.
                ["Api:LocalAnchorUrl"] = "",
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
    /// A session the stand-in anchor minted at <paramref name="tier"/> for the one standing identity,
    /// whose account on this node is set to match.
    /// </summary>
    public string AccessToken(KgsmTier tier) => MintAccessOn(Services, tier);

    /// <summary>
    /// A refresh token the stand-in anchor genuinely signed, for proving one is never accepted as a
    /// bearer: a refresh token is spent at the anchor and nowhere else.
    /// </summary>
    public string RefreshToken(KgsmTier tier)
    {
        GiveTheFakeIdentityAnAccount(Services, tier);
        return Anchor.MintRefresh(FakeDiscordResolver.Identity, tier, "sid_test_" + Guid.NewGuid().ToString("N")).Token;
    }

    /// <summary>
    /// A session the stand-in anchor minted for <paramref name="identity"/>, with an account on this
    /// node at <paramref name="tier"/>/<paramref name="status"/> — the whole setup for one person.
    /// </summary>
    /// <remarks>
    /// <see cref="AccessToken"/> mints for the one standing identity, so every token it hands out is
    /// the same person holding whichever tier was asked for last. A test about who a frame reaches, or
    /// about one account changing while another watches, needs two people, and this is how it gets
    /// them: <see cref="FakeDiscordResolver.IdentityFor"/> names one, and this gives them a session
    /// and an account of their own.
    /// </remarks>
    public string AccessTokenFor(KgsmIdentity identity, KgsmTier tier, UserStatus status = UserStatus.Active)
    {
        SetAccount(identity, tier, status);
        return MintAccess(identity, tier);
    }

    /// <summary>A session the stand-in anchor minted for <paramref name="identity"/>, and nothing else.</summary>
    public static string MintAccess(KgsmIdentity identity, KgsmTier tier) =>
        Anchor.MintAccess(identity, tier, "sid_test_" + Guid.NewGuid().ToString("N")).Token;

    /// <summary>
    /// A session for the standing identity, whose account is set through <paramref name="services"/>.
    /// </summary>
    /// <remarks>
    /// Takes an <see cref="IServiceProvider"/> so a factory built via <c>WithWebHostBuilder</c> — a
    /// DERIVED factory with its OWN random database, replica and service provider — gets the account
    /// written where its own requests will read it.
    /// </remarks>
    internal static string MintAccessOn(IServiceProvider services, KgsmTier tier)
    {
        // A token says what tier it was minted at; the replica says what the holder may do, and the
        // replica is what every gate reads. So a token minted at a tier only means anything if the
        // account behind it holds that tier — which is the production rule, not a test convenience.
        GiveTheFakeIdentityAnAccount(services, tier);
        return MintAccess(FakeDiscordResolver.Identity, tier);
    }

    /// <summary>Create or move the account behind the fake identity to <paramref name="tier"/>.</summary>
    internal static void GiveTheFakeIdentityAnAccount(IServiceProvider services, KgsmTier tier)
    {
        // A factory pointed at a replica that will not open is testing exactly that, and minting a
        // token for it must not be the thing that fails.
        if (services.GetRequiredService<UserDirectory>().Available)
            SetAccountOn(services, FakeDiscordResolver.Identity, tier);
    }

    /// <summary>
    /// Give an identity an account on this node at a tier and status of the test's choosing — what
    /// replication from the anchor would have delivered.
    /// </summary>
    public KgsmUser SetAccount(KgsmIdentity identity, KgsmTier tier, UserStatus status = UserStatus.Active) =>
        SetAccountOn(Services, identity, tier, status);

    /// <summary>The account an identity proves here, or <see langword="null"/>.</summary>
    public KgsmUser? AccountOf(KgsmIdentity identity) =>
        ReplicaOf(Services).FindByCredentialAsync(identity.Handle).GetAwaiter().GetResult();

    /// <summary>
    /// The replica file a factory's node reads, opened directly — the test's stand-in for replication.
    /// </summary>
    /// <remarks>
    /// This node only ever reads the replica; the anchor is its one writer and reaches it through
    /// replication. A test plays that part by writing the same file through the account store, which
    /// is exactly what replication does on the other side.
    /// </remarks>
    internal static SqliteUserStore ReplicaOf(IServiceProvider services) =>
        new(new UserStoreOptions { Path = services.GetRequiredService<ApiOptions>().UsersDbPath });

    internal static KgsmUser SetAccountOn(
        IServiceProvider services, KgsmIdentity identity, KgsmTier tier,
        UserStatus status = UserStatus.Active)
    {
        SqliteUserStore store = ReplicaOf(services);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        KgsmUser? existing = store.FindByCredentialAsync(identity.Handle).GetAwaiter().GetResult();

        KgsmUser account;
        if (existing is not null)
        {
            account = existing with { Tier = tier, Status = status, Updated = now };
            store.UpdateAsync(account).GetAwaiter().GetResult();
        }
        else
        {
            account = new IdentityLinkService(store)
                .ProvisionAsync(identity, tier, TierSource.Granted, status, now)
                .GetAwaiter().GetResult().User!;
        }

        // The cache is off in this factory, but a derived one may not be, and a stale answer here
        // would look like the gate being wrong rather than the setup being stale.
        services.GetRequiredService<UserDirectory>().Authority.ForgetAll();
        return account;
    }

    /// <summary>
    /// Seed one row straight into the local audit table.
    /// </summary>
    /// <remarks>
    /// That table holds this host's pre-cutover history and nothing appends to it any more — every
    /// producer records what it did in its own journal. Tests that exercise the LOCAL half of the merged
    /// read (keyset order, filters, the viewer gate) seed it directly, which is what that half reads.
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
