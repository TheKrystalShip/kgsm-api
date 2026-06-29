using System.Diagnostics;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Logs;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The live host-log follow bridge — the resident piece behind the follow-only, operator-gated
/// <c>hosts/{id}/logs</c> WS topic (the live-tail companion to the REST <c>GET /hosts/{id}/logs</c>).
/// An always-running reconcile loop (the <see cref="ConsoleBridgeManager"/> shape): while the host-logs
/// topic has subscribers it runs <strong>exactly one</strong> shared <c>journalctl -f --output=json -n 0
/// -u …</c> across all configured leaf units and fans every new line out to that topic's subscribers as a
/// <see cref="StreamProtocol.LogLine"/> message; when the last subscriber leaves (or on shutdown) it kills
/// that process, so an idle topic costs nothing.
/// </summary>
/// <remarks>
/// <para><b>Why one shared follow.</b> Twenty operators watching the host log must not spawn twenty
/// <c>journalctl -f</c> processes — the hub fans the single line stream out to every subscriber (each frame
/// serialized once, <see cref="StreamHub.Publish"/>).</para>
/// <para><b><c>-n 0</c> = follow-only.</b> No history backlog on attach — the client hydrated scrollback via
/// REST and applies live lines from the next one on (the patch-only, no-snapshot-on-subscribe rule).</para>
/// <para><b>Per-line unique coalesce key.</b> The line's journald cursor (<see cref="StreamProtocol.HostLogEntityKey"/>) —
/// distinct lines never collapse into the latest under a slow client (the audit/console precedent), they just
/// drop best-effort. The durable record is the journal; the client re-hydrates via REST on reconnect.</para>
/// <para><b>Honesty / degrade.</b> The line shape is the same <see cref="LogLine"/> the REST page emits — never
/// fabricated. If <c>journalctl</c> is missing or unreadable the follow ends; we log once and retry only on a
/// later tick while still subscribed (no spin). The merged source-tagging reuses <see cref="JournalReader.ParseLine"/>,
/// so the WS and REST source/level mapping can't drift.</para>
/// </remarks>
public sealed class JournalFollowBridge : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);

    private readonly ApiOptions _options;
    private readonly JournalReader _reader;
    private readonly ILogger<JournalFollowBridge> _logger;

    // Test seams (defaulting to the real hub) — the wire frame doesn't expose the per-line coalesce key.
    private readonly Action<string, string, StreamMessage> _publish;
    private readonly Func<string, bool> _hasSubscribers;

    private readonly string _topic;
    private Follow? _follow; // the single open journalctl -f, or null when idle. Touched only on the loop thread.

    public JournalFollowBridge(ApiOptions options, JournalReader reader, StreamHub hub, ILogger<JournalFollowBridge> logger)
    {
        _options = options;
        _reader = reader;
        _logger = logger;
        _publish = hub.Publish;
        _hasSubscribers = hub.HasSubscribers;
        _topic = StreamProtocol.HostLogsTopic(options.HostId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(ReconcileInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                bool subscribed = _hasSubscribers(_topic);
                bool running = _follow is { Done.IsCompleted: false };

                if (subscribed && !running)
                    StartFollow(stoppingToken);
                else if (!subscribed && _follow is not null)
                    StopFollow();
                else if (_follow is { Done.IsCompleted: true })
                    // the follow ended on its own (journalctl exited / errored) — clear it so a still-subscribed
                    // topic reopens next tick (bounded retry: at most one attempt per interval).
                    StopFollow();
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
        finally
        {
            StopFollow();
        }
    }

    private void StartFollow(CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var follow = new Follow(cts);
        _follow = follow;
        follow.Done = RunFollowAsync(cts.Token);
    }

    private void StopFollow()
    {
        Follow? f = _follow;
        _follow = null;
        f?.Stop();
    }

    /// <summary>
    /// The shared follow loop: <c>journalctl -f --output=json -n 0 -u …</c>, each line parsed (merged-mode
    /// source tagging) and published to the host-logs topic with its unique cursor as the coalesce key. Ends
    /// only when the token is cancelled (the normal close) or journalctl exits/fails.
    /// </summary>
    private async Task RunFollowAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(_reader.JournalctlPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--output=json");
            psi.ArgumentList.Add("--no-pager");
            psi.ArgumentList.Add("--follow");
            psi.ArgumentList.Add("--lines=0"); // follow-only: no history backlog on attach (REST hydrates that)
            foreach (string unit in _reader.Units) { psi.ArgumentList.Add("--unit"); psi.ArgumentList.Add(unit); }

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
            {
                _logger.LogWarning("host-log follow: journalctl failed to start ({Path})", _reader.JournalctlPath);
                return;
            }

            try
            {
                string? raw;
                while ((raw = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    if (raw.Length == 0) continue;
                    LogLine? line = _reader.ParseLine(raw);
                    if (line is null) continue;

                    _publish(_topic, StreamProtocol.HostLogEntityKey(line.Id),
                        new StreamMessage(_topic, StreamProtocol.LogLine, line));
                }
            }
            finally
            {
                try { proc.StandardOutput.Close(); } catch { /* already gone */ }
                if (!proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* race: exited */ }
                }
            }
        }
        catch (OperationCanceledException) { /* the normal close: our token fired */ }
        catch (Exception ex)
        {
            // missing binary / no journal access — log once-ish; the reconcile loop only retries on a later
            // tick while still subscribed (no tight respawn loop).
            _logger.LogWarning(ex, "host-log follow ended unexpectedly ({Path})", _reader.JournalctlPath);
        }
    }

    // One open journalctl -f: the CTS that ends it + the running Task. Stop() cancels then disposes.
    private sealed class Follow(CancellationTokenSource cts)
    {
        public Task Done { get; set; } = Task.CompletedTask;
        public void Stop()
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }
}
