using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;

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
/// <para><b>The session is re-checked while the stream runs.</b> Authorization is evaluated per
/// REQUEST, and this stream is one request that lasts hours — so the connect-time
/// <c>[Authorize]</c> is the only gate the framework applies, and a session revoked afterwards would
/// keep receiving the host's roster, metrics, console lines and audit rows until the tab closed.
/// <c>sessionAlive</c> closes that: the write loop asks the session registry every
/// <see cref="SessionRecheckInterval"/> whether this connection's <c>sid</c> is still live, and ends
/// the connection when it isn't. Revoking a session therefore cuts its live channel within 20s, the
/// same order as the ≤5s bound REST already has. The check is <b>on the session, not on the access
/// token's expiry</b>: an access token lapses every ~15 minutes by design and the client rotates it
/// reactively, so tearing down on that would churn every stream four times an hour for a credential
/// that is about to be renewed.</para>
/// <para><b>What the reader may do is re-checked on the same clock.</b> Authorization is stamped at
/// connect and an admin can change it minutes later, so the loop also re-reads the account store on
/// every re-check tick. A tier that has moved is applied in place: the connection's own
/// <see cref="Tier"/> becomes the new one, every subscription above it is dropped
/// (<see cref="ApplyTier"/>), and a <c>me.patch</c> tells the reader. This is the backstop, not the
/// fast path — a change made through this API's own endpoints reaches the connection at once through
/// <see cref="StreamHub.AuthorityChanged"/> — and it is what covers the writers this API never sees:
/// the account store is a shared host file that the assistant, the bot and the <c>kgsm-api user</c>
/// command all write.</para>
/// </remarks>
public sealed class StreamConnection
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    // The lag bound for an OPEN stream — how long a revoked session keeps its live channel, and how
    // long a tier changed out of this process keeps its old reach. Its own constant, not the
    // heartbeat's: retuning the keepalive cadence must not silently move either.
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(20);

    private readonly Stream _body;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, ValueTask<bool>>? _sessionAlive;
    private readonly Func<CancellationToken, ValueTask<AccountStanding>>? _authority;
    private readonly TimeSpan _recheckInterval;

    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _subLock = new();

    // The reader's tier, as an int so a publish can read it without taking _subLock. Written only
    // under that lock, together with the subscription strip it drives (see ApplyTier).
    private int _tier;

    // coalesce key -> latest unsent frame. The wake channel is a 1-slot signal (extra writes dropped):
    // the writer always drains ALL pending under the lock, so the token is only a "something changed" hint.
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _pending = new(StringComparer.Ordinal);
    private readonly object _queueLock = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });

    /// <param name="sessionAlive">
    /// Asks whether the session behind this connection is still live. <see langword="null"/> when there
    /// is no session to check — an auth-disabled host's synthetic principal carries no <c>sid</c> — in
    /// which case the stream runs exactly as it did before, unchecked.
    /// </param>
    /// <param name="tier">
    /// What the reader behind this connection may do, as the account store answered it at connect. It
    /// gates the frames whose values differ by tier (the audit feed's personal and privileged fields)
    /// and the topics this connection keeps, and it moves while the stream runs — see
    /// <see cref="ApplyTier"/>. <b>Defaults to <see cref="KgsmTier.None"/></b>: a connection nobody
    /// stated a tier for is the restricted one.
    /// </param>
    /// <param name="accountId">
    /// The KGSM account this connection is authenticated as, which is what a frame about one person is
    /// addressed to. The account id rather than the token's handle, because an account can be proved
    /// several ways — a password and each linked provider identity — and all of them are one person
    /// here. <see langword="null"/> when the caller proves no account on this host, in which case
    /// nothing addressed to an account ever reaches it.
    /// </param>
    /// <param name="sessionId">
    /// The session behind this connection, for the operator-facing view of who is streaming.
    /// <see langword="null"/> on a host whose principal carries no <c>sid</c>.
    /// </param>
    /// <param name="authority">
    /// Re-reads where the reader's account stands, on the same clock as
    /// <paramref name="sessionAlive"/>. <see langword="null"/> when there is no account to re-read —
    /// an auth-disabled host's synthetic principal — in which case the connection keeps the tier it
    /// was given for as long as it lasts.
    /// </param>
    public StreamConnection(
        Stream body,
        IEnumerable<string> topics,
        JsonSerializerOptions json,
        ILogger logger,
        Func<CancellationToken, ValueTask<bool>>? sessionAlive = null,
        KgsmTier tier = KgsmTier.None,
        string? accountId = null,
        string? sessionId = null,
        Func<CancellationToken, ValueTask<AccountStanding>>? authority = null)
        : this(body, topics, json, logger, sessionAlive, RecheckInterval, tier, accountId, sessionId, authority)
    {
    }

    /// <summary>
    /// Overload taking the re-check cadence, so a test can prove the teardown without sitting through
    /// the real 20s. Deliberately not an <c>ApiOptions</c> knob: this interval is a security bound
    /// (how long a revoked session keeps its live channel, and how long a changed tier keeps its old
    /// reach), not an operational tuning dial.
    /// </summary>
    internal StreamConnection(
        Stream body,
        IEnumerable<string> topics,
        JsonSerializerOptions json,
        ILogger logger,
        Func<CancellationToken, ValueTask<bool>>? sessionAlive,
        TimeSpan recheckInterval,
        KgsmTier tier = KgsmTier.None,
        string? accountId = null,
        string? sessionId = null,
        Func<CancellationToken, ValueTask<AccountStanding>>? authority = null)
    {
        _body = body;
        _json = json;
        _logger = logger;
        _sessionAlive = sessionAlive;
        _authority = authority;
        _recheckInterval = recheckInterval;
        _tier = (int)tier;
        AccountId = accountId;
        SessionId = sessionId;
        foreach (string t in topics)
            _subscriptions.Add(t);
    }

    /// <summary>The KGSM account this connection is authenticated as. See the constructor.</summary>
    public string? AccountId { get; }

    /// <summary>The session behind this connection. See the constructor.</summary>
    public string? SessionId { get; }

    /// <summary>What this connection's reader may do, right now.</summary>
    public KgsmTier Tier => (KgsmTier)Volatile.Read(ref _tier);

    /// <summary>Whether this connection's reader holds operator or above — what the hub reads for the
    /// few frames whose values differ by tier.</summary>
    public bool IsOperator => Tier >= KgsmTier.Operator;

    /// <summary>Is this connection authenticated as <paramref name="accountId"/>? A connection that
    /// proves no account is nobody's, and answers <see langword="false"/> to every id.</summary>
    public bool BelongsTo(string accountId) =>
        AccountId is not null && string.Equals(AccountId, accountId, StringComparison.Ordinal);

    /// <summary>
    /// Move this connection to <paramref name="tier"/> in place, dropping every subscription that tier
    /// does not reach. Returns whether anything changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It only ever takes reach away. A promotion never adds a topic back, because the connection's
    /// subscription set is what the client asked for filtered by what it held — and this cannot tell
    /// the difference between a topic the client did not want and one it was refused. The client that
    /// wants the newly-reachable topics opens a stream that asks for them, which is the same thing it
    /// does on any other reconnect.
    /// </para>
    /// <para>
    /// The tier is written before the subscriptions are stripped, so a publish racing this reads a
    /// connection that is at most as privileged as either state and never more than both.
    /// </para>
    /// </remarks>
    public bool ApplyTier(KgsmTier tier)
    {
        lock (_subLock)
        {
            if ((KgsmTier)_tier == tier)
                return false;

            Volatile.Write(ref _tier, (int)tier);
            _subscriptions.RemoveWhere(t => StreamProtocol.MinimumTier(t) > tier);
            return true;
        }
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

    /// <summary>
    /// Tell this connection's reader where their own account now stands, on the <c>me</c> topic.
    /// </summary>
    /// <remarks>
    /// Subscription is still the gate: a client that never asked for <c>me</c> is not sent one, the
    /// same rule every other topic follows. The frame is built here rather than by the hub because
    /// this is the one message with an audience of exactly one connection — nothing is saved by
    /// serializing it anywhere else.
    /// </remarks>
    private void EnqueueStanding(AccountStanding standing)
    {
        if (!IsSubscribed(StreamProtocol.MeTopic))
            return;

        Enqueue(StreamProtocol.MeEntityKey, StreamHub.BuildFrame(
            new StreamMessage(
                StreamProtocol.MeTopic,
                StreamProtocol.MePatch,
                new MeStanding(KgsmTiers.ToWire(standing.Tier), standing.Status)),
            _json));
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
        // The loop's two TIMED duties, each on its own due-time. They are clock-driven rather than
        // "whichever task won the race", because the race answers a different question: a busy stream
        // is woken by frames far more often than any delay completes, so a duty hung off the delay
        // branch simply never runs on the connections carrying the most data. Both clocks are the
        // connect time plus their interval — [Authorize] has just run, and the client has just been
        // told ": connected".
        bool rechecks = _sessionAlive is not null || _authority is not null;
        DateTimeOffset nextRecheck = DateTimeOffset.UtcNow + _recheckInterval;
        DateTimeOffset nextHeartbeat = DateTimeOffset.UtcNow + HeartbeatInterval;

        while (!ct.IsCancellationRequested)
        {
            // Wait for a pending frame or the next duty, whichever comes first. A per-iteration linked
            // CTS lets us cancel the loser once the race is decided — which both releases the abandoned
            // timer on every wake (no timer pile-up under a busy stream) and, critically, guarantees the
            // loop OBSERVES ct. On client disconnect ct is cancelled, so both awaited tasks complete
            // synchronously; without the ct guard below the wake branch would drain an empty queue and
            // `continue` forever without ever yielding — a 100% CPU spin that outlives the (now dead)
            // connection. The wait can't stay at zero and spin either: a duty that fires immediately
            // pushes its own due-time forward before the next iteration.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan wait = nextHeartbeat - now;
            if (rechecks)
            {
                TimeSpan untilCheck = nextRecheck - now;
                if (untilCheck < wait) wait = untilCheck;
            }
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;

            using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<bool> wakeTask = _wake.Reader.WaitToReadAsync(tick.Token).AsTask();
            Task delayTask = Task.Delay(wait, tick.Token);

            await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
            tick.Cancel(); // release the loser (abandoned duty timer or pending wait)

            if (ct.IsCancellationRequested) break; // client gone — tear down, never spin

            // Duty 1: does this connection still stand? Two questions on one clock — is the session
            // behind it live, and what may its reader do now. Ordered before any write, so a session
            // that has just been revoked receives nothing further and a reader who has just been
            // demoted receives nothing their new tier does not reach.
            if (rechecks && DateTimeOffset.UtcNow >= nextRecheck)
            {
                nextRecheck = DateTimeOffset.UtcNow + _recheckInterval;

                bool alive;
                try
                {
                    alive = _sessionAlive is null || await _sessionAlive(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // Couldn't measure. "Unknown" is not "still valid" (the ecosystem's never-fabricate
                    // rule, in its security form), and the teardown is self-correcting rather than
                    // final: the client re-dials, and that redial re-runs the FULL authentication
                    // pipeline — which is the authority on whether it may stream — instead of us
                    // guessing here.
                    _logger.LogWarning(ex, "SSE stream: session re-check failed; ending the connection");
                    break;
                }
                if (!alive)
                {
                    _logger.LogInformation("SSE stream: session no longer valid (revoked or expired) — ending the connection");
                    break;
                }

                if (_authority is not null)
                {
                    AccountStanding standing;
                    try
                    {
                        standing = await _authority(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        // Same rule as the session above, and the same reason it is not softened into a
                        // demotion: an unreadable store means this connection's reach cannot be
                        // measured, and a stream carrying an unmeasurable reach is one nobody can
                        // vouch for. Ending it costs a redial, which asks the authority properly.
                        _logger.LogWarning(ex, "SSE stream: authority re-check failed; ending the connection");
                        break;
                    }

                    if (ApplyTier(standing.Tier))
                    {
                        _logger.LogInformation(
                            "SSE stream: the reader's tier is now {Tier}; re-gated the connection in place.",
                            KgsmTiers.ToWire(standing.Tier));
                        EnqueueStanding(standing);
                    }
                }
            }

            // Duty 2: the keepalive comment, which is also the dead-client detector (alongside
            // RequestAborted). On its own clock too, so the client hears from us every 20s whether the
            // stream has been silent or saturated.
            if (DateTimeOffset.UtcNow >= nextHeartbeat)
            {
                nextHeartbeat = DateTimeOffset.UtcNow + HeartbeatInterval;
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
            }

            // Then drain whatever frames are pending (nothing, when a duty is what woke us).
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
