using System.Net;
using System.Net.Http.Headers;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>The outcome of one push, split by what the caller must DO about it.</summary>
public enum PushOutcome
{
    /// <summary>The push service accepted the message for delivery. It says nothing about whether the
    /// device ever shows it — that is the user agent's business and we never claim otherwise.</summary>
    Accepted,

    /// <summary>The subscription is definitively gone (404/410). Delete the row; it will never work again.</summary>
    Expired,

    /// <summary>A transient or unknown failure. Count it; don't delete on one bad answer.</summary>
    Failed,
}

public sealed record PushResult(PushOutcome Outcome, int? Status, string? Error);

/// <summary>
/// Posts one encrypted message to one push endpoint (RFC 8030 delivery, RFC 8291 body, RFC 8292 auth).
/// </summary>
public sealed class WebPushSender(HttpClient http, ILogger<WebPushSender> logger)
{
    /// <summary>How long the push service should hold the message for a device that is offline. Four
    /// hours: a crash notification is worth catching up on after a nap, worthless after a workday.</summary>
    private const int TtlSeconds = 4 * 60 * 60;

    public async Task<PushResult> SendAsync(
        PushSubscriptionEntity sub, byte[] payload, VapidKeyPair keys, string subject, CancellationToken ct)
    {
        if (!Uri.TryCreate(sub.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
            return new PushResult(PushOutcome.Expired, null, "endpoint is not an absolute https URL");

        byte[] body;
        try
        {
            body = WebPushCrypto.Encrypt(
                payload,
                WebPushCrypto.FromBase64Url(sub.P256dh),
                WebPushCrypto.FromBase64Url(sub.Auth));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // The stored keys are malformed — no retry will fix that, so retire the row rather than
            // failing against it forever.
            return new PushResult(PushOutcome.Expired, null, "subscription keys are unusable: " + ex.Message);
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentEncoding.Add("aes128gcm");
        req.Headers.TryAddWithoutValidation("TTL", TtlSeconds.ToString());
        // "Urgency: normal" is the default; left unset rather than restated.
        req.Headers.TryAddWithoutValidation(
            "Authorization", VapidSigner.Authorization(keys, endpoint, subject, DateTimeOffset.UtcNow));

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new PushResult(PushOutcome.Failed, null, ex.Message);
        }

        using (res)
        {
            int status = (int)res.StatusCode;
            if (res.IsSuccessStatusCode)
                return new PushResult(PushOutcome.Accepted, status, null);

            // 404/410 is the push service saying this subscription no longer exists — the browser
            // unsubscribed, cleared its data, or the service rotated it. It is the ONLY answer that
            // means "delete"; treating anything else that way would evict a device over a bad minute.
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return new PushResult(PushOutcome.Expired, status, "subscription no longer exists");

            string detail = await SafeReadAsync(res, ct).ConfigureAwait(false);
            logger.LogDebug("push rejected by {Host}: {Status} {Detail}", endpoint.Host, status, detail);
            return new PushResult(PushOutcome.Failed, status, detail);
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            string s = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return s.Length > 300 ? s[..300] : s;
        }
        catch { return res.ReasonPhrase ?? ""; }
    }
}
