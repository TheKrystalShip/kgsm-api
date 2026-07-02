using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// One live <c>/api/v1/stream</c> SSE connection. Owns the response body's write loop: drains a
/// <strong>coalesce-to-latest</strong> outbound queue as <c>data: &lt;json&gt;\n\n</c> frames,
/// with a keepalive comment every 20s. The pumps never touch the body — they
/// <see cref="Enqueue"/> through the <c>StreamHub</c>; this class is the only thing that writes
/// the wire.
/// </summary>
/// <remarks>
/// <para><b>Backpressure (the §3·j-aligned resilience contract).</b> The outbound queue holds at most
/// one pending frame per coalesce key — a newer frame supersedes an unsent older one of the same key.
/// This bounds memory to the number of distinct topics/entities a client subscribes to (it can never
/// grow unbounded under a slow client) and matches the client's "just apply the latest" model: a
/// dropped intermediate metric tick is irrelevant, and a status flip never gets dropped behind metric
/// ticks because they carry different keys. If a send still stalls past <see cref="SendTimeout"/> the
/// connection is torn down, and §3·j's client falls back to polling, reconnects, and re-hydrates.</para>
/// <para><b>No initial snapshot.</b> The client hydrates via REST on (re)connect (§3·j) and the stream
/// only pushes patches from the next tick on — so subscribing never replays state here.</para>
/// </remarks>
public sealed class StreamConnection
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly Stream _body;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger _logger;

    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _subLock = new();

    // coalesce key -> latest unsent frame. The wake channel is a 1-slot signal (extra writes dropped):
    // the writer always drains ALL pending under the lock, so the token is only a "something changed" hint.
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _pending = new(StringComparer.Ordinal);
    private readonly object _queueLock = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });

    public StreamConnection(Stream body, IEnumerable<string> topics, JsonSerializerOptions json, ILogger logger)
    {
        _body = body;
        _json = json;
        _logger = logger;
        foreach (string t in topics)
            _subscriptions.Add(t);
    }

    // --- subscription state (read by the hub on every publish) ---

    public bool IsSubscribed(string topic)
    {
        lock (_subLock) return _subscriptions.Contains(topic);
    }

    public bool HasMatchingSubscription(Func<string, bool> match)
    {
        lock (_subLock)
        {
            foreach (string topic in _subscriptions)
                if (match(topic)) return true;
            return false;
        }
    }

    /// <summary>Queue a serialized frame, superseding any unsent frame with the same coalesce key.</summary>
    public void Enqueue(string coalesceKey, ReadOnlyMemory<byte> frame)
    {
        lock (_queueLock) _pending[coalesceKey] = frame;
        _wake.Writer.TryWrite(0); // wake the writer; dropped if a wake is already pending (it drains all anyway)
    }

    /// <summary>Run the write loop until cancel/disconnect/failed-write.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // Write the initial connected comment so the client's fetch resolves promptly (drives mode→live).
        try
        {
            byte[] connected = Encoding.UTF8.GetBytes(": connected\n\n");
            await _body.WriteAsync(connected, ct).ConfigureAwait(false);
            await _body.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSE stream: failed to write connected comment");
            return;
        }

        try
        {
            await WriteLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSE stream: write loop ended");
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Wait for either a pending frame or the heartbeat interval. A per-iteration linked CTS
            // lets us cancel the loser once the race is decided — which both releases the abandoned
            // 20s heartbeat timer on every wake (no timer pile-up under a busy stream) and, critically,
            // guarantees the loop OBSERVES ct. On client disconnect ct is cancelled, so both awaited
            // tasks complete synchronously; without the ct guards below the wake branch would drain an
            // empty queue and `continue` forever without ever yielding — a 100% CPU spin that outlives
            // the (now dead) connection.
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<bool> wakeTask = _wake.Reader.WaitToReadAsync(tick.Token).AsTask();
            Task delayTask = Task.Delay(HeartbeatInterval, tick.Token);

            Task finished = await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
            tick.Cancel(); // release the loser (abandoned heartbeat delay or pending wait)

            if (ct.IsCancellationRequested) break; // client gone — tear down, never spin

            if (finished == delayTask)
            {
                // Heartbeat: write a keepalive comment.
                try
                {
                    byte[] keepalive = Encoding.UTF8.GetBytes(": keepalive\n\n");
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    sendCts.CancelAfter(SendTimeout);
                    await _body.WriteAsync(keepalive, sendCts.Token).ConfigureAwait(false);
                    await _body.FlushAsync(sendCts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break; // stalled client or cancelled — tear down
                }
                continue;
            }

            // Wake branch: drain all pending frames.
            while (_wake.Reader.TryRead(out _)) { } // collapse coalesced wakes

            ReadOnlyMemory<byte>[] frames;
            lock (_queueLock)
            {
                if (_pending.Count == 0) continue;
                frames = new ReadOnlyMemory<byte>[_pending.Count];
                _pending.Values.CopyTo(frames, 0);
                _pending.Clear();
            }

            bool writeFailed = false;
            foreach (ReadOnlyMemory<byte> frame in frames)
            {
                try
                {
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    sendCts.CancelAfter(SendTimeout);
                    await _body.WriteAsync(frame, sendCts.Token).ConfigureAwait(false);
                    await _body.FlushAsync(sendCts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    writeFailed = true;
                    break;
                }
            }
            if (writeFailed) break;
        }
    }
}
