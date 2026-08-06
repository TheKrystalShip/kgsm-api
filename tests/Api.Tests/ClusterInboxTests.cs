using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;

using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Phase 2 of the cluster message bus (<c>docs/cluster-message-bus-plan.md §4/§7</c>) — the
/// <c>POST /api/v1/peers/inbox</c> receive path: fail-closed cluster-token auth, the <c>from</c>-spoof
/// guard, dedupe, and dispatch to the first registered handler, <c>session.revoke</c>. Boots the real
/// app (via <see cref="AuthTestFactory"/>'s shared fixture, re-configured with a known
/// <c>Api__ClusterSecret</c>/<c>Api__NodeId</c> the same way <c>SessionCleanupTests</c>
/// layers config onto the base factory) so a real, validly-signed cluster service token can be minted
/// through the running pipeline's own <see cref="IClusterTokenService"/> — the same "mint through the
/// server's own service" posture <see cref="AuthTestFactory.AccessToken"/> uses for user tokens.
/// </summary>
public sealed class ClusterInboxTests : IClassFixture<AuthTestFactory>
{
    private const string ClusterSecret = "test-cluster-secret-please-ignore";
    private const string NodeId = "node-a";

    private readonly WebApplicationFactory<Program> _app;
    private readonly HttpClient _client;

    public ClusterInboxTests(AuthTestFactory factory)
    {
        // Layer the cluster config onto the shared base factory's own (ConfigureAppConfiguration calls
        // stack — later wins), same pattern as SessionCleanupTests' derived-factory config override.
        _app = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:ClusterSecret"] = ClusterSecret,
                ["Api:NodeId"] = NodeId,
            })));
        _client = _app.CreateClient();
    }

    private IClusterTokenService Tokens => _app.Services.GetRequiredService<IClusterTokenService>();
    private SessionStore Store => _app.Services.GetRequiredService<SessionStore>();
    private ISessionValidator Validator => _app.Services.GetRequiredService<ISessionValidator>();

    private static string EnvelopeJson(string id, string type, string from, object payload) =>
        JsonSerializer.Serialize(new { id, type, from, ts = DateTimeOffset.UtcNow, payload });

    private Task<HttpResponseMessage> PostInbox(string? bearer, string jsonBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/peers/inbox")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    private async Task<string> SeedSessionAsync(string sid)
    {
        await Store.CreateAsync(sid, "discord:cluster-test-user", AuthTestFactory.HostId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30),
            userAgent: null, initialJti: null, CancellationToken.None);
        return sid;
    }

    // ── 1. Valid token + session.revoke (scope sid) ────────────────────────────────────────────

    [Fact]
    public async Task ValidToken_SessionRevokeBySid_RevokesRow_AndEvictsCache()
    {
        string sid = await SeedSessionAsync("sid_cluster_" + Guid.NewGuid().ToString("N"));
        // Warm the validator's cache to `true` first — proves the subsequent `false` comes from an
        // actual Evict, not merely a fresh DB read that happened to already reflect the revoke.
        Assert.True(await Validator.IsValidAsync(sid, CancellationToken.None));

        MintedClusterToken minted = Tokens.Mint();
        string body = EnvelopeJson(Guid.NewGuid().ToString("N"), "session.revoke", NodeId, new { scope = "sid", sid });

        HttpResponseMessage resp = await PostInbox(minted.Token, body);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("accepted", (await Json(resp)).GetProperty("status").GetString());

        SessionEntry? row = await Store.GetByIdAsync(sid, CancellationToken.None);
        Assert.NotNull(row);
        Assert.True(row!.Revoked);
        Assert.False(await Validator.IsValidAsync(sid, CancellationToken.None));
    }

    // ── 2. No Authorization header ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoBearer_401_InvalidClusterToken()
    {
        string body = EnvelopeJson(Guid.NewGuid().ToString("N"), "session.revoke", NodeId,
            new { scope = "sid", sid = "does-not-matter" });

        HttpResponseMessage resp = await PostInbox(null, body);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        JsonElement error = (await Json(resp)).GetProperty("error");
        Assert.Equal("invalid_cluster_token", error.GetProperty("code").GetString());
    }

    // ── 3. Token minted with a WRONG secret ─────────────────────────────────────────────────────

    [Fact]
    public async Task WrongSecretToken_401_InvalidClusterToken()
    {
        IConfiguration wrongConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:HostId"] = "node-imposter",
                ["Api:ClusterSecret"] = "a-totally-different-secret",
                ["Api:NodeId"] = NodeId,
            })
            .Build();
        var wrongTokens = new ClusterTokenService(
            ApiOptions.FromConfiguration(wrongConfig), NullLogger<ClusterTokenService>.Instance);
        MintedClusterToken wrongMinted = wrongTokens.Mint();

        string body = EnvelopeJson(Guid.NewGuid().ToString("N"), "session.revoke", NodeId,
            new { scope = "sid", sid = "does-not-matter" });

        HttpResponseMessage resp = await PostInbox(wrongMinted.Token, body);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("invalid_cluster_token", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    // ── 4. `from` != token node id ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task FromMismatch_403()
    {
        MintedClusterToken minted = Tokens.Mint(); // iss = NodeId ("node-a")
        string body = EnvelopeJson(Guid.NewGuid().ToString("N"), "session.revoke", "node-b-impersonated",
            new { scope = "sid", sid = "does-not-matter" });

        HttpResponseMessage resp = await PostInbox(minted.Token, body);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("from_mismatch", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
    }

    // ── 5. Duplicate delivery (same envelope id twice) ──────────────────────────────────────────

    [Fact]
    public async Task DuplicateDelivery_BothAck200_AppliedOnce()
    {
        string sid = await SeedSessionAsync("sid_cluster_dup_" + Guid.NewGuid().ToString("N"));
        string envelopeId = Guid.NewGuid().ToString("N");
        MintedClusterToken minted = Tokens.Mint();
        string body = EnvelopeJson(envelopeId, "session.revoke", NodeId, new { scope = "sid", sid });

        HttpResponseMessage first = await PostInbox(minted.Token, body);
        HttpResponseMessage second = await PostInbox(minted.Token, body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("accepted", (await Json(first)).GetProperty("status").GetString());
        Assert.Equal("accepted", (await Json(second)).GetProperty("status").GetString());

        // Applied exactly once — one ledger row for this envelope id, and the session is (still, only
        // ever) revoked — a second run would have been harmless anyway (idempotent), but the ledger
        // proves the handler was not re-dispatched.
        using IServiceScope scope = _app.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int rowCount = db.ClusterInbox.Count(i => i.Id == envelopeId);
        Assert.Equal(1, rowCount);

        SessionEntry? row = await Store.GetByIdAsync(sid, CancellationToken.None);
        Assert.True(row!.Revoked);
    }

    // ── 6. Unknown type ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownType_200_Dropped_NoServerError()
    {
        MintedClusterToken minted = Tokens.Mint();
        string body = EnvelopeJson(Guid.NewGuid().ToString("N"), "some.future.type", NodeId, new { whatever = true });

        HttpResponseMessage resp = await PostInbox(minted.Token, body);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("accepted", (await Json(resp)).GetProperty("status").GetString());
    }
}
