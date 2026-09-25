using System.Net.Sockets;
using System.Text.Json;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The kgsm-scheduler leaf client: the API's seam onto the scheduler's socket.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler serves HTTP over a unix-domain socket (Kestrel, nothing off this host has any business
/// asking a leaf what it is scheduled to do), so the transport is the same
/// <see cref="SocketsHttpHandler.ConnectCallback"/> pattern the monitor and reactor clients use.
/// <c>GET /status</c> is the snapshot; the three verbs are <c>POST</c>s under <c>/windows/</c>, so the
/// method already says whether something is being read or changed and one socket carries both.
/// </para>
/// <para>
/// <b>A refusal is an answer.</b> An instruction naming a window the host does not have comes back 200
/// carrying <c>ok:false</c> and the scheduler's own reason, so a non-2xx keeps its single meaning of
/// "the scheduler could not read this". The caller is about to tell a person what happened to their
/// evening, and "it said no" has to stay distinguishable from "it could not be reached".
/// </para>
/// <para>
/// Honesty: an unreachable, slow or malformed snapshot yields <c>null</c> — the caller then reports the
/// scheduler capability down and nulls every window's next fire and last run, never a fabricated
/// schedule. kgsm-api is JIT, so plain reflection-based <see cref="JsonSerializer"/> (camelCase) is fine
/// here — no source-gen needed.
/// </para>
/// </remarks>
public sealed class SchedulerClient
{
    // A snapshot read must be fast (the scheduler answers off an in-memory registry); bound it so a hung
    // socket can never stall a /settings request or the leaf-health poll.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _socketPath;
    private readonly ILogger<SchedulerClient> _logger;
    private readonly HttpClient _http;

    public SchedulerClient(ApiOptions options, ILogger<SchedulerClient> logger)
    {
        _socketPath = options.SchedulerSocketPath;
        _logger = logger;

        string socketPath = _socketPath;

        var handler = new SocketsHttpHandler
        {
            // Every connection is dialed over the unix-domain socket; the request URI host is a
            // placeholder the scheduler ignores.
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://localhost"),
            // Bounded per call rather than on the client, so the budget that makes a slow reply mean a
            // sick daemon is applied where it belongs.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Asks the scheduler for its status snapshot — or <c>null</c> when the socket is unreachable, slow,
    /// or the answer is empty/malformed (honest unknown, never fabricated).
    /// </summary>
    public async Task<SchedulerStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        if (!IsWired)
            return null;

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ReadTimeout);
        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync("/status", timed.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("scheduler answered {Status} for /status at {Path}",
                    (int)response.StatusCode, _socketPath);
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<SchedulerStatusResponse>(Json, timed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("scheduler status read timed out after {Timeout} at {Path}", ReadTimeout, _socketPath);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or JsonException)
        {
            _logger.LogDebug(ex, "scheduler socket unreachable/unreadable at {Path}", _socketPath);
            return null;
        }
    }

    /// <summary>Liveness probe for the §4·b scheduler capability: can connect + parse a snapshot ⇒ healthy.
    /// Returns <c>false</c> on any failure — never throws.</summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct = default) =>
        await GetStatusAsync(ct).ConfigureAwait(false) is not null;

    /// <summary>Whether this host is wired to a scheduler at all.</summary>
    private bool IsWired => !string.IsNullOrWhiteSpace(_socketPath);

    /// <summary>Whether this host can send the scheduler an instruction.</summary>
    /// <remarks>
    /// One socket carries both the reading and the telling, so a host that can ask the scheduler
    /// anything can also tell it something. A surface still asks this rather than assuming it: what it
    /// renders is a button, and a host running no scheduler must not show one.
    /// </remarks>
    public bool CanControl => IsWired;

    /// <summary>
    /// Push one window's next run back by <paramref name="minutes"/>. The schedule is untouched, so the fire
    /// after this one lands where it always would have.
    /// </summary>
    public Task<SchedulerControlResponse> PostponeAsync(
        string instance, string window, int minutes, CancellationToken ct = default) =>
        SendAsync(SchedulerVerb.Postpone, new SchedulerControlRequest(instance, window, minutes), ct);

    /// <summary>Drop this occurrence of one window. The one after it is unaffected.</summary>
    public Task<SchedulerControlResponse> SkipAsync(
        string instance, string window, CancellationToken ct = default) =>
        SendAsync(SchedulerVerb.Skip, new SchedulerControlRequest(instance, window), ct);

    /// <summary>Bring one window forward to the scheduler's next poll — the same run a due one would get.</summary>
    public Task<SchedulerControlResponse> RunNowAsync(
        string instance, string window, CancellationToken ct = default) =>
        SendAsync(SchedulerVerb.RunNow, new SchedulerControlRequest(instance, window), ct);

    /// <summary>
    /// One instruction to the scheduler: the verb is the route, the body is what it acts on.
    /// </summary>
    /// <remarks>
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
    private async Task<SchedulerControlResponse> SendAsync(
        string verb, SchedulerControlRequest request, CancellationToken ct)
    {
        if (!CanControl)
            return new SchedulerControlResponse(false, "this host is not wired to a scheduler");

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ReadTimeout);
        try
        {
            using HttpResponseMessage response = await _http
                .PostAsJsonAsync("/windows/" + verb, request, Json, timed.Token).ConfigureAwait(false);

            // A non-2xx means the scheduler could not read the instruction, which is this API's fault
            // rather than the caller's — and never a refusal, which arrives 200 carrying its own reason.
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("scheduler answered {Status} for /windows/{Verb} at {Path}",
                    (int)response.StatusCode, verb, _socketPath);
                return new SchedulerControlResponse(false, "the scheduler could not read the instruction");
            }

            return await response.Content
                       .ReadFromJsonAsync<SchedulerControlResponse>(Json, timed.Token).ConfigureAwait(false)
                   ?? new SchedulerControlResponse(false, "the scheduler's answer could not be read");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("scheduler {Verb} timed out after {Timeout} at {Path}",
                verb, ReadTimeout, _socketPath);
            return new SchedulerControlResponse(false, "the scheduler did not answer in time");
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or JsonException)
        {
            _logger.LogDebug(ex, "scheduler socket unreachable at {Path}", _socketPath);
            return new SchedulerControlResponse(false, "the scheduler could not be reached");
        }
    }
}

/// <summary>
/// The verbs the scheduler takes, spelled the way its routes are: each constant is the last segment of
/// the <c>POST /windows/&lt;verb&gt;</c> the daemon serves, and the same word a browser asks for.
/// </summary>
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
/// What one instruction acts on. <see cref="Window"/> is the window's schedule expression — its id — and
/// <see cref="Minutes"/> is carried only by <see cref="SchedulerVerb.Postpone"/>. The verb itself is the
/// route, so it is not spelled again in here.
/// </summary>
public sealed record SchedulerControlRequest(string Instance, string Window, int? Minutes = null);

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
/// <param name="LastUpdateCheckUtc">When the update sweep last <em>attempted</em> this instance. Not when
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
