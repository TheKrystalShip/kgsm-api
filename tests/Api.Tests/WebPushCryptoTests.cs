using System.Security.Cryptography;
using System.Text;
using TheKrystalShip.Api.Services.Integrations.WebPush;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Pins the Web Push encryption against RFC 8291's own published example.
/// <para>
/// This matters more than a usual unit test: a mistake in the key derivation does not throw, it produces a
/// body the user agent silently discards. The failure would look like "push just doesn't work", with no
/// error anywhere on our side and nothing to read on the push service's. Reproducing the RFC's vector
/// byte-for-byte — with its fixed salt and fixed server key, which is the only reason the output is
/// deterministic — is what makes the implementation checkable offline.
/// </para>
/// </summary>
public class WebPushCryptoTests
{
    // RFC 8291 §5. The user agent's key pair and auth secret, the server's ephemeral key pair, and the salt.
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string Plaintext = "When I grow up, I want to be a watermelon";

    private const string Expected =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6Tlz"
        + "AC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    private static ECDiffieHellman ServerKey()
    {
        byte[] pub = WebPushCrypto.FromBase64Url(AsPublic);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = WebPushCrypto.FromBase64Url(AsPrivate),
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        });
    }

    [Fact]
    public void Encrypt_reproduces_the_RFC8291_example_body_exactly()
    {
        using ECDiffieHellman server = ServerKey();

        byte[] body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes(Plaintext),
            WebPushCrypto.FromBase64Url(UaPublic),
            WebPushCrypto.FromBase64Url(AuthSecret),
            WebPushCrypto.FromBase64Url(Salt),
            server);

        Assert.Equal(Expected, WebPushCrypto.ToBase64Url(body));
    }

    [Fact]
    public void Body_carries_the_aes128gcm_header_the_content_encoding_requires()
    {
        using ECDiffieHellman server = ServerKey();
        byte[] salt = WebPushCrypto.FromBase64Url(Salt);

        byte[] body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes(Plaintext), WebPushCrypto.FromBase64Url(UaPublic),
            WebPushCrypto.FromBase64Url(AuthSecret), salt, server);

        Assert.Equal(salt, body[..16]);
        // rs, big-endian.
        Assert.Equal((uint)WebPushCrypto.RecordSize, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
        // The key id is the server's ephemeral public point, inline and 65 bytes long.
        Assert.Equal(65, body[20]);
        Assert.Equal(WebPushCrypto.FromBase64Url(AsPublic), body[21..86]);
        // plaintext + the 0x02 delimiter + the GCM tag.
        Assert.Equal(Plaintext.Length + 1 + 16, body.Length - 86);
    }

    [Fact]
    public void A_fresh_send_never_repeats_its_salt_or_ephemeral_key()
    {
        byte[] ua = WebPushCrypto.FromBase64Url(UaPublic);
        byte[] auth = WebPushCrypto.FromBase64Url(AuthSecret);
        byte[] pt = Encoding.UTF8.GetBytes(Plaintext);

        byte[] a = WebPushCrypto.Encrypt(pt, ua, auth);
        byte[] b = WebPushCrypto.Encrypt(pt, ua, auth);

        Assert.NotEqual(a[..16], b[..16]);      // salt
        Assert.NotEqual(a[21..86], b[21..86]);  // ephemeral public key
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(64)]  // one byte short of a point
    [InlineData(66)]
    public void A_malformed_subscription_key_is_refused_rather_than_encrypted_to_nothing(int length)
    {
        byte[] bad = new byte[length];
        bad[0] = 0x04;
        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes("x"), bad, WebPushCrypto.FromBase64Url(AuthSecret)));
    }

    [Fact]
    public void An_auth_secret_of_the_wrong_length_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes("x"), WebPushCrypto.FromBase64Url(UaPublic), new byte[8]));
    }
}
