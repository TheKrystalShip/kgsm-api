using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using TheKrystalShip.Api;

using TheKrystalShip.Api.Realtime;

using TheKrystalShip.Auth;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The <c>me</c> topic and the live connection end to end, through the real pipeline: somebody's access
/// changes at the auth anchor, replication delivers it to this node's replica, the node is told, and that
/// person's open stream hears it on the connection it already holds.
/// </summary>
/// <remarks>
/// Every case here uses an identity of its own (<see cref="TestIdentity.IdentityFor"/>), because
/// the question is who a frame reaches — and the suite's standing identity is one account that every
/// call site re-assigns, which would make "reached the right person" unfalsifiable.
/// </remarks>
public sealed class MeStreamTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private static bool IsMePatch(JsonElement frame) =>
        frame.GetProperty("topic").GetString() == StreamProtocol.MeTopic
        && frame.GetProperty("type").GetString() == StreamProtocol.MePatch;

    private StreamHub Hub => (StreamHub)factory.Services.GetService(typeof(StreamHub))!;

    /// <summary>
    /// The feature: an approval at the anchor lands on the affected person's open panel, with no reload
    /// and no poll. The frame carries the vocabulary <c>GET /me</c> answers in, so the client merges it
    /// over what it hydrated.
    /// </summary>
    [Fact]
    public async Task AnApprovalReachesTheAffectedAccountsOpenStream()
    {
        KgsmIdentity watcher = TestIdentity.IdentityFor("me-stream-watcher");
        string watcherToken = factory.AccessTokenFor(watcher, Persona.None, UserStatus.Pending);

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me", watcherToken);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        factory.SetAccount(watcher, Persona.Reader, UserStatus.Active);

        JsonElement? frame = await frames.WaitForFrame(IsMePatch, Deadline);
        Assert.NotNull(frame);
        JsonElement data = frame!.Value.GetProperty("data");
        Assert.Equal(UserStatuses.Active, data.GetProperty("status").GetString());
        Assert.False(data.TryGetProperty("tier", out _));
    }

    /// <summary>
    /// Per-account delivery, not a broadcast. Somebody else's account is not news anybody else's panel
    /// is entitled to — a topic named for the reader that carried other people's changes would be a
    /// directory of who holds what, handed to every reader on the host.
    /// </summary>
    [Fact]
    public async Task AnApprovalReachesNobodyElsesStream()
    {
        KgsmIdentity subject = TestIdentity.IdentityFor("me-stream-subject");
        KgsmIdentity bystander = TestIdentity.IdentityFor("me-stream-bystander");
        factory.AccessTokenFor(subject, Persona.None, UserStatus.Pending);
        string bystanderToken = factory.AccessTokenFor(bystander, Persona.Reader);

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me", bystanderToken);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        factory.SetAccount(subject, Persona.Reader, UserStatus.Active);

        // Prove silence: one bounded wait with nothing matching. A frame for somebody else would have
        // been enqueued by the time the change was delivered.
        Assert.Null(await frames.WaitForFrame(IsMePatch, TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// The stream's gate is per topic. A caller holding nothing keeps only the topic that needs nothing;
    /// the rest of what they asked for delivers nothing, silently.
    /// </summary>
    [Fact]
    public async Task ACallerHoldingNothingIsSentNothingButTheirOwnStanding()
    {
        KgsmIdentity pending = TestIdentity.IdentityFor("me-stream-gated");
        string token = factory.AccessTokenFor(pending, Persona.None, UserStatus.Pending);

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=servers,audit", token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);

        using SseFrameReader frames = await SseTestHelpers.Frames(stream);
        Assert.Null(await frames.WaitForFrame(_ => true, TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Access taken away applies to the live connection, not only to the client drawing it: the topic
    /// the reader no longer reaches stops counting as a subscriber on the connection they already hold.
    /// </summary>
    [Fact]
    public async Task LosingAnActionStopsItsTopicOnTheLiveConnection()
    {
        KgsmIdentity op = TestIdentity.IdentityFor("me-stream-operator");
        string opToken = factory.AccessTokenFor(op, Persona.Runner);

        string logs = StreamProtocol.HostLogsTopic(AuthTestFactory.HostId);
        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), $"/api/v1/stream?topics=me,{logs}", opToken);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);

        Assert.True(Hub.HasSubscribers(logs), "the operator's subscription never reached the hub");

        factory.SetAccount(op, Persona.Reader);

        Assert.False(Hub.HasSubscribers(logs), "a reader who lost api:logs.read kept receiving the topic");
    }

    /// <summary>
    /// Access granted applies the same way: the topic the client asked for starts delivering on the
    /// connection it already holds.
    /// </summary>
    [Fact]
    public async Task GainingAnActionStartsItsTopicOnTheLiveConnection()
    {
        KgsmIdentity viewer = TestIdentity.IdentityFor("me-stream-promoted");
        string token = factory.AccessTokenFor(viewer, Persona.Reader);

        string services = StreamProtocol.HostServicesTopic(AuthTestFactory.HostId);
        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), $"/api/v1/stream?topics=me,{services}", token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.False(Hub.HasSubscribers(services));

        factory.SetAccount(viewer, Persona.Runner);

        Assert.True(Hub.HasSubscribers(services), "a reader granted api:services.read was not given the topic");
    }

    /// <summary>
    /// An account removed at the anchor holds nothing, on the connection it already has: its topics stop,
    /// and it is told it is no longer known here, the same answer <c>GET /me</c> gives for it.
    /// </summary>
    [Fact]
    public async Task ARemovedAccountLosesItsReachOnTheLiveConnection()
    {
        KgsmIdentity gone = TestIdentity.IdentityFor("me-stream-removed");
        string token = factory.AccessTokenFor(gone, Persona.Runner);

        string logs = StreamProtocol.HostLogsTopic(AuthTestFactory.HostId);
        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), $"/api/v1/stream?topics=me,{logs}", token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        Assert.True(Hub.HasSubscribers(logs), "the operator's subscription never reached the hub");

        AuthTestFactory.RemoveAccountOn(factory.Services, gone);

        JsonElement? frame = await frames.WaitForFrame(IsMePatch, Deadline);
        Assert.NotNull(frame);
        Assert.Equal(StreamAccess.UnknownStatus, frame!.Value.GetProperty("data").GetProperty("status").GetString());
        Assert.False(Hub.HasSubscribers(logs), "a removed account kept receiving a topic");
    }

    /// <summary>
    /// The dev escape hatch: an auth-disabled host authenticates every caller as a synthetic Owner, which
    /// every topic admits — and nothing re-reads an account for it, because the subject it names was never
    /// given one and asking would answer "stranger".
    /// </summary>
    [Fact]
    public async Task AnAuthDisabledHostStreamsAsTheSyntheticOwner()
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
        Assert.True(hub.HasSubscribers(logs), "the synthetic Owner lost a topic");
    }
}
