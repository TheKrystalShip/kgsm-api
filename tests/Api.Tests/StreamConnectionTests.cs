using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit tests for the SSE write loop's lifecycle. The load-bearing one is the busy-loop regression:
/// a disconnected client cancels the per-request token, and the loop MUST tear down rather than spin.
/// </summary>
public class StreamConnectionTests
{
    private static StreamConnection NewConnection(Stream body) =>
        new(body, new[] { "servers" }, new JsonSerializerOptions(), NullLogger.Instance);

    /// <summary>
    /// Regression for the 2026-07-02 prod incident: every disconnected SSE client orphaned a ThreadPool
    /// thread at 100% CPU. On client disconnect the connection token (<c>RequestAborted</c>) is cancelled;
    /// the buggy wake branch never observed it, so <c>await Task.WhenAny(canceledWait, canceledDelay)</c>
    /// completed synchronously every iteration and the loop drained an empty queue and <c>continue</c>d
    /// forever without yielding. The contract: cancelling the token stops <see cref="StreamConnection.RunAsync"/>
    /// promptly — well under the 20s heartbeat, never a spin.
    /// </summary>
    [Fact]
    public async Task RunAsync_stops_promptly_when_the_connection_token_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        StreamConnection conn = NewConnection(new MemoryStream());

        Task run = conn.RunAsync(cts.Token);

        // Let it write the ": connected" comment and settle into the idle heartbeat wait.
        await Task.Delay(150);
        Assert.False(run.IsCompleted, "the loop should still be waiting before cancellation");

        cts.Cancel();

        // Fixed loop returns near-instantly; the buggy loop never would. A 5s ceiling (< the 20s
        // heartbeat) keeps this decisive without being flaky under CI load.
        Task winner = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == run, "RunAsync did not stop after its token was cancelled (busy-loop regression)");

        await run; // observe: RunAsync swallows the cancellation, so this must not throw.
    }

    // ---- the mid-stream session re-check -----------------------------------
    // Authorization runs once, at connect, on a request that then lasts hours. These lock the loop's
    // own re-check: a session revoked mid-stream loses its live channel, and a healthy one is left
    // alone. The cadence is injected (the internal ctor) so the proof doesn't cost the real 20s.

    private static StreamConnection NewChecked(
        Stream body, Func<CancellationToken, ValueTask<bool>>? sessionAlive, TimeSpan? interval = null) =>
        new(body, new[] { "servers" }, new JsonSerializerOptions(), NullLogger.Instance,
            sessionAlive, interval ?? TimeSpan.FromMilliseconds(120));

    private static async Task<bool> Ended(Task run, int ms = 3000) =>
        await Task.WhenAny(run, Task.Delay(ms)).ConfigureAwait(false) == run;

    /// <summary>The revocation case: once the registry stops vouching for the session, the stream ends.</summary>
    [Fact]
    public async Task RunAsync_ends_the_stream_once_the_session_stops_being_valid()
    {
        using var cts = new CancellationTokenSource();
        var alive = true;
        StreamConnection conn = NewChecked(new MemoryStream(), _ => new ValueTask<bool>(Volatile.Read(ref alive)));

        Task run = conn.RunAsync(cts.Token);
        await Task.Delay(300);
        Assert.False(run.IsCompleted, "a live session must keep streaming across re-checks");

        Volatile.Write(ref alive, false);   // the operator revoked it
        Assert.True(await Ended(run), "the stream outlived its session");
        await run;
    }

    /// <summary>
    /// The placement regression. A busy stream is woken by frames far more often than the heartbeat
    /// delay ever completes, so the delay branch never wins — a re-check living inside it would never
    /// fire on exactly the connections carrying the most data. The check runs on the loop's own clock,
    /// so a revoked session is cut off whether its stream is idle or saturated.
    /// </summary>
    [Fact]
    public async Task RunAsync_rechecks_the_session_on_a_stream_too_busy_to_ever_reach_the_heartbeat()
    {
        using var cts = new CancellationTokenSource();
        var alive = true;
        StreamConnection conn = NewChecked(new MemoryStream(), _ => new ValueTask<bool>(Volatile.Read(ref alive)));

        Task run = conn.RunAsync(cts.Token);
        // Keep it saturated for the whole test: a frame every 10ms against a 120ms re-check.
        Task pump = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && !run.IsCompleted)
            {
                conn.Enqueue("servers", Encoding.UTF8.GetBytes("data: {\"topic\":\"servers\"}\n\n"));
                await Task.Delay(10);
            }
        });

        await Task.Delay(300);
        Assert.False(run.IsCompleted, "the busy stream should still be running while its session is valid");

        Volatile.Write(ref alive, false);
        Assert.True(await Ended(run), "a busy stream never re-checked its session");
        await run;
        cts.Cancel();
        await pump;
    }

    /// <summary>
    /// Fail-closed. "Couldn't measure" is not "still valid" — and the teardown is self-correcting, since
    /// the client's redial re-runs the full authentication pipeline, which is the actual authority.
    /// </summary>
    [Fact]
    public async Task RunAsync_ends_the_stream_when_the_session_check_throws()
    {
        using var cts = new CancellationTokenSource();
        StreamConnection conn = NewChecked(new MemoryStream(),
            _ => throw new InvalidOperationException("registry unreachable"));

        Task run = conn.RunAsync(cts.Token);
        Assert.True(await Ended(run), "an unmeasurable session was treated as a valid one");
        await run;   // the loop swallows it — a failed check is not an unhandled fault
    }

    /// <summary>A session that stays valid is never torn down by the re-check itself.</summary>
    [Fact]
    public async Task RunAsync_leaves_a_valid_session_streaming()
    {
        using var cts = new CancellationTokenSource();
        var checks = 0;
        StreamConnection conn = NewChecked(new MemoryStream(), _ =>
        {
            Interlocked.Increment(ref checks);
            return new ValueTask<bool>(true);
        });

        Task run = conn.RunAsync(cts.Token);

        // Deadline-polled, not a fixed sleep: a loaded runner stretches the re-check interval
        // arbitrarily, and the property here is "re-checked repeatedly", not "re-checked fast".
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref checks) < 2 && DateTime.UtcNow < deadline)
        {
            Assert.False(run.IsCompleted, "a valid session was torn down by its own re-check");
            await Task.Delay(50);
        }

        Assert.False(run.IsCompleted, "a valid session was torn down by its own re-check");
        Assert.True(Volatile.Read(ref checks) >= 2, $"expected repeated re-checks, saw {Volatile.Read(ref checks)}");

        cts.Cancel();
        await run;
    }

    /// <summary>
    /// No session to check (an auth-disabled host's synthetic principal carries no <c>sid</c>) → the
    /// stream runs exactly as it did before, and nothing ends it but the client or the app.
    /// </summary>
    [Fact]
    public async Task RunAsync_never_ends_a_stream_that_has_no_session_to_check()
    {
        using var cts = new CancellationTokenSource();
        StreamConnection conn = NewChecked(new MemoryStream(), sessionAlive: null);

        Task run = conn.RunAsync(cts.Token);
        await Task.Delay(500);
        Assert.False(run.IsCompleted, "an unchecked stream ended on its own");

        cts.Cancel();
        await run;
    }


    // ---- the mid-stream authority re-check ---------------------------------
    // The same clock, asking the other question: what may this reader do now. It is the backstop for
    // the writers this process never sees — the account store is a shared host file — so it re-reads
    // rather than waiting to be told.

    private static StreamConnection NewGoverned(
        Stream body,
        IEnumerable<string> topics,
        KgsmTier tier,
        Func<CancellationToken, ValueTask<AccountStanding>> authority) =>
        new(body, topics, new JsonSerializerOptions(JsonSerializerDefaults.Web), NullLogger.Instance,
            sessionAlive: null, TimeSpan.FromMilliseconds(120), tier, accountId: "usr_alice",
            sessionId: "sid_alice", authority);

    /// <summary>
    /// A tier changed by something other than this API's own endpoints still reaches the connection:
    /// the operator-only subscription goes, and the reader is told where they now stand.
    /// </summary>
    [Fact]
    public async Task RunAsync_regates_and_tells_the_reader_when_the_store_moves_the_tier()
    {
        using var cts = new CancellationTokenSource();
        var body = new MemoryStream();
        var demoted = false;
        StreamConnection conn = NewGoverned(body, ["hosts/h/services", "me"], KgsmTier.Operator,
            _ => new ValueTask<AccountStanding>(Volatile.Read(ref demoted)
                ? new AccountStanding("usr_alice", KgsmTier.Viewer, "active")
                : new AccountStanding("usr_alice", KgsmTier.Operator, "active")));

        Task run = conn.RunAsync(cts.Token);
        await Task.Delay(300);
        Assert.True(conn.IsSubscribed("hosts/h/services"), "an unchanged tier must not re-gate anything");
        Assert.DoesNotContain("me.patch", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);

        Volatile.Write(ref demoted, true);

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (conn.IsOperator && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.False(conn.IsOperator, "the connection kept a tier the store had already taken away");
        Assert.False(conn.IsSubscribed("hosts/h/services"));

        while (!Encoding.UTF8.GetString(body.ToArray()).Contains("me.patch", StringComparison.Ordinal)
               && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        string written = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"topic\":\"me\"", written, StringComparison.Ordinal);
        Assert.Contains("\"tier\":\"viewer\"", written, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"active\"", written, StringComparison.Ordinal);

        cts.Cancel();
        await run;
    }

    /// <summary>
    /// Fail-closed, and for the same reason the session check is: an account store that cannot be read
    /// leaves this connection's reach unmeasurable, and it is never softened into a demotion — that
    /// would report an outage as everybody having lost their access. The redial asks properly.
    /// </summary>
    [Fact]
    public async Task RunAsync_ends_the_stream_when_the_authority_check_throws()
    {
        using var cts = new CancellationTokenSource();
        StreamConnection conn = NewGoverned(new MemoryStream(), ["me"], KgsmTier.Viewer,
            _ => throw new KgsmAuthProviderException("the account store could not be read"));

        Task run = conn.RunAsync(cts.Token);
        Assert.True(await Ended(run), "an unmeasurable tier was treated as a still-valid one");
        await run;
    }

    /// <summary>
    /// The re-check runs every tick and says nothing while the answer holds. A patch per tick would
    /// make a panel redraw itself three times a minute to report that nothing happened.
    /// </summary>
    [Fact]
    public async Task RunAsync_pushes_nothing_while_the_tier_holds()
    {
        using var cts = new CancellationTokenSource();
        var body = new MemoryStream();
        var checks = 0;
        StreamConnection conn = NewGoverned(body, ["me"], KgsmTier.Viewer, _ =>
        {
            Interlocked.Increment(ref checks);
            return new ValueTask<AccountStanding>(new AccountStanding("usr_alice", KgsmTier.Viewer, "active"));
        });

        Task run = conn.RunAsync(cts.Token);

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref checks) < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(Volatile.Read(ref checks) >= 3, $"expected repeated re-checks, saw {Volatile.Read(ref checks)}");
        Assert.DoesNotContain("me.patch", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);

        cts.Cancel();
        await run;
    }

    /// <summary>An enqueued frame is written to the body as a <c>data:</c> SSE frame, then the loop
    /// still tears down cleanly on cancellation (the happy path around the regression above).</summary>
    [Fact]
    public async Task RunAsync_writes_an_enqueued_frame_then_stops_on_cancel()
    {
        using var cts = new CancellationTokenSource();
        var body = new MemoryStream();
        StreamConnection conn = NewConnection(body);

        Task run = conn.RunAsync(cts.Token);
        await Task.Delay(100);

        conn.Enqueue("servers", Encoding.UTF8.GetBytes("data: {\"topic\":\"servers\"}\n\n"));
        await Task.Delay(150);

        cts.Cancel();
        Task winner = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == run, "RunAsync did not stop after cancellation");
        await run;

        string written = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains(": connected", written);
        Assert.Contains("data: {\"topic\":\"servers\"}", written);
    }
}
