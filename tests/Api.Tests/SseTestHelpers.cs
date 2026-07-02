using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Shared helpers for exercising <c>GET /api/v1/stream</c> in tests now that it is fetch-based SSE, not
/// a WebSocket. There is no more "Send" — topics are baked into the connect URL (<c>?topics=a,b,c</c>),
/// immutable for the connection's lifetime — so this is a smaller pair than the old WS Send/Receive: an
/// <see cref="OpenStream"/> to connect (usable standalone when a test only cares about the response's
/// status/content-type, e.g. the 401/query-token-ignored cases, without ever touching the body) and an
/// <see cref="SseFrameReader"/> to poll <c>data:</c> frames off an open 200 stream, mirroring the
/// race-free "keep producing the event across a deadline window, stop when the matching frame arrives"
/// polling shape the WS-era tests used (<c>AuditTests</c>/<c>PlayerRosterWsTests</c>/
/// <c>LeafProvisioningTests</c>) — same shape, just no socket underneath.
/// </summary>
internal static class SseTestHelpers
{
    /// <summary>
    /// Issue <c>GET {path}</c> as a long-lived SSE request: an <c>Accept: text/event-stream</c> header
    /// (matching the real client, <c>sse-migration-plan.md §2b</c>) and, if <paramref name="token"/> is
    /// given, an <c>Authorization: Bearer</c> header — <strong>never</strong> a query-string token; that
    /// hack is what the migration removed. Reads with <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// so the call returns as soon as the response headers land, before the (never-ending) body is read —
    /// a caller that only needs the status/content-type can assert on the result directly.
    /// </summary>
    public static Task<HttpResponseMessage> OpenStream(HttpClient client, string path, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>Wrap an open 200 SSE response's body in a frame reader.</summary>
    public static async Task<SseFrameReader> Frames(HttpResponseMessage response) =>
        new(await response.Content.ReadAsStreamAsync());
}

/// <summary>
/// Reads <c>data: &lt;json&gt;\n\n</c> frames off an open <c>/api/v1/stream</c> body, one SSE block
/// (lines up to a blank line) at a time. A comment-only block — <c>: connected\n\n</c> / <c>:
/// keepalive\n\n</c> — carries no <c>data:</c> line and is silently skipped, exactly like a real client's
/// parser would ignore it.
/// </summary>
internal sealed class SseFrameReader(Stream body) : IDisposable
{
    private readonly Stream _body = body;
    private byte[] _buf = [];

    /// <summary>
    /// Keep reading blocks until one satisfies <paramref name="predicate"/> or <paramref name="timeout"/>
    /// elapses. Returns the matching frame's parsed <c>data:</c> JSON, or <c>null</c> on timeout — the
    /// same "nothing arrived in time, not an error" contract the WS-era <c>Receive(socket, timeout)</c>
    /// had, so a caller can loop "trigger the event server-side, poll for the frame" across a deadline
    /// window (no ack protocol) exactly as before, or — for a "prove silence" assertion (e.g. an
    /// operator-only topic silently dropped for a viewer) — call it once with a short bounded timeout and
    /// assert the result is <c>null</c>.
    /// </summary>
    public async Task<JsonElement?> WaitForFrame(Func<JsonElement, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                JsonElement? frame = await ReadOneBlockAsync(cts.Token).ConfigureAwait(false);
                if (frame is null) return null; // stream ended (server closed / connection torn down)
                if (predicate(frame.Value)) return frame;
            }
        }
        catch (OperationCanceledException)
        {
            return null; // the deadline passed with nothing matching
        }
    }

    /// <summary>Read one <c>\n\n</c>-delimited block; returns its parsed <c>data:</c> JSON, or <c>null</c>
    /// for a comment-only block (caller keeps reading) — actually loops internally past comment blocks so
    /// every non-null return is a real frame. Returns <c>null</c> only at end-of-stream.</summary>
    private async Task<JsonElement?> ReadOneBlockAsync(CancellationToken ct)
    {
        while (true)
        {
            List<string> lines = [];
            string? line;
            while ((line = await ReadLineAsync(ct).ConfigureAwait(false)) is not null && line.Length > 0)
                lines.Add(line);

            if (line is null && lines.Count == 0)
                return null; // end of stream, nothing left to parse

            string? dataLine = lines.FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
            if (dataLine is null)
                continue; // ": connected" / ": keepalive" (or any other comment-only block) — skip it

            string json = dataLine["data:".Length..].Trim();
            return JsonDocument.Parse(json).RootElement.Clone();
        }
    }

    // A tiny hand-rolled line reader over the raw byte stream (no StreamReader — its ReadLineAsync(ct)
    // buffers ahead of what we've consumed, which is fine here since nothing else reads this stream, but
    // rolling it by hand keeps this file dependency-free and makes the "one line at a time, respecting
    // cancellation" behavior explicit).
    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int read = await ReadByteAsync(one, ct).ConfigureAwait(false);
            if (read == 0)
                return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            if (one[0] == (byte)'\n')
                return Encoding.UTF8.GetString(line.ToArray());
            line.Add(one[0]);
        }
    }

    private async Task<int> ReadByteAsync(byte[] one, CancellationToken ct)
    {
        if (_buf.Length > 0)
        {
            one[0] = _buf[0];
            _buf = _buf[1..];
            return 1;
        }
        // Read a small chunk ahead so we're not doing one syscall per byte; anything beyond the single
        // byte returned is stashed in _buf for the next call.
        byte[] chunk = new byte[256];
        int n = await _body.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
        if (n == 0) return 0;
        one[0] = chunk[0];
        _buf = chunk[1..n];
        return 1;
    }

    public void Dispose() => _body.Dispose();
}
