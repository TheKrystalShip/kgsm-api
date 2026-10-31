using System.Text;

using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Api.Realtime;

using ActionIds = TheKrystalShip.Api.Services.Auth.ActionIds;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What each live connection is sent: every frame is evaluated for the reader holding the connection, from
/// the actions its token listed — the topic's action, the variant an audit row's reader is entitled to.
/// </summary>
public sealed class StreamHubAccessTests
{
    private static StreamHub NewHub() => new(Options.Create(new JsonOptions()));

    private static StreamAccess Reader(Persona persona) => StreamAccess.For(Personas.Of(persona));

    private static (StreamConnection Conn, MemoryStream Body) Connect(StreamHub hub, StreamAccess access, params string[] topics)
    {
        var body = new MemoryStream();
        var conn = new StreamConnection(body, topics, hub.Json, NullLogger.Instance, access);
        hub.Add(conn);
        return (conn, body);
    }

    [Fact]
    public void EachConnectionGetsTheAuditVariantItsReaderIsEntitledTo()
    {
        StreamHub hub = NewHub();
        (StreamConnection op, MemoryStream opBody) = Connect(hub, Reader(Persona.Runner), "audit");
        (StreamConnection viewer, MemoryStream viewerBody) = Connect(hub, Reader(Persona.Reader), "audit");

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
        (StreamConnection op, MemoryStream opBody) = Connect(hub, Reader(Persona.Runner), "audit");
        (StreamConnection viewer, MemoryStream viewerBody) = Connect(hub, Reader(Persona.Reader), "audit");

        hub.Publish("audit", "k1", new StreamMessage("audit", "audit.append", new { summary = "started mc" }));
        DrainAll(op, viewer);

        Assert.Contains("started mc", Read(opBody), StringComparison.Ordinal);
        Assert.Contains("started mc", Read(viewerBody), StringComparison.Ordinal);
    }

    /// <summary>
    /// A connection nobody stated access for is nobody's: it reads no server and no node topic. The default
    /// matters, because every other construction site is a test and a permissive default would make the
    /// one production call site the only thing standing between a reader and a frame.
    /// </summary>
    [Fact]
    public void AConnectionWithNoStatedAccessReadsNoServer()
    {
        StreamHub hub = NewHub();
        var conn = new StreamConnection(new MemoryStream(), ["audit", "servers"], hub.Json, NullLogger.Instance);

        Assert.False(conn.Receives("audit"));
        Assert.True(conn.Receives("servers"), "a collection is open to subscribe; its frames are cut to its readers");
        Assert.False(conn.Access.Allows(ActionIds.ServerRead));
    }

    [Fact]
    public void ACollectionFrameReachesOnlyReadersOfServers()
    {
        StreamHub hub = NewHub();
        (StreamConnection reader, MemoryStream readerBody) = Connect(hub, Reader(Persona.Reader), "servers");
        (StreamConnection none, MemoryStream noneBody) = Connect(hub, Reader(Persona.None), "servers");

        hub.Publish("servers", "servers:mc", new StreamMessage("servers", "server.patch", new { id = "mc" }), "mc");
        DrainAll(reader, none);

        Assert.Contains("\"id\":\"mc\"", Read(readerBody), StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":\"mc\"", Read(noneBody), StringComparison.Ordinal);
    }

    [Fact]
    public void ARosterFrameReachesOnlyReadersOfServers()
    {
        StreamHub hub = NewHub();
        (StreamConnection reader, MemoryStream readerBody) = Connect(hub, Reader(Persona.Reader), "servers/metrics");
        (StreamConnection none, MemoryStream noneBody) = Connect(hub, Reader(Persona.None), "servers/metrics");

        hub.PublishRows("servers/metrics", "servers-metrics", ["mc", "ark"],
            rows => new StreamMessage("servers/metrics", "metrics.roster", new { servers = rows }));
        DrainAll(reader, none);

        Assert.Contains("\"servers\":[\"mc\",\"ark\"]", Read(readerBody), StringComparison.Ordinal);
        Assert.DoesNotContain("metrics.roster", Read(noneBody), StringComparison.Ordinal);
    }

    [Fact]
    public void ATopicThisBuildDoesNotKnowIsAnOwnersAlone()
    {
        StreamHub hub = NewHub();
        (StreamConnection op, MemoryStream _) = Connect(hub, Reader(Persona.Runner), "future/topic");
        (StreamConnection member, MemoryStream _) = Connect(hub, StreamAccess.Unrestricted, "future/topic");

        Assert.False(op.Receives("future/topic"));
        Assert.True(member.Receives("future/topic"));
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
