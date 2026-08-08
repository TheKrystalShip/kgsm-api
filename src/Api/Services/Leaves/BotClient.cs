using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The kgsm-bot leaf client. Like the scheduler — and unlike the monitor/assistant — the bot speaks
/// <strong>NDJSON-over-unix-socket</strong> rather than HTTP: it carries no web stack, so on connect it
/// writes one JSON line (its gateway/guild/channel status) and closes. Registered ONLY when the socket
/// is configured (<c>Api__BotSocketPath</c>); consumers resolve it optionally and report the surface
/// absent when it is missing.
/// </summary>
/// <remarks>
/// Reading a line here is a genuinely stronger signal than systemd liveness for this leaf: the unit can
/// be active and the gateway connected while the guild never populated, in which case the bot can post
/// nothing at all. The status line carries the resolved guild, so the difference is visible.
/// <para>
/// Honesty: an unreachable, slow or malformed snapshot yields <c>null</c> — the caller reports that it
/// could not be read, which is a different statement from a bot that answered with nothing configured.
/// </para>
/// </remarks>
public sealed class BotClient
{
    // The bot writes one line and closes; bound it so a hung socket can never stall a request.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly string _socketPath;
    private readonly ILogger<BotClient> _logger;

    public BotClient(ApiOptions options, ILogger<BotClient> logger)
    {
        _socketPath = options.BotSocketPath;
        _logger = logger;
    }

    /// <summary>
    /// Connects, reads the one-line status snapshot, and returns it <b>verbatim</b> — or <c>null</c> when
    /// the socket is unreachable, slow, or the line is empty.
    /// </summary>
    /// <remarks>
    /// The raw JSON is relayed rather than deserialized: the bot owns this shape, and re-modelling it
    /// here would add a second definition to keep in step for no gain (the same call the metrics-history
    /// relay makes). The line is parsed only far enough to reject a malformed one, so the API never
    /// forwards a body the SPA would choke on.
    /// </remarks>
    public async Task<string?> GetStatusJsonAsync(CancellationToken ct = default)
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

            // Validate without modelling: a body that isn't JSON is a fault worth reporting as one,
            // rather than relaying for the browser to fail on.
            using (JsonDocument.Parse(line)) { }
            return line;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("bot status read timed out after {Timeout} at {Path}", ReadTimeout, _socketPath);
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException)
        {
            _logger.LogDebug(ex, "bot socket unreachable/unreadable at {Path}", _socketPath);
            return null;
        }
    }
}
