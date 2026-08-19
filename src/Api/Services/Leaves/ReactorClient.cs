using System.Net.Sockets;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The kgsm-reactor leaf client: the API's read seam onto the reactor's status socket.
/// </summary>
/// <remarks>
/// <para>
/// The reactor serves HTTP over a unix-domain socket (Kestrel, server-to-client only — nothing off this
/// host has any business asking a leaf what it is thinking), so the transport is the same
/// <see cref="SocketsHttpHandler.ConnectCallback"/> pattern the monitor client uses. Two paths matter:
/// <c>/health</c>, which answers "the process is up and serving", and <c>/status</c>, which is the richer
/// self-report — the live rules, the counters since start, and the evaluations waiting out their settle
/// windows.
/// </para>
/// <para>
/// <b>The two are deliberately different questions on the leaf's side</b>, and the capability probe asks
/// the cheap one: a reactor that is up while unable to read its ledger must still be able to say so on
/// <c>/status</c> rather than failing <c>/health</c> and looking dead.
/// </para>
/// <para>
/// Honesty: an unreachable, slow or non-2xx answer is <c>false</c>/<c>null</c> — the caller reports the
/// capability down, never a fabricated reading. The transport is always built (from the configured-or-
/// default socket) so a runtime connect arms probing without a restart; the call-time registry gate is
/// what decides whether to dial.
/// </para>
/// </remarks>
public sealed class ReactorClient : IDisposable
{
    // Where the reactor's status socket lives on a standard install — used to build the transport when no
    // explicit path is configured, so a runtime "connect reactor" works against the standard socket.
    private const string DefaultSocketPath = "/run/kgsm-reactor/status.sock";

    // The reactor composes its answer from what its two hosted services already hold and reads nothing off
    // disk, so a slow reply means a sick daemon. Bound it so one can never stall the health poll.
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(2);

    private readonly LeafRegistry _registry;
    private readonly ILogger<ReactorClient> _logger;
    private readonly HttpClient _http;

    public ReactorClient(ApiOptions options, LeafRegistry registry, ILogger<ReactorClient> logger)
    {
        _registry = registry;
        _logger = logger;

        string socketPath = string.IsNullOrWhiteSpace(options.ReactorSocketPath)
            ? DefaultSocketPath
            : options.ReactorSocketPath;

        var handler = new SocketsHttpHandler
        {
            // Every connection is dialed over the unix-domain socket; the request URI host is a
            // placeholder the reactor ignores.
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
            Timeout = AnswerWithin,
        };
    }

    /// <summary>
    /// Whether this host holds a link to a reactor at all.
    /// </summary>
    /// <remarks>
    /// The distinction the two read methods cannot make on their own: both answer <c>null</c>/<c>false</c>
    /// for a leaf that is absent and for one that would not speak, and a surface has to tell those apart —
    /// the first is a host that runs no reactor, the second is a reactor that needs looking at.
    /// </remarks>
    public bool IsProvisioned => _registry.IsProvisioned(ProvisionableLeaf.Reactor);

    /// <summary>
    /// Liveness probe for the reactor capability: <c>GET /health</c> over its status socket. A 2xx means the
    /// daemon is up and serving. Returns <c>false</c> when disconnected at runtime, unreachable, slow or
    /// non-2xx — never throws.
    /// </summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Reactor))
            return false; // disconnected at runtime: the capability is absent, not down.

        try
        {
            using HttpResponseMessage resp = await _http.GetAsync("/health", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("reactor /health probe timed out after {Timeout}", AnswerWithin);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "reactor /health probe failed");
            return false;
        }
    }

    /// <summary>
    /// The reactor's own self-report (<c>GET /status</c>), verbatim. Returns <c>null</c> on the same terms
    /// as the probe — a caller must not read that as a reactor with nothing to say, which is what an empty
    /// rule list means.
    /// </summary>
    public async Task<string?> GetStatusJsonAsync(CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Reactor))
            return null; // disconnected at runtime: honest absent, no request.

        try
        {
            using HttpResponseMessage resp = await _http.GetAsync("/status", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("reactor /status returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("reactor /status timed out after {Timeout}", AnswerWithin);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "reactor /status failed");
            return null;
        }
    }

    /// <summary>
    /// The reactor's decision review (<c>GET /decisions</c>), verbatim. Null on the same terms as the
    /// status read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The review the plan gates propose and act mode behind — what each rule concluded, the busiest
    /// hour a ceiling would have to clear, how far apart a rule's repeats were, and the rules that
    /// decided nothing. The leaf computes all of it from its own ledger; this relays it.
    /// </para>
    /// <para>
    /// ⚠ <b>The window is the leaf's to bound.</b> It clamps <c>days</c> to its own retention, because
    /// only it knows how far its ledger goes back. Re-clamping here against a figure this API guessed
    /// would refuse windows the leaf can in fact answer.
    /// </para>
    /// </remarks>
    public async Task<string?> GetDecisionsJsonAsync(int days, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Reactor))
            return null; // disconnected at runtime: honest absent, no request.

        try
        {
            // Omitted rather than sent as zero when the caller named no window: the leaf clamps what
            // it receives to at least one day, so forwarding a 0 would ask for a single day where the
            // caller meant "whatever you default to" — which is the week the review gate is stated over.
            string path = days > 0 ? $"/decisions?days={days}" : "/decisions";

            using HttpResponseMessage resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("reactor /decisions returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("reactor /decisions timed out after {Timeout}", AnswerWithin);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "reactor /decisions failed");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
