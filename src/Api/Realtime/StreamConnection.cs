using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// One live <c>/api/v1/stream</c> WebSocket. Owns the socket's full-duplex loops: a reader that
/// applies <c>subscribe</c>/<c>unsubscribe</c> commands, and a writer that drains a
/// <strong>coalesce-to-latest</strong> outbound queue. The pumps never touch the socket — they
/// <see cref="Enqueue"/> through the <c>StreamHub</c>; this class is the only thing that reads/writes
/// the wire (a WebSocket permits one concurrent send + one concurrent receive, which the two loops honor).
/// </summary>
/// <remarks>
/// <para><b>Backpressure (the §3·j-aligned resilience contract).</b> The outbound queue holds at most
/// one pending frame per coalesce key — a newer frame supersedes an unsent older one of the same key.
/// This bounds memory to the number of distinct topics/entities a client subscribes to (it can never
/// grow unbounded under a slow client) and matches the client's "just apply the latest" model: a
/// dropped intermediate metric tick is irrelevant, and a status flip never gets dropped behind metric
/// ticks because they carry different keys. If a send still stalls past <see cref="SendTimeout"/> the
/// connection is torn down, and §3·j's client falls back to polling, reconnects, and re-hydrates.</para>
/// <para><b>No initial snapshot.</b> The client hydrates via REST on (re)connect (§3·j) and the socket
/// only pushes patches from the next tick on — so subscribing never replays state here.</para>
/// </remarks>
public sealed class StreamConnection
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly WebSocket _socket;
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

    public StreamConnection(WebSocket socket, JsonSerializerOptions json, ILogger logger)
    {
        _socket = socket;
        _json = json;
        _logger = logger;
    }

    // --- subscription state (read by the hub on every publish; mutated by the reader loop) ---

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

    /// <summary>Run both loops until the socket closes, the client disconnects, or <paramref name="ct"/> fires.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task reader = ReadLoopAsync(cts.Token);
        Task writer = WriteLoopAsync(cts.Token);

        await Task.WhenAny(reader, writer).ConfigureAwait(false);
        cts.Cancel();              // one loop ended (close/error/cancel) -> tear down the other
        _wake.Writer.TryComplete();
        try { await Task.WhenAll(reader, writer).ConfigureAwait(false); }
        catch { /* teardown: cancellation / socket-aborted noise is expected here */ }

        await CloseQuietlyAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var message = new List<byte>(); // accumulates a fragmented message; commands are tiny
        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;

            message.AddRange(buffer.AsSpan(0, result.Count));
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Text)
                HandleCommand(message.ToArray());
            message.Clear();
        }
    }

    private void HandleCommand(byte[] utf8)
    {
        ClientCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<ClientCommand>(utf8, _json); }
        catch (JsonException) { return; } // malformed frame: ignore (no client-error protocol until later)

        if (cmd?.Topics is not { Length: > 0 } topics) return;

        switch (cmd.Type)
        {
            case StreamProtocol.Subscribe:
                lock (_subLock)
                    foreach (string t in topics)
                        if (!string.IsNullOrWhiteSpace(t)) _subscriptions.Add(t);
                break;
            case StreamProtocol.Unsubscribe:
                lock (_subLock)
                    foreach (string t in topics) _subscriptions.Remove(t);
                break;
            default:
                break; // unknown command type: ignore (forward-compatible)
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_wake.Reader.TryRead(out _)) { } // collapse coalesced wakes

                ReadOnlyMemory<byte>[] frames;
                lock (_queueLock)
                {
                    if (_pending.Count == 0) continue;
                    frames = new ReadOnlyMemory<byte>[_pending.Count];
                    _pending.Values.CopyTo(frames, 0);
                    _pending.Clear();
                }

                foreach (ReadOnlyMemory<byte> frame in frames)
                {
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    sendCts.CancelAfter(SendTimeout); // a stalled client gets torn down, not allowed to back up
                    await _socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, sendCts.Token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ChannelClosedException) { }
    }

    private async Task CloseQuietlyAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch { /* best-effort close; the socket may already be aborted */ }
    }

    /// <summary>The inbound client command: <c>{ "type": "subscribe"|"unsubscribe", "topics": [...] }</c>.</summary>
    private sealed record ClientCommand(string? Type, string[]? Topics);
}
