using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Api.Realtime;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The one thing on the stream whose <em>values</em> depend on who is reading: an audit row's personal
/// and privileged fields. The topic stays open to viewers — the feed says the same things to everyone —
/// so the split has to happen per connection, at publish.
/// </summary>
public sealed class StreamHubTierTests
{
    private static StreamHub NewHub() =>
        new(Options.Create(new JsonOptions()));

    private static (StreamConnection Conn, MemoryStream Body) Connection(StreamHub hub, bool isOperator)
    {
        var body = new MemoryStream();
        var conn = new StreamConnection(
            body, ["audit"], hub.Json, NullLogger.Instance, sessionAlive: null, isOperator: isOperator);
        hub.Add(conn);
        return (conn, body);
    }

    [Fact]
    public void EachConnectionGetsTheFrameItsTierIsEntitledTo()
    {
        StreamHub hub = NewHub();
        (StreamConnection op, MemoryStream opBody) = Connection(hub, isOperator: true);
        (StreamConnection viewer, MemoryStream viewerBody) = Connection(hub, isOperator: false);

        hub.Publish("audit", "k1",
            new StreamMessage("audit", "audit.append", new { summary = "ran 'op somebody' on mc" }),
            new StreamMessage("audit", "audit.append", new { summary = "sent a console command to mc" }));

        // Run each write loop briefly so the queued frame reaches its own body.
        Drain(op);
        Drain(viewer);

        string forOperator = Encoding.UTF8.GetString(opBody.ToArray());
        string forViewer = Encoding.UTF8.GetString(viewerBody.ToArray());

        Assert.Contains("op somebody", forOperator, StringComparison.Ordinal);
        Assert.DoesNotContain("op somebody", forViewer, StringComparison.Ordinal);
        Assert.Contains("sent a console command", forViewer, StringComparison.Ordinal);
    }

    /// <summary>
    /// A publish with no restricted variant — every topic but the audit one — reaches both tiers
    /// unchanged. The per-connection path must not become a thing every frame pays for.
    /// </summary>
    [Fact]
    public void AFrameWithNoRestrictedVariantGoesToEverybody()
    {
        StreamHub hub = NewHub();
        (StreamConnection op, MemoryStream opBody) = Connection(hub, isOperator: true);
        (StreamConnection viewer, MemoryStream viewerBody) = Connection(hub, isOperator: false);

        hub.Publish("audit", "k1", new StreamMessage("audit", "audit.append", new { summary = "started mc" }));

        Drain(op);
        Drain(viewer);

        Assert.Contains("started mc", Encoding.UTF8.GetString(opBody.ToArray()), StringComparison.Ordinal);
        Assert.Contains("started mc", Encoding.UTF8.GetString(viewerBody.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>
    /// A connection nobody stated a tier for is the restricted one. The default matters: every other
    /// construction site in the codebase is a test, and a permissive default would make the one
    /// production call site the only thing standing between a viewer and the value.
    /// </summary>
    [Fact]
    public void AConnectionWithNoStatedTierIsNotAnOperator()
    {
        StreamHub hub = NewHub();
        var conn = new StreamConnection(new MemoryStream(), ["audit"], hub.Json, NullLogger.Instance);

        Assert.False(conn.IsOperator);
    }

    private static void Drain(StreamConnection conn)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        conn.RunAsync(cts.Token).GetAwaiter().GetResult();
    }
}
