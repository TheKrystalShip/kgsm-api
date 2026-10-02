using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// A leaf answers for its own configuration, and this node relays rather than deciding.
/// </summary>
/// <remarks>
/// <para>
/// A component owns its descriptor, its overrides and its own lifecycle wherever it runs; only the
/// transport differs. A leaf serves that over a unix socket and this API forwards, because it is the
/// only thing on a node with an address a browser can reach. What is under test is that the forward
/// is <b>opinion-free</b> — the leaf's status and the leaf's bytes, unread — and that the fallback to
/// reading the descriptor happens exactly when the leaf cannot answer.
/// </para>
/// <para>
/// The socket is a real unix socket with a real HTTP/1.1 exchange on it, because the whole mechanism
/// is the dial: a mocked transport would pass while the derived path is wrong, which is the one way
/// this can fail silently.
/// </para>
/// </remarks>
public sealed class LeafSurfaceRelayTests : IDisposable
{
    // Resolved from the real container rather than constructed, so the DI registration and the
    // binding of Api__LeafSurfaceRoot are under test alongside the dial itself.
    private readonly LeafTestFactory _factory = new();
    private readonly IServiceProvider _services;
    private readonly string _root;

    public LeafSurfaceRelayTests()
    {
        // NOT the suite's temp root. A unix socket's path is capped at 108 bytes by the kernel, and
        // the per-run temp directory plus a guid plus the leaf's own directory is already past it —
        // a bind there fails with a length error rather than anything about sockets. The real path
        // this models, /run/kgsm-<id>/surface.sock, is short for the same reason.
        _root = Path.Combine("/tmp", "ksr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        // Appended as a configuration source rather than a host setting: the factory pins its own
        // surface root through an in-memory collection, and configuration is last-source-wins.
        _services = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(
            (_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:LeafSurfaceRoot"] = _root,
            }))).Services;
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir that outlives a run costs nothing */ }
    }

    private LeafSurfaceRelay Relay() => _services.GetRequiredService<LeafSurfaceRelay>();

    /// <summary>
    /// A one-shot HTTP/1.1 listener on the socket path a leaf of this id would serve, answering every
    /// request with the same status and body.
    /// </summary>
    private async Task<Task> ServeAsync(string leafId, HttpStatusCode status, string body, CancellationToken ct)
    {
        string dir = Path.Combine(_root, "kgsm-" + leafId);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "surface.sock");

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);

        // Awaited by the caller so a request it made is known to have been read, rather than raced.
        Task served = Task.Run(async () =>
        {
            using Socket conn = await listener.AcceptAsync(ct).ConfigureAwait(false);
            using (listener)
            {
                var read = new byte[8192];
                int n = await conn.ReceiveAsync(read, ct).ConfigureAwait(false);
                Requests.Add(Encoding.UTF8.GetString(read, 0, n));

                byte[] payload = Encoding.UTF8.GetBytes(body);
                string head =
                    $"HTTP/1.1 {(int)status} X\r\n"
                    + "Content-Type: application/json\r\n"
                    + $"Content-Length: {payload.Length}\r\n"
                    + "Connection: close\r\n\r\n";
                await conn.SendAsync(Encoding.UTF8.GetBytes(head), ct).ConfigureAwait(false);
                await conn.SendAsync(payload, ct).ConfigureAwait(false);
            }
        }, ct);

        // The socket file exists the moment Bind returns, which is what the relay probes.
        await Task.Yield();
        return served;
    }

    private List<string> Requests { get; } = [];

    [Fact]
    public async Task AleafServingASurface_IsAskedAndItsAnswerTravelsVerbatim()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string said = """{"id":"monitor","fields":[],"editable":true}""";
        Task served = await ServeAsync("monitor", HttpStatusCode.OK, said, cts.Token);

        LeafSurfaceAnswer? answer = await Relay()
            .SendAsync("monitor", HttpMethod.Get, "config", null, null, cts.Token);
        await served;

        Assert.NotNull(answer);
        Assert.Equal(200, answer!.Status);
        // Byte for byte. Re-serializing here would mean holding the shape, which is the duplication
        // the relay removes.
        Assert.Equal(said, answer.Body);
        Assert.Contains("GET /component/config", Requests[0]);
    }

    [Fact]
    public async Task ArefusalIsTheLeafsOwn_NeverTranslated()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string said = """{"error":{"code":"invalid_value","message":"'intervalMs' is at least 100"}}""";
        Task served = await ServeAsync("monitor", HttpStatusCode.BadRequest, said, cts.Token);

        LeafSurfaceAnswer? answer = await Relay()
            .SendAsync("monitor", HttpMethod.Put, "config", """{"values":{"intervalMs":"1"}}""", null, cts.Token);
        await served;

        Assert.NotNull(answer);
        Assert.Equal(400, answer!.Status);
        Assert.Equal(said, answer.Body);
        // The body reached the leaf, so what it refused is what was asked rather than an empty apply.
        Assert.Contains("intervalMs", Requests[0]);
    }

    [Fact]
    public async Task AleafWithNoSurface_IsNotAskedAtAll()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        LeafSurfaceRelay relay = Relay();

        Assert.False(relay.ServesOwnSurface("watchdog"));
        // Null is the caller's cue to read the descriptor itself, which is the same answer a leaf
        // that is DOWN gives — and that is the one case a self-serving component cannot cover.
        Assert.Null(await relay.SendAsync("watchdog", HttpMethod.Get, "config", null, null, cts.Token));
    }

    [Fact]
    public async Task AsocketFileNothingListensOn_FallsBackRatherThanFailing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Exactly what a leaf killed mid-run leaves behind: the file is there and the dial is refused.
        string dir = Directory.CreateDirectory(Path.Combine(_root, "kgsm-reactor")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "surface.sock"), "", cts.Token);

        LeafSurfaceRelay relay = Relay();

        Assert.True(relay.ServesOwnSurface("reactor"), "the file is present, which is all the probe reads");
        Assert.Null(await relay.SendAsync("reactor", HttpMethod.Get, "config", null, null, cts.Token));
    }

    [Fact]
    public async Task TheAccountAskingTravelsWithTheRequest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task served = await ServeAsync("scheduler", HttpStatusCode.OK, "{}", cts.Token);

        // The leaf records this account as the author of any automation the change switches on.
        await Relay().SendAsync("scheduler", HttpMethod.Put, "config",
            """{"values":{"updateCheckEnabled":"true"}}""", "local:usr_asking", cts.Token);
        await served;

        Assert.Contains($"{LeafSurfaceRelay.ActingAccountHeader}: local:usr_asking", Requests[0]);
    }

    [Fact]
    public async Task NoAccount_SendsNoHeader()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task served = await ServeAsync("scheduler", HttpStatusCode.OK, "{}", cts.Token);

        await Relay().SendAsync("scheduler", HttpMethod.Get, "config", null, null, cts.Token);
        await served;

        Assert.DoesNotContain(LeafSurfaceRelay.ActingAccountHeader, Requests[0]);
    }

    [Fact]
    public void TheSocketPathIsDerivedFromTheLeafsOwnId()
    {
        // No list of leaves and no per-leaf setting: a leaf's unit already provisions this directory,
        // so a leaf that starts serving a surface is relayed to with nothing registered anywhere.
        Assert.Equal(
            Path.Combine(_root, "kgsm-scheduler", "surface.sock"),
            Relay().SocketPathFor("scheduler"));
    }
}
