using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api;
using TheKrystalShip.KGSM.Sessions.Testing;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Boots the real API in-process with auth ON, against a stand-in sign-in provider (<see cref="Provider"/>)
/// that serves discovery and keys in-process and mints the access tokens a signed-in caller presents.
/// Everything else is the production pipeline — the sessions package's token validation, the action
/// gates, the controllers — so the access matrix exercises the real wiring. The engine/monitor are left
/// unprovisioned so reads degrade to 200 (empty roster / null capacity) with no external dependency.
/// </summary>
public class AuthTestFactory : WebApplicationFactory<Program>
{
    public const string HostId = "test-host";

    /// <summary>The application the stand-in provider issues this cluster's tokens for.</summary>
    public const string Audience = TestIssuer.DefaultAudience;

    /// <summary>
    /// The stand-in provider. One for the whole run: every factory is told its issuer, and a token it
    /// minted verifies on any factory.
    /// </summary>
    public static readonly TestIssuer Provider = new();

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
                ["Sessions:Issuer"] = Provider.Issuer,
                ["Sessions:Audience"] = Audience,
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
                // Never the default either: /etc/kgsm/cluster-founded describes the machine running the
                // suite.
                ["Cluster:FoundedPath"] =
                    Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-founded-{Guid.NewGuid():N}"),
            });
        });

        builder.ConfigureTestServices(services => Provider.UseTestIssuer(services));
    }

    /// <summary>A token the stand-in provider minted for the standing person, listing what <paramref name="persona"/> holds.</summary>
    public string AccessToken(Persona persona) => MintAccess(persona);

    /// <summary>A token the stand-in provider minted for the standing person, listing what <paramref name="persona"/> holds.</summary>
    public static string MintAccess(Persona persona) => MintAccess(TestIdentity.Person, persona);

    /// <summary>A token the stand-in provider minted for <paramref name="person"/>, listing what <paramref name="persona"/> holds.</summary>
    public static string MintAccess(TestPerson person, Persona persona) =>
        Provider.Mint(person.Subject, person.Name, Personas.Of(persona), Audience);

    /// <summary>A token the stand-in provider minted for <paramref name="person"/>, listing what <paramref name="persona"/> holds.</summary>
    public string AccessTokenFor(TestPerson person, Persona persona) => MintAccess(person, persona);

    /// <summary>A token for <paramref name="person"/> listing exactly <paramref name="actions"/>.</summary>
    public string AccessTokenWith(TestPerson person, IEnumerable<string> actions, TimeSpan? lifetime = null) =>
        Provider.Mint(person.Subject, person.Name, actions, Audience, lifetime: lifetime);

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
