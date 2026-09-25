using System.Net.Sockets;
using System.Text.Json;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The kgsm-bot leaf client: the API's read seam onto the bot's status socket.
/// </summary>
/// <remarks>
/// The bot serves HTTP over a unix-domain socket, so the transport is the same
/// <see cref="SocketsHttpHandler.ConnectCallback"/> pattern the monitor and reactor clients use, and
/// <c>GET /status</c> is the snapshot. Registered ONLY when the socket is configured
/// (<c>Api__BotSocketPath</c>); consumers resolve it optionally and report the surface absent when it
/// is missing.
/// <para>
/// Reading it is a genuinely stronger signal than systemd liveness for this leaf: the unit can be
/// active and the gateway connected while the guild never populated, in which case the bot can post
/// nothing at all. The snapshot carries the resolved guild, so the difference is visible.
/// </para>
/// <para>
/// Honesty: an unreachable, slow or malformed snapshot yields <c>null</c> — the caller reports that it
/// could not be read, which is a different statement from a bot that answered with nothing configured.
/// </para>
/// </remarks>
public sealed class BotClient
{
    // The bot answers off an in-memory read of its live client; bound it so a hung socket can never
    // stall a request.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly string _socketPath;
    private readonly ILogger<BotClient> _logger;
    private readonly HttpClient _http;

    public BotClient(ApiOptions options, ILogger<BotClient> logger)
    {
        _socketPath = options.BotSocketPath;
        _logger = logger;

        string socketPath = _socketPath;

        var handler = new SocketsHttpHandler
        {
            // Every connection is dialed over the unix-domain socket; the request URI host is a
            // placeholder the bot ignores.
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
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Asks for the status snapshot and returns it <b>verbatim</b> — or <c>null</c> when the socket is
    /// unreachable, slow, or the answer is empty.
    /// </summary>
    /// <remarks>
    /// The raw JSON is relayed rather than deserialized: the bot owns this shape, and re-modelling it
    /// here would add a second definition to keep in step for no gain (the same call the metrics-history
    /// relay makes). It is parsed only far enough to reject a malformed body, so the API never forwards
    /// one the SPA would choke on.
    /// </remarks>
    public async Task<string?> GetStatusJsonAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_socketPath))
            return null;

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ReadTimeout);
        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync("/status", timed.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("bot answered {Status} for /status at {Path}",
                    (int)response.StatusCode, _socketPath);
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(timed.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            // Validate without modelling: a body that isn't JSON is a fault worth reporting as one,
            // rather than relaying for the browser to fail on.
            using (JsonDocument.Parse(body)) { }
            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("bot status read timed out after {Timeout} at {Path}", ReadTimeout, _socketPath);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or JsonException)
        {
            _logger.LogDebug(ex, "bot socket unreachable/unreadable at {Path}", _socketPath);
            return null;
        }
    }
}
