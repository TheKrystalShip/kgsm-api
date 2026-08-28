using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>Who is answering a proposal, in the shape the leaf takes it.</summary>
/// <remarks>
/// <b>Built here from the authenticated principal, never bound from the request.</b> A caller-supplied
/// name would let anybody sign anybody else's confirmation, and the leaf has no way to tell the two
/// apart — it checks the shape and trusts whoever authenticated the person.
/// </remarks>
internal sealed record ReactorRedemptionBody(string By)
{
    [System.Text.Json.Serialization.JsonPropertyName("by")]
    public string By { get; init; } = By;
}

/// <summary>The one serializer setting this relay needs, so the leaf reads what it expects.</summary>
internal static class ReactorRelayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

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

    // A preview is the one call here that does real work: it reads the supervisor and the monitor once
    // per subject, across as much of the fleet as the rule enumerates. Holding it to the probe's budget
    // would report a healthy leaf as unreachable to somebody who is only composing a rule.
    private static readonly TimeSpan PreviewWithin = TimeSpan.FromSeconds(30);

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
            // Bounded per call rather than on the client: the probe's budget is what makes a slow reply
            // mean a sick daemon, and applying it to every call would cap the one that legitimately does
            // work. Each method links its own deadline below.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// A token that cancels when the caller does, or when this call has taken long enough.
    /// </summary>
    /// <remarks>
    /// The two are told apart at the catch site by asking whether the caller's token fired, which is what
    /// lets a timeout be logged as one rather than as a request somebody abandoned.
    /// </remarks>
    private static CancellationTokenSource Bounded(CancellationToken ct, TimeSpan budget)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(budget);
        return linked;
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
            using CancellationTokenSource bound = Bounded(ct, AnswerWithin);
            using HttpResponseMessage resp = await _http.GetAsync("/health", bound.Token).ConfigureAwait(false);
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
            using CancellationTokenSource bound = Bounded(ct, AnswerWithin);
            using HttpResponseMessage resp = await _http.GetAsync("/status", bound.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("reactor /status returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
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
    /// What a rule may be made of on the running build (<c>GET /catalog</c>), verbatim. Null on the same
    /// terms as the status read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signals, subject sources, actions, operators and outcomes the panel renders its rule editor
    /// from. Relayed rather than reshaped, and never cached against a build: the leaf is the only thing
    /// that knows what it can measure, and a copy held here would go on offering a signal after the
    /// build that measures it was replaced.
    /// </para>
    /// <para>
    /// <b>Read-only, and the boundary is deliberate.</b> Publishing what a rule may be made of is not
    /// the same as the leaf accepting one over a socket. Composing and storing a rule is this API's
    /// half — it writes the file and restarts the unit through the grant it already holds — so nothing
    /// off the host acquires the ability to tell a leaf what to think.
    /// </para>
    /// </remarks>
    public async Task<string?> GetCatalogJsonAsync(CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Reactor))
            return null; // disconnected at runtime: honest absent, no request.

        try
        {
            using CancellationTokenSource bound = Bounded(ct, AnswerWithin);
            using HttpResponseMessage resp = await _http.GetAsync("/catalog", bound.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("reactor /catalog returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("reactor /catalog timed out after {Timeout}", AnswerWithin);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "reactor /catalog failed");
            return null;
        }
    }

    /// <summary>
    /// The events a rule may wake on (<c>GET /triggers</c>), verbatim. Null on the same terms as the
    /// status read.
    /// </summary>
    /// <remarks>
    /// Read off what the leaf's ledger has actually observed, with each type's producer and how often
    /// it fires — a query over this host's own history rather than a property of the build, which is
    /// why it is a separate call from the catalog. Relayed rather than reshaped.
    /// </remarks>
    public async Task<string?> GetTriggersJsonAsync(int days, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Reactor))
            return null; // disconnected at runtime: honest absent, no request.

        try
        {
            // Omitted rather than sent as zero when the caller named no window, for the same reason the
            // decision review omits it: the leaf owns both the default and the ceiling on it.
            string path = days > 0 ? $"/triggers?days={days}" : "/triggers";

            using CancellationTokenSource bound = Bounded(ct, AnswerWithin);
            using HttpResponseMessage resp = await _http.GetAsync(path, bound.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("reactor /triggers returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("reactor /triggers timed out after {Timeout}", AnswerWithin);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "reactor /triggers failed");
            return null;
        }
    }

    /// <summary>
    /// What a proposed rule would decide about this host right now (<c>POST /preview</c>), verbatim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A read expressed as a POST, because the rule is the question.</b> The leaf stores nothing,
    /// dispatches nothing, and writes no decision — so this does not cross the boundary the read-only
    /// socket draws.
    /// </para>
    /// <para>
    /// The status is returned beside the body because a rule the leaf could not read is a caller error
    /// that has to reach the person composing, where every other failure here is the leaf being absent.
    /// Collapsing the two would report "the reactor didn't answer" for a misplaced comma.
    /// </para>
    /// </remarks>
    /// <returns>The body and the leaf's status code, or <c>(null, 0)</c> when it could not be reached.</returns>
    public async Task<(string? Body, int Status)> PreviewJsonAsync(string rule, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Reactor))
            return (null, 0); // disconnected at runtime: honest absent, no request.

        try
        {
            using var content = new StringContent(rule, System.Text.Encoding.UTF8, "application/json");
            using CancellationTokenSource bound = Bounded(ct, PreviewWithin);
            using HttpResponseMessage resp =
                await _http.PostAsync("/preview", content, bound.Token).ConfigureAwait(false);

            string body = await resp.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
                return (body, (int)resp.StatusCode);

            if (resp.StatusCode == HttpStatusCode.BadRequest)
                return (body, StatusCodes.Status400BadRequest);

            _logger.LogDebug("reactor /preview returned {Status}", (int)resp.StatusCode);
            return (null, (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("reactor /preview timed out after {Timeout}", AnswerWithin);
            return (null, 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "reactor /preview failed");
            return (null, 0);
        }
    }

    /// <summary>
    /// What this host is offering, and what recently became of its offers
    /// (<c>GET /proposals</c>), verbatim.
    /// </summary>
    /// <remarks>
    /// <b>The body carries redemption handles, so this is operator-gated on the way out.</b> A handle
    /// is the capability: anything holding one can ask for the action it names. The leaf will not act on
    /// one without a named caller and the API will not hand one to a caller it has not authorised, and
    /// both halves are needed — the leaf cannot know who anybody is, and the API cannot re-derive the
    /// condition.
    /// </remarks>
    public async Task<string?> GetProposalsJsonAsync(int days, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Reactor))
            return null; // disconnected at runtime: honest absent, no request.

        try
        {
            string path = days > 0 ? $"/proposals?days={days}" : "/proposals";

            using CancellationTokenSource bound = Bounded(ct, AnswerWithin);
            using HttpResponseMessage resp = await _http.GetAsync(path, bound.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("reactor /proposals returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("reactor /proposals timed out after {Timeout}", AnswerWithin);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "reactor /proposals failed");
            return null;
        }
    }

    /// <summary>
    /// Redeems a proposal handle — confirm or dismiss — as <paramref name="by"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one call here that changes the host, and the identity it carries is not the caller's to
    /// choose.</b> <paramref name="by"/> comes from the authenticated principal, never from the request
    /// body: an action a person authorised has to name the person who actually authorised it, and a
    /// caller-supplied name would let anybody sign anybody else's confirmation.
    /// </para>
    /// <para>
    /// Confirming can take as long as the action does — a backup of a large world is minutes — so it
    /// gets the preview budget rather than the two seconds a question gets. Timing out here does not
    /// mean nothing happened: the leaf claims the proposal before it performs, so the offer is spent
    /// and re-reading <c>/proposals</c> is what says how it went.
    /// </para>
    /// <para>
    /// The status travels with the body because the leaf's codes are meaningful and distinct — 404 for
    /// a handle nothing carries, 409 for one already answered or expired, 503 for a world that would not
    /// answer, and 200 for every proper ending including "no longer applicable", which is the safety
    /// property working rather than a failure.
    /// </para>
    /// </remarks>
    /// <returns>The body and the leaf's status code, or <c>(null, 0)</c> when it could not be reached.</returns>
    /// <summary>
    /// Store one rule, by asking the leaf to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This API does not write rule files and does not judge rules.</b> Which signals, operators
    /// and actions exist belongs to the running build; a copy of that here is how the panel and the
    /// leaf come to disagree about which rules are valid. The leaf validates against what it can
    /// actually honour, stores only what passes, and answers with the verdict — which is relayed
    /// verbatim, status code included.
    /// </para>
    /// <para>
    /// A 422 from the leaf is a rule it refused: nothing was written and nothing changed. That is a
    /// real answer to the caller rather than a failure of this relay, so it travels as-is.
    /// </para>
    /// </remarks>
    public async Task<(string? Body, int Status)> WriteRuleAsync(
        string ruleId, string ruleJson, string by, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Reactor))
            return (null, 0); // disconnected at runtime: honest absent, no request.

        try
        {
            // The rule travels as the leaf's own document shape, with the actor beside it. Composed as
            // text rather than through a DTO because this API has no opinion about a rule's contents
            // and adding one here would be a second schema to keep in step with the build.
            string payload =
                $"{{\"rule\":{ruleJson},\"by\":{JsonSerializer.Serialize(by, ReactorRelayJson.Options)}}}";

            using var content = new StringContent(
                payload, System.Text.Encoding.UTF8, "application/json");

            using CancellationTokenSource bound = Bounded(ct, PreviewWithin);
            using HttpResponseMessage resp = await _http
                .PutAsync($"/rules/{Uri.EscapeDataString(ruleId)}", content, bound.Token)
                .ConfigureAwait(false);

            string body = await resp.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
            return (body, (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("reactor rule write timed out after {Timeout}", PreviewWithin);
            return (null, 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "reactor rule write failed");
            return (null, 0);
        }
    }

    /// <summary>
    /// Remove a rule's file, by asking the leaf to.
    /// </summary>
    /// <remarks>
    /// <b>Deleting is not retiring.</b> A retired rule keeps its file so the decisions it already made
    /// still resolve to a rule that can be named; deleting one leaves those naming an id nothing can
    /// describe. The panel retires by storing the rule with <c>retired</c> set — this is for a rule that
    /// was never meant to exist.
    /// </remarks>
    public async Task<(string? Body, int Status)> DeleteRuleAsync(string ruleId, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Reactor))
            return (null, 0);

        try
        {
            using CancellationTokenSource bound = Bounded(ct, PreviewWithin);
            using HttpResponseMessage resp = await _http
                .DeleteAsync($"/rules/{Uri.EscapeDataString(ruleId)}", bound.Token)
                .ConfigureAwait(false);

            string body = await resp.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
            return (body, (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("reactor rule delete timed out after {Timeout}", PreviewWithin);
            return (null, 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "reactor rule delete failed");
            return (null, 0);
        }
    }

    public async Task<(string? Body, int Status)> RedeemProposalAsync(
        string handle, bool confirm, string by, CancellationToken ct)
    {
        if (!_registry.IsProvisioned(ProvisionableLeaf.Reactor))
            return (null, 0); // disconnected at runtime: honest absent, no request.

        try
        {
            string verb = confirm ? "confirm" : "dismiss";
            using var content = new StringContent(
                JsonSerializer.Serialize(new ReactorRedemptionBody(by), ReactorRelayJson.Options),
                System.Text.Encoding.UTF8, "application/json");

            using CancellationTokenSource bound = Bounded(ct, PreviewWithin);
            using HttpResponseMessage resp = await _http
                .PostAsync($"/proposals/{Uri.EscapeDataString(handle)}/{verb}", content, bound.Token)
                .ConfigureAwait(false);

            string body = await resp.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
            return (body, (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not "nothing happened". The leaf claims a proposal before performing, so a timeout here
            // is a slow action rather than a refused one, and the caller has to re-read to find out.
            _logger.LogWarning(
                "reactor proposal redemption timed out after {Timeout}; the offer may already be spent",
                PreviewWithin);
            return (null, 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            _logger.LogDebug(ex, "reactor proposal redemption failed");
            return (null, 0);
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
    /// <b>The window is the leaf's to bound.</b> It clamps <c>days</c> to its own retention, because
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

            using CancellationTokenSource bound = Bounded(ct, AnswerWithin);
            using HttpResponseMessage resp = await _http.GetAsync(path, bound.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("reactor /decisions returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
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
