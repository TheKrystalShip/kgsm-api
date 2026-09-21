using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>What a leaf answered for itself, passed through untouched.</summary>
/// <param name="Status">The leaf's own status code.</param>
/// <param name="Body">Its body, verbatim — never re-serialized, because re-serializing means holding
/// the shape here, which is the duplication this relay exists to end.</param>
/// <param name="ContentType">What it said the body is.</param>
public sealed record LeafSurfaceAnswer(int Status, string Body, string? ContentType);

/// <summary>
/// Asks a leaf about itself, over the socket it serves its own surface on.
/// </summary>
/// <remarks>
/// <para>
/// <b>A component owns its configuration, and only the transport differs.</b> An anchor serves that
/// over HTTP at its member address; a leaf serves it over a unix socket and this relays, because this
/// API is the only thing on a node with an address a browser can reach. So what travels here is the
/// leaf's own answer — status and body, unread — and this holds no second copy of the descriptor's
/// rules to check it against.
/// </para>
/// <para>
/// <b>The socket is derived, never configured.</b> Every leaf's unit already provisions
/// <c>/run/kgsm-&lt;id&gt;/</c> as its <c>RuntimeDirectory</c>, so the surface socket is one path built
/// from the leaf's own id and this API carries no list of leaves and no per-leaf setting. A leaf that
/// serves one is a leaf that answers; that is the whole registration.
/// </para>
/// <para>
/// <b>Silence is an answer this cannot give.</b> A leaf that is down, that has no surface, or that
/// refuses the connection yields null — and null is the caller's cue to fall back to reading the
/// descriptor itself. That path is not a leftover: a leaf that is down is exactly when its
/// configuration is wanted, and it is the one case a self-serving component cannot cover.
/// </para>
/// </remarks>
public sealed class LeafSurfaceRelay
{
    /// <summary>The routes every component mounts, under one prefix so this needs to know nothing
    /// about which leaf it is forwarding to.</summary>
    private const string Prefix = "/component/";

    // The dial is local and the leaf is answering about files on this machine. A leaf that has not
    // answered in this long is one the caller should stop waiting for and read for itself.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ApiOptions _options;
    private readonly ILogger<LeafSurfaceRelay> _logger;

    public LeafSurfaceRelay(ApiOptions options, ILogger<LeafSurfaceRelay> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Where this leaf would serve its own surface, if it serves one.</summary>
    public string SocketPathFor(string leafId) =>
        Path.Combine(_options.LeafSurfaceRoot, "kgsm-" + leafId, "surface.sock");

    /// <summary>Whether this leaf is serving a surface at all — the socket file's presence.</summary>
    /// <remarks>
    /// Cheap enough to ask per request, and it is a fact rather than a cache: a leaf that has just
    /// started serving one is relayed to at once, and one that has stopped falls back at once.
    /// </remarks>
    public bool ServesOwnSurface(string leafId)
    {
        try { return File.Exists(SocketPathFor(leafId)); }
        catch { return false; }
    }

    /// <summary>
    /// Forwards one request to the leaf and hands back what it said, or null when it said nothing.
    /// </summary>
    public async Task<LeafSurfaceAnswer?> SendAsync(
        string leafId, HttpMethod method, string route, string? body, CancellationToken ct)
    {
        string socket = SocketPathFor(leafId);
        if (!ServesOwnSurface(leafId))
            return null;

        using var handler = new SocketsHttpHandler
        {
            // Every connection is dialed over the unix socket; the request URI's host is a placeholder
            // the leaf ignores.
            ConnectCallback = async (_, cancel) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(socket), cancel).ConfigureAwait(false);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
        };

        using var client = new HttpClient(handler) { Timeout = Timeout };

        using var request = new HttpRequestMessage(method, "http://leaf" + Prefix + route.TrimStart('/'));
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

        try
        {
            using HttpResponseMessage res = await client.SendAsync(request, ct).ConfigureAwait(false);

            // A leaf that answers on the socket but does not know this route has no surface; reading
            // its descriptor here is the honest fallback rather than reporting the leaf as absent.
            if (res.StatusCode == HttpStatusCode.NotFound && res.Content.Headers.ContentLength is null or 0)
                return null;

            string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new LeafSurfaceAnswer((int)res.StatusCode, text, res.Content.Headers.ContentType?.ToString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the caller left — propagate rather than reporting the leaf silent
        }
        catch (Exception ex)
        {
            // Down, mid-restart, or holding a socket file nothing is listening on. All of them mean
            // the same thing to the caller: ask the disk instead.
            _logger.LogDebug(ex, "{Leaf} did not answer for itself on {Socket}", leafId, socket);
            return null;
        }
    }
}
