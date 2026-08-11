using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>
/// Message encryption for Web Push — RFC 8291 over the <c>aes128gcm</c> content encoding of RFC 8188.
/// <para>
/// Hand-rolled on <c>System.Security.Cryptography</c> rather than taken from a package: every primitive
/// this needs (P-256 ECDH, HKDF, AES-GCM) is in the BCL on net10, while the usual NuGet option drags in
/// BouncyCastle and Newtonsoft for the same ~80 lines. The correctness argument is the RFC's own published
/// test vector, which <c>WebPushCryptoTests</c> reproduces byte-for-byte — a bug here would not throw, it
/// would silently deliver nothing, so it is pinned rather than eyeballed.
/// </para>
/// <para>
/// The push service is a relay and never a reader: the body is sealed to the subscription's own key pair,
/// so Google/Mozilla/Apple carry ciphertext they cannot open. That is what makes routing a self-hosted
/// panel's notifications through them acceptable.
/// </para>
/// </summary>
public static class WebPushCrypto
{
    /// <summary>The one record size we emit. A single record holds the whole payload, so this only has to
    /// exceed it; 4096 is the conventional value and the practical push-service body ceiling.</summary>
    public const int RecordSize = 4096;

    private const int KeyLength = 65;   // uncompressed P-256 point: 0x04 || X(32) || Y(32)
    private const int SaltLength = 16;
    private const int TagLength = 16;

    private static readonly byte[] KeyInfoPrefix = "WebPush: info\0"u8.ToArray();
    private static readonly byte[] CekInfo = "Content-Encoding: aes128gcm\0"u8.ToArray();
    private static readonly byte[] NonceInfo = "Content-Encoding: nonce\0"u8.ToArray();

    /// <summary>
    /// Seal <paramref name="plaintext"/> for one subscription. Returns the complete <c>aes128gcm</c> body:
    /// <c>salt(16) ‖ rs(4) ‖ idlen(1) ‖ as_public(65) ‖ ciphertext</c>.
    /// </summary>
    /// <param name="uaPublicKey">The subscription's <c>p256dh</c>, raw (65 bytes, uncompressed point).</param>
    /// <param name="authSecret">The subscription's <c>auth</c>, raw (16 bytes).</param>
    /// <param name="salt">Test seam only — a fixed salt to reproduce a known vector. Production passes
    /// <see langword="null"/> and gets 16 fresh random bytes, which is required: a reused salt with a reused
    /// ephemeral key would repeat a GCM nonce.</param>
    /// <param name="serverKey">Test seam only — a fixed ephemeral key pair. Production passes
    /// <see langword="null"/> and gets a fresh one per message.</param>
    public static byte[] Encrypt(
        byte[] plaintext,
        byte[] uaPublicKey,
        byte[] authSecret,
        byte[]? salt = null,
        ECDiffieHellman? serverKey = null)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (uaPublicKey is not { Length: KeyLength } || uaPublicKey[0] != 0x04)
            throw new ArgumentException("p256dh must be a 65-byte uncompressed P-256 point", nameof(uaPublicKey));
        if (authSecret is not { Length: 16 })
            throw new ArgumentException("auth must be 16 bytes", nameof(authSecret));

        salt ??= RandomNumberGenerator.GetBytes(SaltLength);
        if (salt.Length != SaltLength)
            throw new ArgumentException("salt must be 16 bytes", nameof(salt));

        ECDiffieHellman ephemeral = serverKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            byte[] asPublic = ExportPoint(ephemeral);

            // The shared secret is the RAW X coordinate — DeriveKeyMaterial would hash it, which is the
            // wrong input for RFC 8291 and fails in a way no exception reports.
            using ECDiffieHellman ua = ImportPoint(uaPublicKey);
            byte[] ecdhSecret = ephemeral.DeriveRawSecretAgreement(ua.PublicKey);

            // RFC 8291 §3.4 — the auth secret salts an extra HKDF round whose info binds BOTH public keys,
            // so a ciphertext is tied to this exact pair of parties.
            byte[] keyInfo = [.. KeyInfoPrefix, .. uaPublicKey, .. asPublic];
            byte[] prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdhSecret, authSecret);
            byte[] ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

            // RFC 8188 §2.2 — the content-encoding key and nonce.
            byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
            byte[] cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);
            byte[] nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

            // One record, so the padding delimiter is 0x02 ("last record"). 0x01 here would make a
            // conforming user agent wait for a continuation that never comes.
            byte[] padded = [.. plaintext, 0x02];
            byte[] ciphertext = new byte[padded.Length + TagLength];
            using (var gcm = new AesGcm(cek, TagLength))
            {
                gcm.Encrypt(nonce, padded,
                    ciphertext.AsSpan(0, padded.Length), ciphertext.AsSpan(padded.Length, TagLength));
            }

            // The header block (RFC 8188 §2.1), then the single record.
            byte[] body = new byte[SaltLength + 4 + 1 + KeyLength + ciphertext.Length];
            Span<byte> w = body;
            salt.CopyTo(w);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(w[SaltLength..], RecordSize);
            w[SaltLength + 4] = KeyLength;
            asPublic.CopyTo(w[(SaltLength + 5)..]);
            ciphertext.CopyTo(w[(SaltLength + 5 + KeyLength)..]);

            CryptographicOperations.ZeroMemory(ecdhSecret);
            CryptographicOperations.ZeroMemory(prkKey);
            CryptographicOperations.ZeroMemory(ikm);
            CryptographicOperations.ZeroMemory(prk);
            CryptographicOperations.ZeroMemory(cek);
            return body;
        }
        finally
        {
            // Only dispose what we created; a caller-supplied key belongs to the caller.
            if (serverKey is null) ephemeral.Dispose();
        }
    }

    /// <summary>The 65-byte uncompressed public point of an ECDH key, X and Y left-padded to the curve size
    /// (an export can be short when a coordinate has leading zero bytes).</summary>
    internal static byte[] ExportPoint(ECDiffieHellman key)
    {
        ECParameters p = key.ExportParameters(false);
        byte[] point = new byte[KeyLength];
        point[0] = 0x04;
        CopyRightAligned(p.Q.X!, point.AsSpan(1, 32));
        CopyRightAligned(p.Q.Y!, point.AsSpan(33, 32));
        return point;
    }

    private static ECDiffieHellman ImportPoint(byte[] point) =>
        ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });

    private static void CopyRightAligned(byte[] src, Span<byte> dst)
    {
        if (src.Length > dst.Length) src = src[^dst.Length..];
        dst.Clear();
        src.CopyTo(dst[(dst.Length - src.Length)..]);
    }

    /// <summary>base64url decode, tolerating the padding a client may or may not send.</summary>
    public static byte[] FromBase64Url(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Base64Url.DecodeFromChars(s.TrimEnd('=').AsSpan());
    }

    public static string ToBase64Url(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    internal static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
