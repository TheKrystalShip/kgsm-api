using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The kgsm-scheduler leaf client. Unlike the monitor/assistant, the scheduler speaks
/// <strong>NDJSON-over-unix-socket</strong>, not HTTP: on connect to the status socket it writes exactly one
/// JSON line — the per-instance maintenance-window snapshot — then closes. This client dials that socket,
/// reads the single line, and parses it. It is registered ONLY when the socket is configured
/// (<c>Api__SchedulerSocketPath</c>); consumers resolve it optionally and degrade to <c>absent</c>/null when
/// it is missing.
/// </summary>
/// <remarks>
/// Honesty: an unreachable/timed-out/malformed snapshot yields <c>null</c> — the caller then reports the
/// scheduler capability down and nulls every window's next fire and last run (never a fabricated schedule).
/// kgsm-api is JIT, so plain reflection-based <see cref="JsonSerializer"/> (camelCase) is fine here — no
/// source-gen needed.
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
    private readonly string _controlSocketPath;
    private readonly ILogger<SchedulerClient> _logger;

    public SchedulerClient(ApiOptions options, ILogger<SchedulerClient> logger)
    {
        _socketPath = options.SchedulerSocketPath;
        _controlSocketPath = options.SchedulerControlSocketPath;
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

    /// <summary>Whether this host is wired to send the scheduler an instruction at all.</summary>
    public bool CanControl => !string.IsNullOrWhiteSpace(_controlSocketPath);

    /// <summary>
    /// Push one window's next run back by <paramref name="minutes"/>. The schedule is untouched, so the fire
    /// after this one lands where it always would have.
    /// </summary>
    public Task<SchedulerControlResponse> PostponeAsync(
        string instance, string window, int minutes, CancellationToken ct = default) =>
        SendAsync(new SchedulerControlRequest(SchedulerVerb.Postpone, instance, window, minutes), ct);

    /// <summary>Drop this occurrence of one window. The one after it is unaffected.</summary>
    public Task<SchedulerControlResponse> SkipAsync(
        string instance, string window, CancellationToken ct = default) =>
        SendAsync(new SchedulerControlRequest(SchedulerVerb.Skip, instance, window), ct);

    /// <summary>Bring one window forward to the scheduler's next poll — the same run a due one would get.</summary>
    public Task<SchedulerControlResponse> RunNowAsync(
        string instance, string window, CancellationToken ct = default) =>
        SendAsync(new SchedulerControlRequest(SchedulerVerb.RunNow, instance, window), ct);

    /// <summary>
    /// One instruction to the scheduler: write a line, read the reply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A different socket from the status one, and deliberately so on the daemon's side: that one's
    /// contract is that a client only ever reads. This writes one NDJSON line and reads one back.
    /// </para>
    /// <para>
    /// <b>Every verb names its window.</b> One instance holds several appointments, and moving the wrong
    /// one is worse than refusing — the daemon refuses an instruction that names none.
    /// </para>
    /// <para>
    /// <b>Every failure is reported, never swallowed into a success.</b> The caller is about to tell a
    /// person what happened to their evening, so "we could not reach the scheduler" has to be
    /// distinguishable from "it said no" and from "it is deferred".
    /// </para>
    /// </remarks>
    private async Task<SchedulerControlResponse> SendAsync(SchedulerControlRequest request, CancellationToken ct)
    {
        if (!CanControl)
            return new SchedulerControlResponse(false, "this host is not wired to the scheduler's control socket");

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ReadTimeout);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_controlSocketPath), timed.Token)
                .ConfigureAwait(false);

            await using var stream = new NetworkStream(socket, ownsSocket: false);
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json) + "\n");
            await stream.WriteAsync(line, timed.Token).ConfigureAwait(false);
            await stream.FlushAsync(timed.Token).ConfigureAwait(false);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? reply = await reader.ReadLineAsync(timed.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reply))
                return new SchedulerControlResponse(false, "the scheduler answered nothing");

            return JsonSerializer.Deserialize<SchedulerControlResponse>(reply, Json)
                ?? new SchedulerControlResponse(false, "the scheduler's answer could not be read");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("scheduler control write timed out after {Timeout} at {Path}",
                ReadTimeout, _controlSocketPath);
            return new SchedulerControlResponse(false, "the scheduler did not answer in time");
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException)
        {
            _logger.LogDebug(ex, "scheduler control socket unreachable at {Path}", _controlSocketPath);
            return new SchedulerControlResponse(false, "the scheduler could not be reached");
        }
    }
}

/// <summary>The control socket's verbs, spelled the way the daemon reads them.</summary>
public static class SchedulerVerb
{
    /// <summary>Push a window's next run back. Takes <c>minutes</c>, which the daemon caps at 720.</summary>
    public const string Postpone = "postpone";

    /// <summary>Drop this occurrence of a window.</summary>
    public const string Skip = "skip";

    /// <summary>Bring a window forward to the next poll.</summary>
    public const string RunNow = "run-now";

    /// <summary>Whether <paramref name="verb"/> is one the scheduler takes.</summary>
    public static bool IsKnown(string? verb) => verb is Postpone or Skip or RunNow;
}

/// <summary>
/// One instruction to the scheduler, as its control socket takes it. <see cref="Window"/> is the window's
/// schedule expression — its id — and <see cref="Minutes"/> is carried only by <see cref="SchedulerVerb.Postpone"/>.
/// </summary>
public sealed record SchedulerControlRequest(string Command, string Instance, string Window, int? Minutes = null);

/// <summary>What the scheduler said. <see cref="NextFireUtc"/> is the window's target as it now stands.</summary>
public sealed record SchedulerControlResponse(bool Ok, string Message, DateTimeOffset? NextFireUtc = null);

/// <summary>The scheduler's one-line status snapshot: the maintenance state of every instance it reads.</summary>
public sealed record SchedulerStatusResponse(IReadOnlyList<SchedulerInstanceStatus>? Instances);

/// <summary>
/// One instance's maintenance state — its windows, plus the update sweep's own three fields.
/// </summary>
/// <param name="Name">The kgsm instance id.</param>
/// <param name="Timezone">The instance's IANA timezone, as kgsm holds it. Blank when it declares none.</param>
/// <param name="Windows">Every window written on the instance, valid or not.</param>
/// <param name="LastUpdateCheckUtc">When the update sweep last <em>attempted</em> this instance. ⚠ Not when
/// the upstream was last fetched: a server skipped as recently-checked is null here while the engine holds a
/// real check time for it. These three answer "is the sweep working, and what failed".</param>
/// <param name="LastUpdateCheckOk">Whether that attempt succeeded.</param>
/// <param name="LastUpdateCheckMessage">What went wrong, when something did.</param>
public sealed record SchedulerInstanceStatus(
    string Name,
    string? Timezone,
    IReadOnlyList<SchedulerWindowStatus>? Windows,
    DateTimeOffset? LastUpdateCheckUtc = null,
    bool? LastUpdateCheckOk = null,
    string? LastUpdateCheckMessage = null);

/// <summary>
/// One maintenance window as the daemon holds it.
/// </summary>
/// <param name="Id">The window's schedule expression, which is its identity (<c>weekly.sun@04:00</c>).</param>
/// <param name="Kind"><c>appointment</c> or <c>interval</c>.</param>
/// <param name="Tasks">The tasks it runs, in canonical order.</param>
/// <param name="Valid">Whether this host will fire it.</param>
/// <param name="Error">Why it will not, when it will not — naming the offending text.</param>
/// <param name="NextFireUtc">The next fire. Null on an invalid window: the pair is what tells an
/// unreadable window apart from one that is simply not due.</param>
/// <param name="LastRun">The last run since the daemon started, or null when it has not run in that time
/// (the record lives in the daemon's memory, not on disk).</param>
public sealed record SchedulerWindowStatus(
    string Id,
    string? Kind,
    IReadOnlyList<string>? Tasks,
    bool Valid,
    string? Error,
    DateTimeOffset? NextFireUtc,
    SchedulerWindowRun? LastRun);

/// <summary>
/// One window run: when it started and finished, how it ended, and a row per task it got to.
/// </summary>
/// <param name="Outcome">The window's own outcome, in the <see cref="MaintenanceOutcome"/> vocabulary.</param>
public sealed record SchedulerWindowRun(
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc,
    string? Outcome,
    IReadOnlyList<SchedulerTaskRun>? Tasks);

/// <summary>One task inside a run — what it was, how it ended, and the daemon's words for why.</summary>
public sealed record SchedulerTaskRun(string Name, string? Outcome, string? Message);

/// <summary>
/// How a window or one of its tasks ended. Four words rather than a boolean, because "did the maintenance
/// work" has four genuinely different answers, and collapsing them loses the one a surface should raise.
/// </summary>
public static class MaintenanceOutcome
{
    /// <summary>It was owed and it happened.</summary>
    public const string Ok = "ok";

    /// <summary>It was owed and it did not happen. The one a surface raises.</summary>
    public const string Failed = "failed";

    /// <summary>It did not apply to the instance as it stood — recorded with its reason, never raised.</summary>
    public const string Skipped = "skipped";

    /// <summary>An earlier task in the same window failed, so this one never got its turn.</summary>
    public const string Aborted = "aborted";
}
