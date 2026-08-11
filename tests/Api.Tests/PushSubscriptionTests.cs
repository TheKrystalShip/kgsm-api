using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Integrations.WebPush;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The per-user half of Web Push: registering a device, listing your own, and revoking one.
/// <para>
/// The isolation cases are the point of this file. A subscription row is a capability to push to
/// somebody's phone, so "can user B see or delete user A's device" is a security question, not a
/// tidiness one — and the honest way to ask it is with two real signed-in identities, not one identity
/// and a hand-built query.
/// </para>
/// </summary>
public class PushSubscriptionTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    // A structurally valid subscription: a 65-byte uncompressed P-256 point and a 16-byte auth secret.
    // Made up, but the RIGHT SHAPE — the endpoint validates key material on the way in, so a lazy
    // placeholder would be rejected and the test would pass for the wrong reason.
    private static string ValidP256dh()
    {
        byte[] point = new byte[65];
        point[0] = 0x04;
        Random.Shared.NextBytes(point.AsSpan(1));
        return WebPushCrypto.ToBase64Url(point);
    }

    private static string ValidAuth() => WebPushCrypto.ToBase64Url(Random.Shared.GetItems<byte>([1, 2, 3, 4], 16));

    private static object Subscription(string endpoint) =>
        new { endpoint, keys = new { p256dh = ValidP256dh(), auth = ValidAuth() } };

    private HttpClient Client(string token)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>A SECOND real identity, so cross-user isolation is exercised against two different
    /// subjects rather than one subject pretending to be two.</summary>
    private string OtherUserToken()
    {
        var identity = new KgsmIdentity(KgsmActorProvider.Discord, "999000111", "someone-else", "Someone Else", null, []);
        var tokens = factory.Services.GetRequiredService<ISessionTokenService>();
        var store = factory.Services.GetRequiredService<SessionStore>();
        var opts = factory.Services.GetRequiredService<TheKrystalShip.Api.ApiOptions>();
        string sid = "sid_other_" + Guid.NewGuid().ToString("N");
        MintedToken minted = tokens.MintAccess(identity, KgsmTier.Viewer, sid);
        store.CreateAsync(sid, identity.Handle, opts.HostId, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(opts.SessionsRefreshAbsoluteDays),
            userAgent: null, initialJti: minted.Jti, CancellationToken.None).GetAwaiter().GetResult();
        AuthTestFactory.SetAccountOn(factory.Services, identity, KgsmTier.Viewer);
        return minted.Token;
    }

    [Fact]
    public async Task The_key_endpoint_hands_out_a_usable_application_server_key()
    {
        HttpClient c = Client(factory.AccessToken(KgsmTier.Viewer));
        HttpResponseMessage res = await c.GetAsync("/api/v1/push/key");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        JsonElement body = await res.Content.ReadFromJsonAsync<JsonElement>();
        string key = body.GetProperty("publicKey").GetString()!;
        // It must be the uncompressed point the browser passes as applicationServerKey, or
        // pushManager.subscribe rejects it.
        byte[] raw = WebPushCrypto.FromBase64Url(key);
        Assert.Equal(65, raw.Length);
        Assert.Equal(0x04, raw[0]);
    }

    [Fact]
    public async Task The_key_is_stable_across_calls()
    {
        HttpClient c = Client(factory.AccessToken(KgsmTier.Viewer));
        // Regenerating would silently orphan every device already subscribed with the old key.
        JsonElement a = await c.GetFromJsonAsync<JsonElement>("/api/v1/push/key");
        JsonElement b = await c.GetFromJsonAsync<JsonElement>("/api/v1/push/key");
        Assert.Equal(a.GetProperty("publicKey").GetString(), b.GetProperty("publicKey").GetString());
    }

    [Fact]
    public async Task A_device_registers_and_appears_in_its_owners_list()
    {
        HttpClient c = Client(factory.AccessToken(KgsmTier.Viewer));
        string endpoint = "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N");

        HttpResponseMessage res = await c.PostAsJsonAsync("/api/v1/push/subscriptions", Subscription(endpoint));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        JsonElement list = await c.GetFromJsonAsync<JsonElement>(
            "/api/v1/push/subscriptions?endpoint=" + Uri.EscapeDataString(endpoint));
        JsonElement[] devices = [.. list.GetProperty("devices").EnumerateArray()];
        Assert.Contains(devices, d => d.GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task Re_subscribing_the_same_browser_updates_one_row_rather_than_adding_another()
    {
        HttpClient c = Client(factory.AccessToken(KgsmTier.Viewer));
        string endpoint = "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N");

        await c.PostAsJsonAsync("/api/v1/push/subscriptions", Subscription(endpoint));
        await c.PostAsJsonAsync("/api/v1/push/subscriptions", Subscription(endpoint));

        JsonElement list = await c.GetFromJsonAsync<JsonElement>("/api/v1/push/subscriptions");
        int matches = list.GetProperty("devices").EnumerateArray().Count();
        // Two rows would mean every notification arrives on this device twice.
        Assert.Equal(1, list.GetProperty("devices").EnumerateArray()
            .Count(d => d.GetProperty("id").GetString() == Fingerprint(endpoint)));
        Assert.True(matches >= 1);
    }

    [Fact]
    public async Task One_users_devices_are_invisible_to_another()
    {
        string mineEndpoint = "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N");
        HttpClient mine = Client(factory.AccessToken(KgsmTier.Viewer));
        await mine.PostAsJsonAsync("/api/v1/push/subscriptions", Subscription(mineEndpoint));

        HttpClient theirs = Client(OtherUserToken());
        JsonElement list = await theirs.GetFromJsonAsync<JsonElement>("/api/v1/push/subscriptions");

        Assert.DoesNotContain(list.GetProperty("devices").EnumerateArray(),
            d => d.GetProperty("id").GetString() == Fingerprint(mineEndpoint));
    }

    [Fact]
    public async Task One_user_cannot_revoke_anothers_device()
    {
        string mineEndpoint = "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N");
        HttpClient mine = Client(factory.AccessToken(KgsmTier.Viewer));
        await mine.PostAsJsonAsync("/api/v1/push/subscriptions", Subscription(mineEndpoint));

        HttpClient theirs = Client(OtherUserToken());
        HttpResponseMessage res = await theirs.DeleteAsync(
            "/api/v1/push/subscriptions?endpoint=" + Uri.EscapeDataString(mineEndpoint));

        // 404, not 403: whether someone else's endpoint exists is not a thing to confirm.
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // And it really is still there.
        JsonElement list = await mine.GetFromJsonAsync<JsonElement>("/api/v1/push/subscriptions");
        Assert.Contains(list.GetProperty("devices").EnumerateArray(),
            d => d.GetProperty("id").GetString() == Fingerprint(mineEndpoint));
    }

    [Fact]
    public async Task A_user_can_revoke_their_own_device()
    {
        string endpoint = "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N");
        HttpClient c = Client(factory.AccessToken(KgsmTier.Viewer));
        await c.PostAsJsonAsync("/api/v1/push/subscriptions", Subscription(endpoint));

        HttpResponseMessage res = await c.DeleteAsync(
            "/api/v1/push/subscriptions?endpoint=" + Uri.EscapeDataString(endpoint));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        JsonElement list = await c.GetFromJsonAsync<JsonElement>("/api/v1/push/subscriptions");
        Assert.DoesNotContain(list.GetProperty("devices").EnumerateArray(),
            d => d.GetProperty("id").GetString() == Fingerprint(endpoint));
    }

    [Theory]
    // Malformed key material is refused at REGISTER time. Accepting it would produce a row that fails
    // to encrypt later, at send time, where nobody is watching.
    [InlineData("not-base64url!!", "AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("BBBB", "AAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_malformed_subscription_is_refused_at_registration(string p256dh, string auth)
    {
        HttpClient c = Client(factory.AccessToken(KgsmTier.Viewer));
        HttpResponseMessage res = await c.PostAsJsonAsync("/api/v1/push/subscriptions", new
        {
            endpoint = "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N"),
            keys = new { p256dh, auth },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_non_https_endpoint_is_refused()
    {
        HttpClient c = Client(factory.AccessToken(KgsmTier.Viewer));
        HttpResponseMessage res = await c.PostAsJsonAsync("/api/v1/push/subscriptions",
            Subscription("http://fcm.googleapis.com/fcm/send/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Push_needs_a_session()
    {
        HttpClient anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/push/subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/push/key")).StatusCode);
    }

    private static string Fingerprint(string endpoint)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(WebPushCrypto.Utf8(endpoint));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }
}
