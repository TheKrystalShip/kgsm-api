using System.Security.Cryptography;
using System.Text.Json;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>
/// The application server's VAPID identity (RFC 8292) — one P-256 key pair per host, and the signed JWT
/// that proves a push request came from us.
/// <para>
/// The pair is generated once and then <b>fixed for the life of the host</b>: the public key is baked into
/// every subscription a browser creates, so rotating it silently invalidates every device already
/// registered. That is why it is persisted beside the integration config rather than derived at boot.
/// </para>
/// </summary>
public sealed record VapidKeyPair(string PrivateKey, string PublicKey)
{
    /// <summary>A fresh pair, base64url-encoded: the private scalar (32 bytes) and the uncompressed public
    /// point (65 bytes, which is the form <c>applicationServerKey</c> takes in the browser).</summary>
    public static VapidKeyPair Generate()
    {
        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters p = ec.ExportParameters(true);
        byte[] point = new byte[65];
        point[0] = 0x04;
        RightAlign(p.Q.X!, point.AsSpan(1, 32));
        RightAlign(p.Q.Y!, point.AsSpan(33, 32));
        byte[] d = new byte[32];
        RightAlign(p.D!, d);
        return new VapidKeyPair(WebPushCrypto.ToBase64Url(d), WebPushCrypto.ToBase64Url(point));
    }

    private static void RightAlign(byte[] src, Span<byte> dst)
    {
        if (src.Length > dst.Length) src = src[^dst.Length..];
        dst.Clear();
        src.CopyTo(dst[(dst.Length - src.Length)..]);
    }

    internal ECDsa CreateSigner()
    {
        byte[] pub = WebPushCrypto.FromBase64Url(PublicKey);
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = WebPushCrypto.FromBase64Url(PrivateKey),
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        });
    }
}

/// <summary>Builds the <c>Authorization: vapid</c> header value for one push request.</summary>
public static class VapidSigner
{
    /// <summary>How long a token stays valid. Comfortably inside the 24h ceiling RFC 8292 puts on
    /// <c>exp</c>, which some push services enforce strictly.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The header value: <c>vapid t=&lt;jwt&gt;, k=&lt;public key&gt;</c>.
    /// </summary>
    /// <param name="endpoint">The subscription endpoint. Only its ORIGIN goes in the token's audience —
    /// sending the full path would leak the subscription id into the JWT and be rejected besides.</param>
    /// <param name="subject">A <c>mailto:</c> or <c>https:</c> contact for whoever operates this server;
    /// push services want a way to reach an abusive sender.</param>
    public static string Authorization(VapidKeyPair keys, Uri endpoint, string subject, DateTimeOffset now)
    {
        string audience = endpoint.GetLeftPart(UriPartial.Authority);
        long exp = now.Add(Lifetime).ToUnixTimeSeconds();

        string header = WebPushCrypto.ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(
            new JwtHeader("JWT", "ES256"), Json));
        string payload = WebPushCrypto.ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(
            new JwtClaims(audience, exp, subject), Json));

        byte[] signingInput = WebPushCrypto.Utf8(header + "." + payload);
        using ECDsa signer = keys.CreateSigner();
        // ES256 is r‖s fixed-width, NOT the DER/ASN.1 encoding SignData produces by default — a DER
        // signature is well-formed and every push service rejects it.
        byte[] sig = signer.SignData(signingInput, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"vapid t={header}.{payload}.{WebPushCrypto.ToBase64Url(sig)}, k={keys.PublicKey}";
    }

    private sealed record JwtHeader(string Typ, string Alg);

    private sealed record JwtClaims(string Aud, long Exp, string Sub);
}
