using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using TheKrystalShip.Api;

using TheKrystalShip.Api.Realtime;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The <c>me</c> topic end to end, through the real pipeline: an admin changes what somebody may do,
/// and that person's open stream hears it on the connection it already holds.
/// </summary>
/// <remarks>
/// Every case here uses an identity of its own (<see cref="FakeDiscordResolver.IdentityFor"/>), because
/// the question is who a frame reaches — and the suite's standing identity is one account that every
/// call site re-tiers, which would make "reached the right person" unfalsifiable.
/// </remarks>
public sealed class MeStreamTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private HttpClient Bearer(string token)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static bool IsMePatch(JsonElement frame) =>
        frame.GetProperty("topic").GetString() == StreamProtocol.MeTopic
        && frame.GetProperty("type").GetString() == StreamProtocol.MePatch;

    private Task<HttpResponseMessage> Retier(string token, string userId, string body) =>
        Bearer(token).PatchAsync($"/auth/users/{userId}",
            new StringContent(body, Encoding.UTF8, "application/json"));

    /// <summary>
    /// The feature: a tier changed in the Users tab lands on the affected person's open panel, with no
    /// reload and no poll. The frame carries the wire vocabulary <c>GET /me</c> answers in, so the
    /// client merges it over what it hydrated.
    /// </summary>
    [Fact]
    public async Task ARetierReachesTheAffectedAccountsOpenStream()
    {
        KgsmIdentity watcher = FakeDiscordResolver.IdentityFor("me-stream-watcher");
        KgsmIdentity admin = FakeDiscordResolver.IdentityFor("me-stream-admin");
        string watcherToken = factory.AccessTokenFor(watcher, KgsmTier.Viewer);
        string adminToken = factory.AccessTokenFor(admin, KgsmTier.Admin);
        string watcherId = factory.AccountOf(watcher)!.UserId;

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me", watcherToken);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        using HttpResponseMessage patch = await Retier(adminToken, watcherId, """{"tier":"operator"}""");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        JsonElement? frame = await frames.WaitForFrame(IsMePatch, Deadline);
        Assert.NotNull(frame);
        JsonElement data = frame!.Value.GetProperty("data");
        Assert.Equal(KgsmTiers.Operator, data.GetProperty("tier").GetString());
        Assert.Equal(UserStatuses.Active, data.GetProperty("status").GetString());
    }

    /// <summary>
    /// Per-user delivery, not a broadcast. Somebody else's tier is not news anybody else's panel is
    /// entitled to — and a topic named for the reader that carried other people's account changes
    /// would be a directory of who holds what, handed to every viewer on the host.
    /// </summary>
    [Fact]
    public async Task ARetierReachesNobodyElsesStream()
    {
        KgsmIdentity subject = FakeDiscordResolver.IdentityFor("me-stream-subject");
        KgsmIdentity bystander = FakeDiscordResolver.IdentityFor("me-stream-bystander");
        KgsmIdentity admin = FakeDiscordResolver.IdentityFor("me-stream-admin2");
        factory.AccessTokenFor(subject, KgsmTier.Viewer);
        string bystanderToken = factory.AccessTokenFor(bystander, KgsmTier.Viewer);
        string adminToken = factory.AccessTokenFor(admin, KgsmTier.Admin);
        string subjectId = factory.AccountOf(subject)!.UserId;

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me", bystanderToken);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        using HttpResponseMessage patch = await Retier(adminToken, subjectId, """{"tier":"operator"}""");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        // Prove silence, the way the operator-topic drop is proven: one bounded wait with nothing
        // matching. A frame for somebody else would have been enqueued by the time the PATCH returned.
        Assert.Null(await frames.WaitForFrame(IsMePatch, TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// The person with the least standing on the host is exactly the one who needs this. Somebody
    /// awaiting approval holds nothing, connects for news about themselves alone, and hears the
    /// approval on that connection instead of reloading until an admin gets to them.
    /// </summary>
    [Fact]
    public async Task APendingCallerStreamsForItsOwnStandingAndHearsTheApproval()
    {
        KgsmIdentity pending = FakeDiscordResolver.IdentityFor("me-stream-pending");
        KgsmIdentity admin = FakeDiscordResolver.IdentityFor("me-stream-admin3");
        string pendingToken = factory.AccessTokenFor(pending, KgsmTier.None, UserStatus.Pending);
        string adminToken = factory.AccessTokenFor(admin, KgsmTier.Admin);
        string pendingId = factory.AccountOf(pending)!.UserId;

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me,servers", pendingToken);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        using HttpResponseMessage patch = await Retier(
            adminToken, pendingId, """{"tier":"viewer","status":"active"}""");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        JsonElement? frame = await frames.WaitForFrame(IsMePatch, Deadline);
        Assert.NotNull(frame);
        JsonElement data = frame!.Value.GetProperty("data");
        Assert.Equal(KgsmTiers.Viewer, data.GetProperty("tier").GetString());
        Assert.Equal(UserStatuses.Active, data.GetProperty("status").GetString());
    }

    /// <summary>
    /// The stream's gate is per topic. A caller holding nothing keeps only the topic that needs
    /// nothing — the rest of what they asked for is dropped at connect, silently, exactly as an
    /// operator-only topic is for a viewer.
    /// </summary>
    [Fact]
    public async Task ACallerHoldingNothingKeepsOnlyTheTopicThatNeedsNothing()
    {
        KgsmIdentity pending = FakeDiscordResolver.IdentityFor("me-stream-gated");
        string token = factory.AccessTokenFor(pending, KgsmTier.None, UserStatus.Pending);

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=servers,audit", token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);

        using SseFrameReader frames = await SseTestHelpers.Frames(stream);
        Assert.Null(await frames.WaitForFrame(_ => true, TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// A demotion re-gates the live connection, not only the client drawing it. The operator-only
    /// topic leaves the subscription set on the connection the reader already holds, so the window
    /// between the change and their next reconnect is not a window in which they still receive it.
    /// </summary>
    [Fact]
    public async Task ADemotionStripsAnOperatorTopicFromTheLiveConnection()
    {
        KgsmIdentity op = FakeDiscordResolver.IdentityFor("me-stream-operator");
        KgsmIdentity admin = FakeDiscordResolver.IdentityFor("me-stream-admin4");
        string opToken = factory.AccessTokenFor(op, KgsmTier.Operator);
        string adminToken = factory.AccessTokenFor(admin, KgsmTier.Admin);
        string opId = factory.AccountOf(op)!.UserId;

        string logs = StreamProtocol.HostLogsTopic(AuthTestFactory.HostId);
        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), $"/api/v1/stream?topics=me,{logs}", opToken);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        var hub = (StreamHub)factory.Services.GetService(typeof(StreamHub))!;
        Assert.True(hub.HasSubscribers(logs), "the operator's subscription never reached the hub");

        using HttpResponseMessage patch = await Retier(adminToken, opId, """{"tier":"viewer"}""");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        JsonElement? frame = await frames.WaitForFrame(IsMePatch, Deadline);
        Assert.NotNull(frame);
        Assert.Equal(KgsmTiers.Viewer, frame!.Value.GetProperty("data").GetProperty("tier").GetString());
        Assert.False(hub.HasSubscribers(logs), "a demoted reader kept an operator-only subscription");
    }

    /// <summary>
    /// The dev escape hatch is untouched. An auth-disabled host authenticates every caller as a
    /// synthetic admin, which the per-topic gate admits everywhere — and nothing re-reads an account
    /// for it, because the subject it names was never given one and asking would answer "stranger".
    /// </summary>
    [Fact]
    public async Task AnAuthDisabledHostStreamsAsTheSyntheticAdmin()
    {
        using WebApplicationFactory<Program> open = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:AuthDisabled"] = "true",
                    ["Api:DisabledAuthActor"] = "local:claude",
                    ["Api:DbPath"] = AuthTestFactory.NewDbPath("kgsm-api-tests-open"),
                })));

        string logs = StreamProtocol.HostLogsTopic(AuthTestFactory.HostId);
        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            open.CreateClient(), $"/api/v1/stream?topics=me,{logs}");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);

        var hub = (StreamHub)open.Services.GetService(typeof(StreamHub))!;
        Assert.True(hub.HasSubscribers(logs), "the synthetic admin lost an operator-only topic");
    }
}
