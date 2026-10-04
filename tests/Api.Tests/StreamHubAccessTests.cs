using System.Text;

using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Auth;
using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Users;

using ActionIds = TheKrystalShip.Api.Services.Auth.ActionIds;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What each live connection is sent: every frame is evaluated for the reader holding the connection,
/// from the replica as it stands — the topic's action, the server a collection frame is about, the
/// variant an audit row's reader is entitled to.
/// </summary>
public sealed class StreamHubAccessTests : IDisposable
{
    private const string Node = "test-node";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-api-hub-" + Guid.NewGuid().ToString("N"));
    private readonly TestAuthority _authority;

    public StreamHubAccessTests()
    {
        Directory.CreateDirectory(_dir);
        _authority = TestAuthority.At(Path.Combine(_dir, "replica.db"));
    }

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

    private static StreamHub NewHub() => new(Options.Create(new JsonOptions()));

    // Servers named by id alone: these tests care which server a frame is about, not its install.
    private static AccessScope TargetOf(string? serverId) =>
        serverId is null ? AccessScope.ForNode(Node) : AccessScope.ForInstance(Node, serverId, "nonce-" + serverId);

    private string Person(string subject, Persona persona, UserStatus status = UserStatus.Active) =>
        _authority.Set(TestIdentity.IdentityFor(subject), persona, status).UserId;

    private StreamAccess Reader(string accountId) => StreamAccess.For(accountId, _authority.Evaluator(), TargetOf);

    private (StreamConnection Conn, MemoryStream Body) Connect(StreamHub hub, StreamAccess access, params string[] topics)
    {
        var body = new MemoryStream();
        var conn = new StreamConnection(body, topics, hub.Json, NullLogger.Instance, sessionAlive: null, access);
        hub.Add(conn);
        return (conn, body);
    }

    [Fact]
    public void EachConnectionGetsTheAuditVariantItsReaderIsEntitledTo()
    {
        StreamHub hub = NewHub();
        (StreamConnection op, MemoryStream opBody) = Connect(hub, Reader(Person("hub-op", Persona.Runner)), "audit");
        (StreamConnection viewer, MemoryStream viewerBody) = Connect(hub, Reader(Person("hub-viewer", Persona.Reader)), "audit");

        hub.Publish("audit", "k1",
            new StreamMessage("audit", "audit.append", new { summary = "ran 'op somebody' on mc" }),
            redacted: new StreamRedaction(ActionIds.AuditPersonalFields,
                new StreamMessage("audit", "audit.append", new { summary = "sent a console command to mc" })));

        DrainAll(op, viewer);

        Assert.Contains("op somebody", Read(opBody), StringComparison.Ordinal);
        Assert.DoesNotContain("op somebody", Read(viewerBody), StringComparison.Ordinal);
        Assert.Contains("sent a console command", Read(viewerBody), StringComparison.Ordinal);
    }

    [Fact]
    public void AFrameWithNoRedactedVariantGoesToEveryReaderOfTheTopic()
    {
        StreamHub hub = NewHub();
        (StreamConnection op, MemoryStream opBody) = Connect(hub, Reader(Person("hub-op2", Persona.Runner)), "audit");
        (StreamConnection viewer, MemoryStream viewerBody) = Connect(hub, Reader(Person("hub-viewer2", Persona.Reader)), "audit");

        hub.Publish("audit", "k1", new StreamMessage("audit", "audit.append", new { summary = "started mc" }));
        DrainAll(op, viewer);

        Assert.Contains("started mc", Read(opBody), StringComparison.Ordinal);
        Assert.Contains("started mc", Read(viewerBody), StringComparison.Ordinal);
    }

    /// <summary>
    /// A connection nobody stated access for is nobody's: its own <c>me</c> and nothing else. The default
    /// matters, because every other construction site is a test and a permissive default would make the
    /// one production call site the only thing standing between a reader and a frame.
    /// </summary>
    [Fact]
    public void AConnectionWithNoStatedAccessReachesOnlyMe()
    {
        StreamHub hub = NewHub();
        var conn = new StreamConnection(new MemoryStream(), ["audit", "servers", "me"], hub.Json, NullLogger.Instance);

        Assert.False(conn.Receives("audit"));
        Assert.True(conn.Receives("servers"), "a collection is open to subscribe; its frames are cut per server");
        Assert.True(conn.Receives("me"));
        Assert.False(conn.Access.Allows(ActionIds.ServerRead, "mc"));
    }

    [Fact]
    public void ACollectionFrameReachesOnlyReadersOfTheServerItIsAbout()
    {
        StreamHub hub = NewHub();
        string scoped = Person("hub-scoped", Persona.None);
        _authority.Grant(scoped, ActionIds.ServerRead, TargetOf("mc"));

        (StreamConnection one, MemoryStream oneBody) = Connect(hub, Reader(scoped), "servers");
        (StreamConnection all, MemoryStream allBody) = Connect(hub, Reader(Person("hub-all", Persona.Reader)), "servers");

        hub.Publish("servers", "servers:mc", new StreamMessage("servers", "server.patch", new { id = "mc" }), "mc");
        hub.Publish("servers", "servers:ark", new StreamMessage("servers", "server.patch", new { id = "ark" }), "ark");
        DrainAll(one, all);

        Assert.Contains("\"id\":\"mc\"", Read(oneBody), StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":\"ark\"", Read(oneBody), StringComparison.Ordinal);
        Assert.Contains("\"id\":\"mc\"", Read(allBody), StringComparison.Ordinal);
        Assert.Contains("\"id\":\"ark\"", Read(allBody), StringComparison.Ordinal);
    }

    [Fact]
    public void ARosterFrameIsCutToTheRowsEachReaderMayRead()
    {
        StreamHub hub = NewHub();
        string scoped = Person("hub-rows", Persona.None);
        _authority.Grant(scoped, ActionIds.ServerRead, TargetOf("mc"));
        (StreamConnection conn, MemoryStream body) = Connect(hub, Reader(scoped), "servers/metrics");

        hub.PublishRows("servers/metrics", "servers-metrics", ["mc", "ark"], row => row,
            rows => new StreamMessage("servers/metrics", "metrics.roster", new { servers = rows }));
        DrainAll(conn);

        Assert.Contains("\"servers\":[\"mc\"]", Read(body), StringComparison.Ordinal);
    }

    // --- the `me` topic: one account's own standing, delivered to that account and nobody else ------

    /// <summary>
    /// The whole point of a per-account audience. A fact about one person's account reaches every
    /// connection they hold and no connection anybody else holds — including the connection of somebody
    /// who proves no account here at all, which belongs to nobody.
    /// </summary>
    [Fact]
    public void AStatusChangeReachesThatAccountAndNoOther()
    {
        StreamHub hub = NewHub();
        string alice = Person("hub-alice", Persona.Reader, UserStatus.Pending);
        string bob = Person("hub-bob", Persona.Reader);
        (StreamConnection aliceLaptop, MemoryStream laptop) = Connect(hub, Reader(alice), "me");
        (StreamConnection alicePhone, MemoryStream phone) = Connect(hub, Reader(alice), "me");
        (StreamConnection bobConn, MemoryStream bobBody) = Connect(hub, Reader(bob), "me");
        (StreamConnection strangerConn, MemoryStream stranger) = Connect(hub, StreamAccess.Nobody, "me");

        Person("hub-alice", Persona.Reader, UserStatus.Active);
        hub.AccessChanged(_authority.Evaluator());
        DrainAll(aliceLaptop, alicePhone, bobConn, strangerConn);

        Assert.Contains("\"status\":\"active\"", Read(laptop), StringComparison.Ordinal);
        Assert.Contains("\"status\":\"active\"", Read(phone), StringComparison.Ordinal);
        Assert.DoesNotContain("me.patch", Read(bobBody), StringComparison.Ordinal);
        Assert.DoesNotContain("me.patch", Read(stranger), StringComparison.Ordinal);
    }

    /// <summary>
    /// A client that never asked for the topic is not sent it. Being about the reader is not a licence
    /// to push onto a subscription they did not open — the same rule every other topic follows.
    /// </summary>
    [Fact]
    public void AConnectionThatDidNotSubscribeToMeIsSentNothing()
    {
        StreamHub hub = NewHub();
        string alice = Person("hub-alice2", Persona.Reader, UserStatus.Pending);
        (StreamConnection conn, MemoryStream body) = Connect(hub, Reader(alice), "servers");

        Person("hub-alice2", Persona.Reader, UserStatus.Active);
        hub.AccessChanged(_authority.Evaluator());
        DrainAll(conn);

        Assert.DoesNotContain("me.patch", Read(body), StringComparison.Ordinal);
    }

    // --- access changing under an open connection ----------------------------------------------------

    /// <summary>
    /// Access taken away lands on the connection, not just on the client rendering it: the topics the
    /// reader no longer reaches stop delivering, and the audit feed flips to the redacted variant.
    /// </summary>
    [Fact]
    public void LosingAccessStopsTheTopicsAndFlipsTheAuditVariant()
    {
        StreamHub hub = NewHub();
        string alice = Person("hub-demoted", Persona.Runner);
        (StreamConnection conn, MemoryStream body) = Connect(hub, Reader(alice), "audit", "hosts/h/services", "hosts/h/logs", "me");

        Assert.True(conn.Receives("hosts/h/services"));

        Person("hub-demoted", Persona.Reader);
        hub.AccessChanged(_authority.Evaluator());

        Assert.False(conn.Receives("hosts/h/services"));
        Assert.False(conn.Receives("hosts/h/logs"));
        Assert.True(conn.Receives("audit"));

        hub.Publish("audit", "k1",
            new StreamMessage("audit", "audit.append", new { summary = "ran 'op somebody' on mc" }),
            redacted: new StreamRedaction(ActionIds.AuditPersonalFields,
                new StreamMessage("audit", "audit.append", new { summary = "sent a console command to mc" })));
        DrainAll(conn);

        string written = Read(body);
        Assert.DoesNotContain("op somebody", written, StringComparison.Ordinal);
        Assert.Contains("sent a console command", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Access granted lands the same way: the subscription the client asked for starts delivering, with
    /// no reconnect. The connection keeps what was asked for and asks again on every frame.
    /// </summary>
    [Fact]
    public void GainingAccessStartsTheTopicTheClientAskedFor()
    {
        StreamHub hub = NewHub();
        string alice = Person("hub-promoted", Persona.Reader);
        (StreamConnection conn, MemoryStream _) = Connect(hub, Reader(alice), "hosts/h/services", "me");

        Assert.False(conn.Receives("hosts/h/services"));

        Person("hub-promoted", Persona.Runner);
        hub.AccessChanged(_authority.Evaluator());

        Assert.True(conn.Receives("hosts/h/services"));
    }

    [Fact]
    public void ATopicThisBuildDoesNotKnowIsAnOwnersAlone()
    {
        StreamHub hub = NewHub();
        (StreamConnection op, MemoryStream _) = Connect(hub, Reader(Person("hub-op3", Persona.Runner)), "future/topic");
        (StreamConnection owner, MemoryStream _) = Connect(hub, Reader(Person("hub-owner", Persona.Owner)), "future/topic");

        Assert.False(op.Receives("future/topic"));
        Assert.True(owner.Receives("future/topic"));
    }

    private static string Read(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    private static void DrainAll(params StreamConnection[] connections)
    {
        foreach (StreamConnection c in connections)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            c.RunAsync(cts.Token).GetAwaiter().GetResult();
        }
    }
}
