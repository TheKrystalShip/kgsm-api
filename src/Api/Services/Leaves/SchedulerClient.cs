using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The kgsm-scheduler leaf client (Settings Phase 3). Unlike the monitor/assistant, the scheduler speaks
/// <strong>NDJSON-over-unix-socket</strong>, not HTTP: on connect it writes exactly one JSON line — the
/// status snapshot (per-instance <c>nextFireUtc</c> + last-run) — then closes. This client dials that socket,
/// reads the single line, and parses it. It is registered ONLY when the socket is configured
/// (<c>KGSM_API_SCHEDULER_SOCKET</c>); consumers resolve it optionally and degrade to <c>absent</c>/null when
/// it is missing.
/// </summary>
/// <remarks>
/// Honesty: an unreachable/timed-out/malformed snapshot yields <c>null</c> — the caller then reports the
/// scheduler capability down and nulls <c>nextFireUtc</c> (never a fabricated schedule). kgsm-api is JIT, so
/// plain reflection-based <see cref="JsonSerializer"/> (camelCase) is fine here — no source-gen needed.
/// </remarks>
public sealed class SchedulerClient
{
    // A snapshot read must be fast (the scheduler writes one line and closes); bound it so a hung socket can
    // never stall a /settings request or the leaf-health poll.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _socketPath;
    private readonly ILogger<SchedulerClient> _logger;

    public SchedulerClient(ApiOptions options, ILogger<SchedulerClient> logger)
    {
        _socketPath = options.SchedulerSocketPath;
        _logger = logger;
    }

    /// <summary>
    /// Connects to the scheduler socket, reads the one-line status snapshot, and returns it — or <c>null</c>
    /// when the socket is unreachable, slow, or the line is empty/malformed (honest unknown, never fabricated).
    /// </summary>
    public async Task<SchedulerStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ReadTimeout);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), timed.Token).ConfigureAwait(false);

            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line = await reader.ReadLineAsync(timed.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                return null;

            return JsonSerializer.Deserialize<SchedulerStatusResponse>(line, Json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("scheduler status read timed out after {Timeout} at {Path}", ReadTimeout, _socketPath);
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException)
        {
            _logger.LogDebug(ex, "scheduler socket unreachable/unreadable at {Path}", _socketPath);
            return null;
        }
    }

    /// <summary>Liveness probe for the §4·b scheduler capability: can connect + parse a snapshot ⇒ healthy.
    /// Returns <c>false</c> on any failure — never throws.</summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct = default) =>
        await GetStatusAsync(ct).ConfigureAwait(false) is not null;
}

/// <summary>The scheduler's one-line status snapshot: the per-instance schedule state it supervises.</summary>
public sealed record SchedulerStatusResponse(IReadOnlyList<SchedulerInstanceStatus> Instances);

/// <summary>One instance's scheduler state — the configured cadence (echoed from kgsm config) plus the
/// computed <see cref="NextFireUtc"/> and the last-run outcome. All nullable — honest unknown, never guessed.</summary>
public sealed record SchedulerInstanceStatus(
    string Name,
    string? ScheduledRestart,
    string? RestartTime,
    string? RestartDay,
    string? Timezone,
    DateTimeOffset? NextFireUtc,
    DateTimeOffset? LastRunUtc,
    bool? LastRunOk,
    string? LastRunMessage,
    // Phase 4 — auto-backup last-run outcome (all nullable: null when the scheduler hasn't run a backup yet
    // or predates the feature). Honest unknown, never guessed.
    DateTimeOffset? LastBackupUtc = null,
    bool? LastBackupOk = null,
    string? LastBackupMessage = null);
