using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Integrations.WebPush;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What actually goes on the wire for one push, checked from the user agent's side of the fence.
/// <para>
/// <see cref="WebPushCryptoTests"/> proves the ciphertext matches the RFC. This proves the rest of the
/// request is something a browser could accept: it captures the real <see cref="WebPushSender"/>
/// request without sending it, verifies the VAPID token the way a push service would (signature,
/// audience, expiry), and then <b>decrypts the body with the subscription's own private key</b> to get
/// the notification JSON back. If any link were wrong the payload would not come out the far end.
/// </para>
/// <para>No network: the push service is a stub handler, so the endpoint host is never contacted.</para>
/// </summary>
public class WebPushDeliveryTests
{
    private const string Endpoint = "https://fcm.googleapis.test/fcm/send/abc123";

    /// <summary>A stand-in push service that records the request and answers with a fixed status.</summary>
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public byte[] Body { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent("") };
        }
    }

    /// <summary>A browser's subscription: its own P-256 key pair plus a 16-byte auth secret.</summary>
    private sealed record UserAgent(ECDiffieHellman Key, byte[] Public, byte[] AuthSecret)
    {
        public static UserAgent New()
        {
            ECDiffieHellman k = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            return new UserAgent(k, WebPushCrypto.ExportPoint(k), RandomNumberGenerator.GetBytes(16));
        }

        public PushSubscriptionEntity Subscription() => new()
        {
            Endpoint = Endpoint,
            UserSubject = "local:usr_test",
            P256dh = WebPushCrypto.ToBase64Url(Public),
            Auth = WebPushCrypto.ToBase64Url(AuthSecret),
        };
    }

    private static (WebPushSender Sender, CapturingHandler Handler) Sender(HttpStatusCode status = HttpStatusCode.Created)
    {
        var handler = new CapturingHandler(status);
        return (new WebPushSender(new HttpClient(handler), NullLogger<WebPushSender>.Instance), handler);
    }

    /// <summary>The user-agent half of RFC 8291 — exactly the steps a browser runs on receipt.</summary>
    private static byte[] Decrypt(byte[] body, UserAgent ua)
    {
        byte[] salt = body[..16];
        int idLen = body[20];
        byte[] asPublic = body[21..(21 + idLen)];
        byte[] ciphertext = body[(21 + idLen)..];

        using ECDiffieHellman server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] },
        });
        byte[] ecdh = ua.Key.DeriveRawSecretAgreement(server.PublicKey);

        byte[] keyInfo = [.. Encoding.UTF8.GetBytes("WebPush: info\0"), .. ua.Public, .. asPublic];
        byte[] prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdh, ua.AuthSecret);
        byte[] ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);
        byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        byte[] cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.UTF8.GetBytes("Content-Encoding: aes128gcm\0"));
        byte[] nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.UTF8.GetBytes("Content-Encoding: nonce\0"));

        byte[] plain = new byte[ciphertext.Length - 16];
        using var gcm = new AesGcm(cek, 16);
        gcm.Decrypt(nonce, ciphertext.AsSpan(0, plain.Length), ciphertext.AsSpan(plain.Length, 16), plain);

        // Strip the record delimiter the encoder appended.
        Assert.Equal(0x02, plain[^1]);
        return plain[..^1];
    }

    [Fact]
    public async Task The_subscribed_browser_can_decrypt_the_notification_we_send_it()
    {
        UserAgent ua = UserAgent.New();
        (WebPushSender sender, CapturingHandler handler) = Sender();
        VapidKeyPair keys = VapidKeyPair.Generate();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { title = "minecraft crashed", body = "watchdog restarted it", serverId = "minecraft" });

        PushResult result = await sender.SendAsync(ua.Subscription(), payload, keys, "https://panel.test", default);

        Assert.Equal(PushOutcome.Accepted, result.Outcome);
        byte[] plain = Decrypt(handler.Body, ua);
        JsonElement got = JsonSerializer.Deserialize<JsonElement>(plain);
        Assert.Equal("minecraft crashed", got.GetProperty("title").GetString());
        Assert.Equal("minecraft", got.GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task The_request_carries_the_headers_the_push_protocol_requires()
    {
        UserAgent ua = UserAgent.New();
        (WebPushSender sender, CapturingHandler handler) = Sender();
        await sender.SendAsync(ua.Subscription(), "{}"u8.ToArray(), VapidKeyPair.Generate(), "https://panel.test", default);

        HttpRequestMessage req = handler.Request!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(Endpoint, req.RequestUri!.ToString());
        Assert.Equal("aes128gcm", Assert.Single(req.Content!.Headers.ContentEncoding));
        Assert.Equal("application/octet-stream", req.Content.Headers.ContentType!.MediaType);
        Assert.True(req.Headers.TryGetValues("TTL", out var ttl) && int.Parse(ttl.First()) > 0);
    }

    [Fact]
    public async Task The_vapid_token_verifies_against_the_advertised_key_and_names_the_endpoint_origin()
    {
        UserAgent ua = UserAgent.New();
        (WebPushSender sender, CapturingHandler handler) = Sender();
        VapidKeyPair keys = VapidKeyPair.Generate();

        await sender.SendAsync(ua.Subscription(), "{}"u8.ToArray(), keys, "https://panel.test", default);

        string auth = handler.Request!.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("vapid ", auth);
        string t = Part(auth, "t="), k = Part(auth, "k=");
        Assert.Equal(keys.PublicKey, k);

        string[] segs = t.Split('.');
        Assert.Equal(3, segs.Length);

        // A push service checks the audience is ITS origin and that the token has not expired.
        JsonElement claims = JsonSerializer.Deserialize<JsonElement>(WebPushCrypto.FromBase64Url(segs[1]));
        Assert.Equal("https://fcm.googleapis.test", claims.GetProperty("aud").GetString());
        Assert.Equal("https://panel.test", claims.GetProperty("sub").GetString());
        long exp = claims.GetProperty("exp").GetInt64();
        Assert.InRange(exp, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds());

        // ES256 is a raw r‖s signature; a DER-encoded one is well-formed and universally rejected.
        byte[] sig = WebPushCrypto.FromBase64Url(segs[2]);
        Assert.Equal(64, sig.Length);
        byte[] pub = WebPushCrypto.FromBase64Url(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        });
        Assert.True(verifier.VerifyData(
            Encoding.UTF8.GetBytes(segs[0] + "." + segs[1]), sig,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task A_dead_subscription_reports_Expired_so_the_row_is_dropped(HttpStatusCode status)
    {
        UserAgent ua = UserAgent.New();
        (WebPushSender sender, _) = Sender(status);
        PushResult r = await sender.SendAsync(ua.Subscription(), "{}"u8.ToArray(), VapidKeyPair.Generate(), "https://panel.test", default);
        Assert.Equal(PushOutcome.Expired, r.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task Any_other_rejection_is_Failed_not_Expired(HttpStatusCode status)
    {
        UserAgent ua = UserAgent.New();
        (WebPushSender sender, _) = Sender(status);
        PushResult r = await sender.SendAsync(ua.Subscription(), "{}"u8.ToArray(), VapidKeyPair.Generate(), "https://panel.test", default);
        // Deleting on a 429 would evict a live device over a busy minute.
        Assert.Equal(PushOutcome.Failed, r.Outcome);
    }

    [Fact]
    public async Task A_subscription_with_unusable_keys_is_retired_rather_than_retried_forever()
    {
        (WebPushSender sender, CapturingHandler handler) = Sender();
        var broken = new PushSubscriptionEntity
        {
            Endpoint = Endpoint, UserSubject = "u", P256dh = "AAAA", Auth = WebPushCrypto.ToBase64Url(new byte[16]),
        };
        PushResult r = await sender.SendAsync(broken, "{}"u8.ToArray(), VapidKeyPair.Generate(), "https://panel.test", default);
        Assert.Equal(PushOutcome.Expired, r.Outcome);
        Assert.Null(handler.Request); // never even attempted
    }

    private static string Part(string header, string prefix) =>
        header["vapid ".Length..].Split(',').Select(p => p.Trim())
            .First(p => p.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
}
