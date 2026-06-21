using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// #8 coverage for the <see cref="ConsoleBridgeManager"/> — the follow-only <c>servers/{id}/console</c>
/// bridge, driven through its <c>ReconcileAsync</c> seam with a faked <see cref="IWatchdogClient"/> and a
/// captured publish hop. Load-bearing invariants: a subscribed native instance opens EXACTLY ONE bridge that
/// publishes each line with a UNIQUE incrementing <c>console:{id}:{seq}</c> coalesce key (lines never
/// collapse); losing the last subscriber CLOSES the bridge (cancels the unbounded follow); a non-native/404
/// (empty follow) and a transport throw are each marked not-capable so the loop never reopens them
/// (no retry-spam); an idle stream (no console subscribers) does zero work.
/// </summary>
public sealed class ConsoleBridgeTests
{
    private static ApiOptions Options() =>
        ApiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KGSM_API_HOST_ID"] = "hotrod",
                // A non-empty watchdog socket => WatchdogProvisioned (the bridge would run); the tests drive
                // ReconcileAsync directly with a fake, so the path is never opened.
                ["KGSM_API_WATCHDOG_SOCKET"] = "/run/test/watchdog.sock",
            })
            .Build());

    private static WatchdogInstanceState Native(string name) =>
        new() { Name = name, Desired = "running", Phase = "running" };

    // Capture every publish: (coalesceKey, message). The wire frame doesn't carry the coalesce key, so this
    // is the only place the "unique per-line key" requirement is observable.
    private sealed record Published(string Topic, string CoalesceKey, StreamMessage Message);

    private static (ConsoleBridgeManager mgr, ConcurrentQueue<Published> sent) Manager(
        Func<string, bool> hasSubscribers, Func<Func<string, bool>, bool> anySubscription)
    {
        var sent = new ConcurrentQueue<Published>();
        var mgr = new ConsoleBridgeManager(
            Options(), NullLogger<ConsoleBridgeManager>.Instance,
            publish: (topic, key, msg) => sent.Enqueue(new Published(topic, key, msg)),
            hasSubscribers: hasSubscribers,
            anySubscription: anySubscription);
        return (mgr, sent);
    }

    [Fact]
    public async Task SubscribedNativeInstance_OpensOneBridge_PublishesUniqueIncrementingSeq()
    {
        string topic = StreamProtocol.ServerConsoleTopic("mc");
        var (mgr, sent) = Manager(
            hasSubscribers: t => t == topic,
            anySubscription: pred => pred(topic));

        var wd = new FakeWatchdog([Native("mc")]);
        wd.SetFollow("mc", ["line A", "line B", "line C"], holdOpenAfter: true);

        await mgr.ReconcileAsync(wd, CancellationToken.None);
        await wd.WaitForFollowOpened("mc");
        await WaitUntil(() => sent.Count >= 3);

        Assert.Equal(1, wd.FollowOpenCount("mc")); // exactly one shared bridge, not one per line/subscriber

        Published[] lines = sent.ToArray();
        Assert.Equal(3, lines.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(topic, lines[i].Topic);
            Assert.Equal(StreamProtocol.ConsoleLine, lines[i].Message.Type);
            // UNIQUE, monotonically incrementing per-line key — lines never collapse into the latest.
            Assert.Equal($"console:mc:{i}", lines[i].CoalesceKey);
            var data = Assert.IsType<ConsoleBridgeManager.ConsoleLineData>(lines[i].Message.Data);
            Assert.Equal("mc", data.Id);
            Assert.Equal(i, data.Seq);
        }
        Assert.Equal(["line A", "line B", "line C"],
            lines.Select(l => ((ConsoleBridgeManager.ConsoleLineData)l.Message.Data).Line));

        await mgr.StopAllBridgesForTestAsync();
    }

    [Fact]
    public async Task LosingLastSubscriber_ClosesBridge_CancelsFollow()
    {
        string topic = StreamProtocol.ServerConsoleTopic("mc");
        bool subscribed = true;
        var (mgr, _) = Manager(
            hasSubscribers: t => subscribed && t == topic,
            anySubscription: pred => subscribed && pred(topic));

        var wd = new FakeWatchdog([Native("mc")]);
        wd.SetFollow("mc", [], holdOpenAfter: true); // holds open until its token is cancelled

        await mgr.ReconcileAsync(wd, CancellationToken.None);
        await wd.WaitForFollowOpened("mc");
        Assert.False(wd.FollowCancelled("mc")); // still open while subscribed

        // The last subscriber leaves -> the next reconcile closes the bridge (cancels the unbounded follow).
        subscribed = false;
        await mgr.ReconcileAsync(wd, CancellationToken.None);
        await WaitUntil(() => wd.FollowCancelled("mc"));
        Assert.True(wd.FollowCancelled("mc"));

        await mgr.StopAllBridgesForTestAsync();
    }

    [Fact]
    public async Task NonNative404_EmptyFollow_MarkedNotCapable_NeverReopened()
    {
        string topic = StreamProtocol.ServerConsoleTopic("web"); // a non-native instance: follow yields empty
        var (mgr, sent) = Manager(
            hasSubscribers: t => t == topic,
            anySubscription: pred => pred(topic));

        var wd = new FakeWatchdog([Native("web")]);
        wd.SetFollow("web", [], holdOpenAfter: false); // 404 => yield break: 0 lines, completes, no throw

        await mgr.ReconcileAsync(wd, CancellationToken.None);
        await wd.WaitForFollowOpened("web");
        await WaitUntil(() => wd.FollowOpenCount("web") >= 1);

        // Several more reconcile ticks while still subscribed must NOT reopen it (no retry-spam).
        for (int i = 0; i < 5; i++)
            await mgr.ReconcileAsync(wd, CancellationToken.None);

        Assert.Equal(1, wd.FollowOpenCount("web"));
        Assert.Empty(sent);

        await mgr.StopAllBridgesForTestAsync();
    }

    [Fact]
    public async Task FollowThrows_IsTransient_DoesNotCrash_PublishesNothing_AndRecovers()
    {
        // A transport throw (watchdog down mid-open) is TRANSIENT — never a permanent not-capable mark (that
        // would mean the console is "lost", contradicting the down-recovers leaf invariant). It must not crash
        // reconcile, must publish nothing while throwing, and must REOPEN on a later tick once it recovers.
        string topic = StreamProtocol.ServerConsoleTopic("boom");
        var (mgr, sent) = Manager(
            hasSubscribers: t => t == topic,
            anySubscription: pred => pred(topic));

        var wd = new FakeWatchdog([Native("boom")]);
        wd.SetFollowThrows("boom"); // the watchdog is down mid-open

        await mgr.ReconcileAsync(wd, CancellationToken.None); // must not throw out of reconcile
        await WaitUntil(() => wd.FollowOpenCount("boom") >= 1);
        await WaitUntil(() => !mgr.HasBridgeForTest("boom")); // the throwing follow self-ends, drops its entry
        Assert.Empty(sent);                                   // nothing published while failing

        // The watchdog recovers: a later reconcile REOPENS the follow (not permanently disabled) and streams.
        wd.SetFollow("boom", ["recovered line"], holdOpenAfter: true);
        await mgr.ReconcileAsync(wd, CancellationToken.None);
        await WaitUntil(() => sent.Count >= 1);

        Assert.True(wd.FollowOpenCount("boom") >= 2); // reopened — transient, recoverable
        Published line = Assert.Single(sent);
        Assert.Equal("console:boom:0", line.CoalesceKey);
        Assert.Equal("recovered line", ((ConsoleBridgeManager.ConsoleLineData)line.Message.Data).Line);

        await mgr.StopAllBridgesForTestAsync();
    }

    [Fact]
    public async Task NoConsoleSubscribers_DoesZeroWork()
    {
        var (mgr, sent) = Manager(
            hasSubscribers: _ => false,
            anySubscription: _ => false);

        var wd = new FakeWatchdog([Native("mc")]);
        wd.SetFollow("mc", ["x"], holdOpenAfter: true);

        await mgr.ReconcileAsync(wd, CancellationToken.None);

        Assert.Equal(0, wd.ListCallCount); // idle gate: no watchdog list, no bridges
        Assert.Equal(0, wd.FollowOpenCount("mc"));
        Assert.Empty(sent);

        await mgr.StopAllBridgesForTestAsync();
    }

    [Fact]
    public void ConsoleLine_SerializesToTheFrozenCamelCaseWireShape()
    {
        // The frozen WS frame: { topic, type, data:{ id, seq, line } } — camelCase, via the shared HTTP JSON
        // options the hub uses. The live WS follow is owed-to-human, so this locks the wire shape cheaply.
        var hub = new StreamHub(Microsoft.Extensions.Options.Options.Create(
            new Microsoft.AspNetCore.Http.Json.JsonOptions
            {
                SerializerOptions = { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase },
            }));
        string topic = StreamProtocol.ServerConsoleTopic("mc");
        var msg = new StreamMessage(topic, StreamProtocol.ConsoleLine,
            new ConsoleBridgeManager.ConsoleLineData("mc", 7, "hello world"));

        string json = System.Text.Json.JsonSerializer.Serialize(msg, hub.Json);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;
        Assert.Equal("servers/mc/console", root.GetProperty("topic").GetString());
        Assert.Equal("console.line", root.GetProperty("type").GetString());
        System.Text.Json.JsonElement data = root.GetProperty("data");
        Assert.Equal("mc", data.GetProperty("id").GetString());
        Assert.Equal(7, data.GetProperty("seq").GetInt64());
        Assert.Equal("hello world", data.GetProperty("line").GetString());
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    /// <summary>
    /// A fake <see cref="IWatchdogClient"/> whose console-follow behavior is scripted per instance: a fixed set
    /// of lines, then either complete (404/EOF) or hold open until the caller cancels the follow token. Records
    /// how many times each follow was opened, and whether it was cancelled — the bridge open/close assertions.
    /// </summary>
    private sealed class FakeWatchdog(IReadOnlyList<WatchdogInstanceState> states) : IWatchdogClient
    {
        private readonly Dictionary<string, (string[] lines, bool hold, bool throws)> _scripts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _opened = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _cancelled = new(StringComparer.Ordinal);
        public int ListCallCount;

        public void SetFollow(string name, string[] lines, bool holdOpenAfter) => _scripts[name] = (lines, holdOpenAfter, false);
        public void SetFollowThrows(string name) => _scripts[name] = (Array.Empty<string>(), false, true);

        public int FollowOpenCount(string name) => _opened.TryGetValue(name, out int n) ? n : 0;
        public bool FollowCancelled(string name) => _cancelled.TryGetValue(name, out bool c) && c;

        public async Task WaitForFollowOpened(string name, int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (FollowOpenCount(name) == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
        }

        public Task<IReadOnlyList<WatchdogInstanceState>> ListAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ListCallCount);
            return Task.FromResult(states);
        }

        public async IAsyncEnumerable<string> FollowConsoleAsync(
            string instanceName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _opened.AddOrUpdate(instanceName, 1, (_, n) => n + 1);
            (string[] lines, bool hold, bool throws) = _scripts.TryGetValue(instanceName, out var s)
                ? s : (Array.Empty<string>(), false, false);

            if (throws)
            {
                // Surface like a transport failure on open (the real client's GetAsync/EnsureSuccessStatusCode).
                throw new HttpRequestException("watchdog unreachable (test)");
            }

            foreach (string line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
                await Task.Yield();
            }

            if (hold)
            {
                // A capable console never self-completes — hold open until cancelled (the real contract).
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _cancelled[instanceName] = true;
                    throw;
                }
            }
            // else: complete (404 yield-break / a finite EOF) — the not-capable path when 0 lines were yielded.
        }

        public Task<IReadOnlyList<string>> GetConsoleTailAsync(string instanceName, int lines, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        // Unused by the console bridge — satisfy the interface.
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<WatchdogReadyState?> GetReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult<WatchdogReadyState?>(null);
        public Task<WatchdogActionResult> StartAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogActionResult> StopAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WatchdogInstanceState?> GetStatusAsync(string instanceName, CancellationToken cancellationToken = default) => Task.FromResult<WatchdogInstanceState?>(null);

        public void Dispose() { }
    }
}
