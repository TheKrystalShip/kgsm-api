using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Realtime;

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
