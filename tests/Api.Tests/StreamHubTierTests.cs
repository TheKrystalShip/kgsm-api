using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Api.Realtime;

using TheKrystalShip.KGSM.Auth;

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

    private static (StreamConnection Conn, MemoryStream Body) Connection(
        StreamHub hub, bool isOperator, string? accountId = null, IEnumerable<string>? topics = null)
    {
        var body = new MemoryStream();
        var conn = new StreamConnection(
            body, topics ?? ["audit"], hub.Json, NullLogger.Instance, sessionAlive: null,
            tier: isOperator ? KgsmTier.Operator : KgsmTier.Viewer, accountId: accountId);
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


    // --- the `me` topic: one account's own standing, delivered to that account and nobody else ------

    /// <summary>
    /// The whole point of a per-account audience. A fact about one person's access reaches every
    /// connection they hold and no connection anybody else holds — including the connection of
    /// somebody who proves no account here at all, which belongs to nobody.
    /// </summary>
    [Fact]
    public void APublishToAnAccountReachesThatAccountAndNoOther()
    {
        StreamHub hub = NewHub();
        (StreamConnection aliceLaptop, MemoryStream laptop) = Connection(hub, false, "usr_alice", ["me"]);
        (StreamConnection alicePhone, MemoryStream phone) = Connection(hub, false, "usr_alice", ["me"]);
        (StreamConnection bobConn, MemoryStream bob) = Connection(hub, false, "usr_bob", ["me"]);
        (StreamConnection strangerConn, MemoryStream stranger) = Connection(hub, false, null, ["me"]);

        hub.AuthorityChanged("usr_alice", KgsmTier.Operator, "active");
        DrainAll(aliceLaptop, alicePhone, bobConn, strangerConn);

        Assert.Contains("\"tier\":\"operator\"", Read(laptop), StringComparison.Ordinal);
        Assert.Contains("\"status\":\"active\"", Read(laptop), StringComparison.Ordinal);
        Assert.Contains("\"tier\":\"operator\"", Read(phone), StringComparison.Ordinal);
        Assert.DoesNotContain("me.patch", Read(bob), StringComparison.Ordinal);
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
        (StreamConnection conn, MemoryStream body) = Connection(hub, false, "usr_alice", ["servers"]);

        hub.AuthorityChanged("usr_alice", KgsmTier.Operator, "active");
        DrainAll(conn);

        Assert.DoesNotContain("me.patch", Read(body), StringComparison.Ordinal);
    }

    /// <summary>
    /// A status change under an unchanged tier is still news — an account approved straight to
    /// <c>viewer</c> and one whose tier was already right both leave a panel that would otherwise sit
    /// on "awaiting approval" until somebody reloaded it.
    /// </summary>
    [Fact]
    public void AStandingIsPushedEvenWhenTheTierDidNotMove()
    {
        StreamHub hub = NewHub();
        (StreamConnection conn, MemoryStream body) = Connection(hub, false, "usr_alice", ["me"]);

        hub.AuthorityChanged("usr_alice", KgsmTier.Viewer, "active");
        DrainAll(conn);

        Assert.Equal(KgsmTier.Viewer, conn.Tier);
        Assert.Contains("\"status\":\"active\"", Read(body), StringComparison.Ordinal);
    }

    // --- in-place re-gating -------------------------------------------------------------------------

    /// <summary>
    /// A demotion lands on the connection, not just on the client rendering it. The operator-only
    /// topics leave the subscription set, and the audit feed flips to the redacted variant — the same
    /// standing a fresh connect at the new tier would produce.
    /// </summary>
    [Fact]
    public void ADemotionStripsTheOperatorOnlyTopicsAndFlipsTheAuditVariant()
    {
        StreamHub hub = NewHub();
        (StreamConnection conn, MemoryStream body) =
            Connection(hub, true, "usr_alice", ["audit", "hosts/h/services", "hosts/h/logs", "me"]);

        Assert.True(conn.IsOperator);
        Assert.True(conn.IsSubscribed("hosts/h/services"));

        hub.AuthorityChanged("usr_alice", KgsmTier.Viewer, "active");

        Assert.False(conn.IsOperator);
        Assert.False(conn.IsSubscribed("hosts/h/services"));
        Assert.False(conn.IsSubscribed("hosts/h/logs"));
        Assert.True(conn.IsSubscribed("audit"), "the audit feed says the same things to everyone");

        hub.Publish("audit", "k1",
            new StreamMessage("audit", "audit.append", new { summary = "ran 'op somebody' on mc" }),
            new StreamMessage("audit", "audit.append", new { summary = "sent a console command to mc" }));
        DrainAll(conn);

        string written = Read(body);
        Assert.DoesNotContain("op somebody", written, StringComparison.Ordinal);
        Assert.Contains("sent a console command", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Demoting to nothing at all leaves the connection where a stranger's fresh connect would leave
    /// it: the one topic that needs no grant, and nothing else.
    /// </summary>
    [Fact]
    public void ADemotionToNoneLeavesOnlyTheTopicThatNeedsNoGrant()
    {
        StreamHub hub = NewHub();
        (StreamConnection conn, MemoryStream _) =
            Connection(hub, false, "usr_alice", ["servers", "audit", "me"]);

        hub.AuthorityChanged("usr_alice", KgsmTier.None, "unknown");

        Assert.False(conn.IsSubscribed("servers"));
        Assert.False(conn.IsSubscribed("audit"));
        Assert.True(conn.IsSubscribed("me"));
    }

    /// <summary>
    /// A promotion never hands back a topic. The subscription set is what the client asked for filtered
    /// by what it held, and this cannot tell a topic the client did not want from one it was refused —
    /// so it adds nothing, and a client that wants more opens a stream asking for more.
    /// </summary>
    [Fact]
    public void APromotionNeverAddsASubscriptionBack()
    {
        StreamHub hub = NewHub();
        (StreamConnection conn, MemoryStream _) =
            Connection(hub, true, "usr_alice", ["hosts/h/services", "me"]);

        hub.AuthorityChanged("usr_alice", KgsmTier.Viewer, "active");
        hub.AuthorityChanged("usr_alice", KgsmTier.Admin, "active");

        Assert.True(conn.IsOperator);
        Assert.False(conn.IsSubscribed("hosts/h/services"));
    }

    private static string Read(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    private static void DrainAll(params StreamConnection[] connections)
    {
        foreach (StreamConnection c in connections)
            Drain(c);
    }

    private static void Drain(StreamConnection conn)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        conn.RunAsync(cts.Token).GetAwaiter().GetResult();
    }
}
