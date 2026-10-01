using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Minting;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>GET /api/v1/me/access</c>: what the caller may do on this node, from this node's replica of the
/// authority — and an honest outage while that replica is not there.
/// </summary>
public sealed class MeAccessTests : IDisposable
{
    private const string ClusterId = "test-cluster";
    private const string Issuer = "kgsm";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-api-me-access-" + Guid.NewGuid().ToString("N"));

    public MeAccessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed record Published(string? Audience, string? Issuer, IReadOnlyList<SecurityKey> Keys) : IClusterSessionKeys;

    private static WebApplicationFactory<Program> Node(AuthTestFactory factory, EcdsaSessionSigner signer, Action<IServiceCollection>? more = null) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClusterSessionKeys>();
            services.AddSingleton<IClusterSessionKeys>(new Published(ClusterId, Issuer, SessionKeys.VerificationKeysFrom(signer.PublicKeys)));
            more?.Invoke(services);
        }));

    private static string Token(EcdsaSessionSigner signer, KgsmIdentity who) =>
        new SessionTokenService(new SessionTokenOptions(ClusterId, TimeSpan.FromMinutes(15), TimeSpan.FromDays(30), Issuer), signer)
            .MintAccess(who, "sid_1").Token;

    private static HttpRequestMessage Get(string token)
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/me/access");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>A replica that could not be opened.</summary>
    private sealed class Unopened : IReplicatedAuthority
    {
        public SqliteAuthorityStore? Replica => null;

        public string? UnavailableReason => "the replica could not be opened";
    }

    [Fact]
    public async Task AReplicaThatCannotBeRead_IsAnOutage_NotAnEmptyAnswer()
    {
        using EcdsaSessionSigner signer = EcdsaSessionSigner.Generate();
        await using AuthTestFactory factory = new();
        using WebApplicationFactory<Program> node = Node(factory, signer, services =>
        {
            services.RemoveAll<IReplicatedAuthority>();
            services.AddSingleton<IReplicatedAuthority>(new Unopened());
        });
        KgsmIdentity alice = new("discord", "42", "alice", "Alice", null, []);

        HttpResponseMessage response = await node.CreateClient().SendAsync(Get(Token(signer, alice)));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("authority_unavailable",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// A version 2 replica in which <paramref name="accountId"/> holds a role reading the library
    /// cluster-wide and an auth action this node does not perform, and a manifest directory saying this
    /// node performs the engine's actions.
    /// </summary>
    private string ReplicaPath => Path.Combine(_dir, "replica.db");

    private string ManifestDirectory => Path.Combine(_dir, "actions");

    /// <summary>This node reading the replica and the manifests below, once they are written.</summary>
    private Action<IServiceCollection> ReplicaServices() => services =>
    {
        services.RemoveAll<IReplicatedAuthority>();
        services.AddSingleton<IReplicatedAuthority>(new AuthorityReplicaFile(ReplicaPath, NullLogger<AuthorityReplicaFile>.Instance));
        services.RemoveAll<AuthorityReporterOptions>();
        services.AddSingleton(new AuthorityReporterOptions { ManifestDirectories = [ManifestDirectory] });
    };

    private async Task SeedReplicaAsync(string accountId)
    {
        string replicaPath = ReplicaPath;
        SqliteAuthorityStore replica = new(new UserStoreOptions { Path = replicaPath });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await replica.ApplyAsync(new CatalogRecord(
        [
            new CatalogActionRecord("kgsm:library.read", "Read the library", "read", "cluster", false),
            new CatalogActionRecord("auth:roles.edit", "Edit roles", "write", "cluster", false),
        ], 2), now);
        await replica.ApplyAsync(new PermissionRecord("prm_1", "Library", ["kgsm:library.read", "auth:roles.edit"], 3), now);
        await replica.ApplyAsync(new RoleRecord("role_1", "Readers", "custom", 1, ["prm_1"], 4), now);
        await replica.ApplyAsync(new AccountRecord(accountId, "alice", "Alice", "admitted", "person", "active", now, now,
            [new ReplicatedIdentity("discord:42", null)], null, [], 5), now);
        await replica.ApplyAsync(new AssignmentRecord("asg_1", accountId, "role_1", "cluster", null, now, 6), now);
        await replica.ConfirmAsync(new AuthorityCurrent(6, 300, AccessContract.Version, now), now);

        Directory.CreateDirectory(ManifestDirectory);
        File.WriteAllText(Path.Combine(ManifestDirectory, "kgsm.json"),
            """{"schemaVersion":1,"component":"kgsm","actions":[{"id":"library.read","title":"Read the library","effect":"read","scope":"cluster"}]}""");
    }

    [Fact]
    public async Task AChangeTheReplicaTakes_ReachesTheAccountsOpenMeStream()
    {
        using EcdsaSessionSigner signer = EcdsaSessionSigner.Generate();
        await using AuthTestFactory factory = new();
        KgsmIdentity alice = new("discord", "42", "alice", "Alice", null, []);

        using WebApplicationFactory<Program> node = Node(factory, signer, ReplicaServices());
        await SeedReplicaAsync("usr_alice");

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(node.CreateClient(), "/api/v1/stream?topics=me", Token(signer, alice));
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        foreach (IAuthorityChangeListener listener in node.Services.GetServices<IAuthorityChangeListener>())
            await listener.AuthorityChangedAsync(CancellationToken.None);

        JsonElement? frame = await frames.WaitForFrame(
            f => f.GetProperty("topic").GetString() == "me" && f.GetProperty("type").GetString() == "me.access",
            TimeSpan.FromSeconds(5));

        Assert.NotNull(frame);
        Assert.Equal(["kgsm:library.read"],
            frame.Value.GetProperty("data").GetProperty("cluster").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task AVersionTwoReplica_AnswersTheActionsThisNodePerforms()
    {
        using EcdsaSessionSigner signer = EcdsaSessionSigner.Generate();
        await using AuthTestFactory factory = new();
        using WebApplicationFactory<Program> node = Node(factory, signer, ReplicaServices());
        KgsmIdentity alice = new("discord", "42", "alice", "Alice", null, []);
        await SeedReplicaAsync("usr_alice");

        HttpResponseMessage response = await node.CreateClient().SendAsync(Get(Token(signer, alice)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement report = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["kgsm:library.read"], report.GetProperty("cluster").EnumerateArray().Select(a => a.GetString()));
        Assert.True(report.GetProperty("current").GetBoolean());
        Assert.Equal(6, report.GetProperty("version").GetInt64());
        Assert.False(report.GetProperty("owner").GetBoolean());
    }

    [Fact]
    public async Task AnAuthDisabledHost_AnswersAsAnOwnerHoldingEveryActionItsComponentsDeclare()
    {
        await using AuthTestFactory factory = new();
        using WebApplicationFactory<Program> node = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:AuthDisabled"] = "true",
                ["Api:DisabledAuthActor"] = "local:claude",
                ["Api:DbPath"] = AuthTestFactory.NewDbPath("kgsm-api-tests-access-open"),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<AuthorityReporterOptions>();
                services.AddSingleton(new AuthorityReporterOptions { ManifestDirectories = [ManifestDirectory] });
            });
        });
        Directory.CreateDirectory(ManifestDirectory);
        File.WriteAllText(Path.Combine(ManifestDirectory, "kgsm.json"),
            """{"schemaVersion":1,"component":"kgsm","actions":[{"id":"server.start","title":"Start servers","effect":"execute","scope":"instance"},{"id":"library.read","title":"Read the library","effect":"read","scope":"cluster"}]}""");

        HttpResponseMessage response = await node.CreateClient().GetAsync("/api/v1/me/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement report = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(report.GetProperty("owner").GetBoolean());
        Assert.True(report.GetProperty("current").GetBoolean());
        Assert.Equal(["kgsm:library.read", "kgsm:server.start"],
            report.GetProperty("cluster").EnumerateArray().Select(a => a.GetString()));
    }
}
