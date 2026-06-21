using System.Collections.Concurrent;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The live-console follow bridge (#8) — the resident piece behind the follow-only <c>servers/{id}/console</c>
/// WS topic. An always-running reconcile loop (AlertEngine-shaped, NOT a per-source pump) that, while a
/// console topic has subscribers, opens <strong>exactly one</strong> shared watchdog tail-bridge per native
/// instance and fans every appended line out to all of that topic's subscribers as a <c>console.line</c>
/// message. When the last subscriber leaves (or the instance vanishes, or on shutdown) it cancels that
/// bridge's token — which is the only thing that ends kgsm-lib's unbounded <see cref="IWatchdogClient.FollowConsoleAsync"/>
/// — so a closed/idle topic leaks no watchdog tail loop.
/// </summary>
/// <remarks>
/// <para><b>Why a shared bridge, not per-connection.</b> Twenty viewers watching one server's console must
/// not open twenty watchdog follows. The manager keys an open bridge by instance id; the hub fans the one
/// line stream out to every subscriber (each frame is serialized once, see <see cref="StreamHub.Publish"/>).</para>
/// <para><b>Coalesce key is per-line (<c>console:{id}:{seq}</c>), unique — the audit-append precedent.</b> A
/// static topic-level key would let a slow client's queue collapse all-but-the-latest line into one slot
/// (correct for a supersede-by-latest patch, WRONG for a log line). With a unique key, a slow client drops
/// <em>some</em> lines under backpressure (best-effort tail) but two lines never fuse into one. The durable
/// record is the watchdog's LogFile; the client re-hydrates scrollback via the REST endpoint on reconnect.</para>
/// <para><b>Native-only / non-capable handling (no retry-spam).</b> kgsm-lib's <c>FollowConsoleAsync</c>
/// answers a 404 (unknown / non-native / no-console) by completing the sequence <em>empty, without
/// throwing</em>, and a transport failure (watchdog down mid-open) by throwing. A genuinely capable console
/// NEVER self-completes (it holds the connection open, even on quiet/EOF/missing-log — it ends only when we
/// cancel). So: a follow that ends — by exception OR by yielding <b>zero</b> lines while we did not cancel —
/// is treated as <b>not console-capable</b>; the instance is marked so the reconcile loop never reopens it
/// (logged once). A follow that yielded ≥1 line and then ended normally is a genuine server-side disconnect
/// — the mark is NOT set, so it may reopen on the next tick if still subscribed.</para>
/// <para><b>Degrade gracefully.</b> <see cref="IWatchdogClient"/> is resolved OPTIONALLY (the AlertEngine
/// pattern — it is registered only when the watchdog is provisioned). Absent → the loop logs once and never
/// opens a bridge, never publishes, never 500s; the REST scrollback endpoint returns <c>{ lines: [] }</c>.</para>
/// <para><b>Threading.</b> <see cref="_bridges"/> and <see cref="_notCapable"/> are concurrent; each bridge
/// runs as its own <see cref="Task"/> (never awaited inline in the loop — the follow is unbounded). The
/// reconcile body (<see cref="ReconcileAsync"/>) is the single-threaded unit-test seam, and the publish hop
/// goes through <see cref="_publish"/> (defaulting to the hub) so a test can capture the exact
/// <c>(coalesceKey, message)</c> pairs the wire frame doesn't expose.</para>
/// </remarks>
public sealed class ConsoleBridgeManager : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);

    private readonly ApiOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<ConsoleBridgeManager> _logger;

    // id -> open bridge (its CTS + the running follow Task). Cancelling the CTS ends the unbounded follow.
    private readonly ConcurrentDictionary<string, Bridge> _bridges = new(StringComparer.Ordinal);
    // ids proven not console-capable (404 / threw / instant 0-line EOF) — never reopened (no retry-spam).
    private readonly ConcurrentDictionary<string, byte> _notCapable = new(StringComparer.Ordinal);

    // Test seams (defaulting to the real hub): the publish hop and the two subscription queries. The wire
    // frame doesn't expose the per-line coalesce key and the hub's subscriber state lives in real sockets,
    // so a unit test injects these to drive ReconcileAsync deterministically (the AlertEngine.Tick precedent).
    private readonly Action<string, string, StreamMessage> _publish;
    private readonly Func<string, bool> _hasSubscribers;
    private readonly Func<Func<string, bool>, bool> _anySubscription;

    public ConsoleBridgeManager(ApiOptions options, IServiceProvider services, StreamHub hub, ILogger<ConsoleBridgeManager> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
        _publish = hub.Publish;
        _hasSubscribers = hub.HasSubscribers;
        _anySubscription = hub.AnySubscription;
    }

    /// <summary>Test ctor: drive <see cref="ReconcileAsync"/> directly with controllable subscriber state and a
    /// captured publish hop (the wire frame doesn't carry the coalesce key). The watchdog is supplied
    /// per-reconcile (a fake), not via DI.</summary>
    internal ConsoleBridgeManager(ApiOptions options, ILogger<ConsoleBridgeManager> logger,
        Action<string, string, StreamMessage> publish,
        Func<string, bool> hasSubscribers,
        Func<Func<string, bool>, bool> anySubscription)
    {
        _options = options;
        _services = null!;
        _logger = logger;
        _publish = publish;
        _hasSubscribers = hasSubscribers;
        _anySubscription = anySubscription;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WatchdogProvisioned)
        {
            // No native supervisor on this host — there is no console source. The WS topic stays silent and
            // the REST scrollback degrades to { lines: [] } (ServerConsoleController handles the absent case).
            _logger.LogInformation(
                "Console: watchdog not provisioned — live console follow is off (the servers/{{id}}/console topic stays silent).");
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(ReconcileInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var watchdog = _services.GetService(typeof(IWatchdogClient)) as IWatchdogClient;
                    if (watchdog is null) continue; // registered only when provisioned — stay safe
                    await ReconcileAsync(watchdog, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "console reconcile failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
        finally
        {
            // Cancel every open follow so no watchdog tail loop leaks past shutdown.
            foreach (Bridge b in _bridges.Values) b.Stop();
        }
    }

    /// <summary>
    /// One reconcile pass: open a bridge for each subscribed-but-unbridged native instance, close a bridge
    /// whose topic lost its last subscriber or whose instance vanished. Pure over the engine's state apart
    /// from opening/cancelling follow tasks and the publish hop — the single-threaded unit-test seam.
    /// </summary>
    internal async Task ReconcileAsync(IWatchdogClient watchdog, CancellationToken stoppingToken)
    {
        // Idle gate: if NOTHING subscribes to any console topic, do zero work (no watchdog list, no bridges).
        // A still-open bridge with no subscribers is closed below regardless, so this can't strand one.
        bool anyConsoleSubscribers = _anySubscription(StreamProtocol.IsServerConsoleTopic);
        if (!anyConsoleSubscribers && _bridges.IsEmpty)
            return;

        // The candidate id set = the instances the watchdog supervises (cheap socket call, NATIVE-scoped —
        // exactly the instances that can have a console; AlertEngine uses the same source). A failed/absent
        // list is honest-unknown: skip opening this tick, but still tend (close) the bridges we already hold.
        IReadOnlyList<WatchdogInstanceState> states = Array.Empty<WatchdogInstanceState>();
        bool haveStates = false;
        if (anyConsoleSubscribers)
        {
            try
            {
                states = await watchdog.ListAsync(stoppingToken).ConfigureAwait(false);
                haveStates = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "console: watchdog list failed — not opening new bridges this tick");
            }
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        if (haveStates)
        {
            foreach (WatchdogInstanceState ws in states)
            {
                if (string.IsNullOrEmpty(ws.Name)) continue;
                present.Add(ws.Name);

                string topic = StreamProtocol.ServerConsoleTopic(ws.Name);
                if (!_hasSubscribers(topic)) continue;
                if (_notCapable.ContainsKey(ws.Name)) continue; // proven not-capable — never reopen (no spam)
                if (_bridges.ContainsKey(ws.Name)) continue;    // already following

                OpenBridge(watchdog, ws.Name, stoppingToken);
            }
        }

        // Close bridges whose topic lost its last subscriber, or whose instance vanished from a fresh list.
        foreach ((string id, Bridge bridge) in _bridges)
        {
            bool stillSubscribed = _hasSubscribers(StreamProtocol.ServerConsoleTopic(id));
            bool vanished = haveStates && !present.Contains(id);
            if (!stillSubscribed || vanished)
            {
                if (_bridges.TryRemove(id, out Bridge? removed))
                    removed.Stop();
            }
        }
    }

    /// <summary>Test helper: is a bridge currently open (following) for <paramref name="id"/>? Lets a test
    /// wait for a self-ending follow to drain before driving the reconcile that should reopen it.</summary>
    internal bool HasBridgeForTest(string id) => _bridges.ContainsKey(id);

    /// <summary>Test teardown: cancel every open follow and await its loop so a test driving
    /// <see cref="ReconcileAsync"/> directly (no hosted <c>ExecuteAsync</c>) leaks no held-open follow Task.</summary>
    internal async Task StopAllBridgesForTestAsync()
    {
        Bridge[] open = _bridges.Values.ToArray();
        _bridges.Clear();
        foreach (Bridge b in open) b.Stop();
        foreach (Bridge b in open)
        {
            if (b.Task is { } t)
            {
                try { await t.ConfigureAwait(false); } catch { /* cancellation noise */ }
            }
        }
    }

    private void OpenBridge(IWatchdogClient watchdog, string id, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var bridge = new Bridge(cts);
        if (!_bridges.TryAdd(id, bridge))
        {
            cts.Dispose();
            return;
        }
        bridge.Task = RunFollowAsync(watchdog, id, cts.Token);
    }

    /// <summary>
    /// The shared per-instance follow loop. Yields each appended line out to the topic's subscribers with a
    /// UNIQUE, monotonically-incrementing per-line coalesce key. Ends only when the bridge's token is
    /// cancelled (the normal close) or the server disconnects. A follow that ends by throwing, or by yielding
    /// ZERO lines without us cancelling, marks the instance not-capable so the reconcile loop never reopens it.
    /// </summary>
    private async Task RunFollowAsync(IWatchdogClient watchdog, string id, CancellationToken ct)
    {
        string topic = StreamProtocol.ServerConsoleTopic(id);
        long seq = 0;
        try
        {
            await foreach (string line in watchdog.FollowConsoleAsync(id, ct).ConfigureAwait(false))
            {
                _publish(topic, StreamProtocol.ConsoleEntityKey(id, seq),
                    new StreamMessage(topic, StreamProtocol.ConsoleLine, new ConsoleLineData(id, seq, line)));
                seq++;
            }

            // The sequence ended without us cancelling. A capable console NEVER self-completes, so:
            //  - zero lines  -> a 404 yield-break (non-native / unknown / no-console): NOT capable, don't reopen.
            //  - >=1 line     -> a genuine server-side disconnect: leave it reopenable next tick if still subscribed.
            if (!ct.IsCancellationRequested && seq == 0)
            {
                if (_notCapable.TryAdd(id, 0))
                    _logger.LogInformation(
                        "Console: '{Instance}' is not console-capable (non-native / unknown / no console) — not following it.", id);
            }
        }
        catch (OperationCanceledException) { /* the normal close: our token fired */ }
        catch (Exception ex)
        {
            // A TRANSIENT failure — a transport throw (watchdog down mid-open) or a non-404 error. We do NOT
            // mark not-capable here: that flag is permanent (never cleared), and a watchdog blip is "down,
            // still there → recovers", never "lost" (the leaf-health invariant). Retry is naturally bounded —
            // the next reconcile only reopens after a SUCCESSFUL ListAsync, so a fully-down watchdog produces
            // zero follow attempts (honest-unknown, the AlertEngine precedent: the interval is the debounce).
            // Only the structural seq==0 completion above (a 404 yield-break) is a permanent not-capable.
            _logger.LogDebug(ex, "console: follow for '{Instance}' failed — will retry on a later tick if still subscribed", id);
        }
        finally
        {
            // Drop our entry if this loop ended on its own (404 / disconnect / throw) so the dict never shows
            // a dead bridge — a future capable subscribe can then reopen (unless marked not-capable above,
            // which the reconcile loop honors first). A no-op if the reconcile close path already removed us.
            _bridges.TryRemove(id, out _);
        }
    }

    /// <summary>The data payload of a <see cref="StreamProtocol.ConsoleLine"/> message.</summary>
    internal sealed record ConsoleLineData(string Id, long Seq, string Line);

    // One open follow: the CTS that ends it + the running Task. Stop() cancels then disposes the CTS.
    private sealed class Bridge(CancellationTokenSource cts)
    {
        public Task? Task { get; set; }
        public void Stop()
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }
}
