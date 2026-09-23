using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using TheKrystalShip.Api;

using TheKrystalShip.Api.Realtime;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Cluster.Messaging;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The <c>me</c> topic end to end, through the real pipeline: an admin changes what somebody may do at
/// the auth anchor, replication delivers it, and that person's open stream hears it on the connection
/// it already holds.
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

    /// <summary>
    /// What an admin's change at the auth anchor arrives here as: an <c>account.changed</c> message,
    /// handed to the handler this node registers for it exactly as the cluster bus would.
    /// </summary>
    private async Task Retier(string userId, KgsmTier tier, UserStatus? status = null)
    {
        SqliteUserStore replica = AuthTestFactory.ReplicaOf(factory.Services);
        KgsmUser account = (await replica.FindByIdAsync(userId))!;
        KgsmUser changed = account with
        {
            Tier = tier,
            TierSource = TierSource.Granted,
            Status = status ?? account.Status,
            Updated = DateTimeOffset.UtcNow,
        };
        var change = new AccountChange(
            ReplicatedAccount.From(changed, await replica.ListCredentialsAsync(userId)),
            DateTimeOffset.UtcNow.UtcTicks);

        var envelope = new ClusterEnvelope(
            Guid.NewGuid().ToString("N"), "account.changed", "test-anchor", DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(change, AccountReplicationJson.Default.AccountChange));

        IClusterMessageHandler handler = factory.Services.GetServices<IClusterMessageHandler>()
            .Single(h => h.Type == "account.changed");
        await handler.HandleAsync(envelope, CancellationToken.None);
    }

    /// <summary>
    /// The feature: a tier changed at the anchor lands on the affected person's open panel, with no
    /// reload and no poll. The frame carries the wire vocabulary <c>GET /me</c> answers in, so the
    /// client merges it over what it hydrated.
    /// </summary>
    [Fact]
    public async Task ARetierReachesTheAffectedAccountsOpenStream()
    {
        KgsmIdentity watcher = FakeDiscordResolver.IdentityFor("me-stream-watcher");
        string watcherToken = factory.AccessTokenFor(watcher, KgsmTier.Viewer);
        string watcherId = factory.AccountOf(watcher)!.UserId;

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me", watcherToken);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        await Retier(watcherId, KgsmTier.Operator);

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
        factory.AccessTokenFor(subject, KgsmTier.Viewer);
        string bystanderToken = factory.AccessTokenFor(bystander, KgsmTier.Viewer);
        string subjectId = factory.AccountOf(subject)!.UserId;

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me", bystanderToken);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        await Retier(subjectId, KgsmTier.Operator);

        // Prove silence, the way the operator-topic drop is proven: one bounded wait with nothing
        // matching. A frame for somebody else would have been enqueued by the time the handler returned.
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
        string pendingToken = factory.AccessTokenFor(pending, KgsmTier.None, UserStatus.Pending);
        string pendingId = factory.AccountOf(pending)!.UserId;

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me,servers", pendingToken);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        await Retier(pendingId, KgsmTier.Viewer, UserStatus.Active);

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
        string opToken = factory.AccessTokenFor(op, KgsmTier.Operator);
        string opId = factory.AccountOf(op)!.UserId;

        string logs = StreamProtocol.HostLogsTopic(AuthTestFactory.HostId);
        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), $"/api/v1/stream?topics=me,{logs}", opToken);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        var hub = (StreamHub)factory.Services.GetService(typeof(StreamHub))!;
        Assert.True(hub.HasSubscribers(logs), "the operator's subscription never reached the hub");

        await Retier(opId, KgsmTier.Viewer);

        JsonElement? frame = await frames.WaitForFrame(IsMePatch, Deadline);
        Assert.NotNull(frame);
        Assert.Equal(KgsmTiers.Viewer, frame!.Value.GetProperty("data").GetProperty("tier").GetString());
        Assert.False(hub.HasSubscribers(logs), "a demoted reader kept an operator-only subscription");
    }

    /// <summary>
    /// An account removed at the anchor holds nothing, on the connection it already has: re-gated to
    /// nothing and told it is no longer known here, the same answer <c>GET /me</c> gives for it.
    /// </summary>
    [Fact]
    public async Task ARemovedAccountLosesItsReachOnTheLiveConnection()
    {
        KgsmIdentity gone = FakeDiscordResolver.IdentityFor("me-stream-removed");
        string token = factory.AccessTokenFor(gone, KgsmTier.Operator);
        string goneId = factory.AccountOf(gone)!.UserId;

        string logs = StreamProtocol.HostLogsTopic(AuthTestFactory.HostId);
        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), $"/api/v1/stream?topics=me,{logs}", token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        var hub = (StreamHub)factory.Services.GetService(typeof(StreamHub))!;
        Assert.True(hub.HasSubscribers(logs), "the operator's subscription never reached the hub");

        var envelope = new ClusterEnvelope(
            Guid.NewGuid().ToString("N"), "account.removed", "test-anchor", DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(
                new AccountRemoval(goneId, DateTimeOffset.UtcNow.UtcTicks),
                AccountReplicationJson.Default.AccountRemoval));
        await factory.Services.GetServices<IClusterMessageHandler>()
            .Single(h => h.Type == "account.removed")
            .HandleAsync(envelope, CancellationToken.None);

        JsonElement? frame = await frames.WaitForFrame(IsMePatch, Deadline);
        Assert.NotNull(frame);
        JsonElement data = frame!.Value.GetProperty("data");
        Assert.Equal(KgsmTiers.None, data.GetProperty("tier").GetString());
        Assert.Equal("unknown", data.GetProperty("status").GetString());
        Assert.False(hub.HasSubscribers(logs), "a removed account kept an operator-only subscription");
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
