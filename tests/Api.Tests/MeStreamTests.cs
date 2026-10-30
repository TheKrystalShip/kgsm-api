using System.Net;
using System.Text.Json;

using TheKrystalShip.Api.Realtime;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The live connection end to end, through the real pipeline: what a person's open stream is sent follows
/// the actions their token listed, a frame addressed to a person reaches only their connections, and the
/// stream ends when the token that opened it expires.
/// </summary>
/// <remarks>
/// Every case here uses a person of its own (<see cref="TestIdentity.PersonFor"/>), because the question is
/// who a frame reaches.
/// </remarks>
public sealed class MeStreamTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static bool IsMePatch(JsonElement frame) =>
        frame.GetProperty("topic").GetString() == StreamProtocol.MeTopic
        && frame.GetProperty("type").GetString() == StreamProtocol.MePatch;

    private StreamHub Hub => (StreamHub)factory.Services.GetService(typeof(StreamHub))!;

    [Fact]
    public async Task AFrameAddressedToAPersonReachesTheirOpenStream()
    {
        TestPerson watcher = TestIdentity.PersonFor("me-stream-watcher");

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me", factory.AccessTokenFor(watcher, Persona.None));
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        // Keep producing across the deadline: the connection registers with the hub as the response
        // starts, and a publish that lands before it is simply not addressed to anybody yet.
        using var cts = new CancellationTokenSource(Deadline);
        Task<JsonElement?> arrived = frames.WaitForFrame(IsMePatch, Deadline);
        while (!arrived.IsCompleted && !cts.IsCancellationRequested)
        {
            Hub.PublishToAccount(watcher.Subject, StreamProtocol.MeTopic, StreamProtocol.MeEntityKey,
                new StreamMessage(StreamProtocol.MeTopic, StreamProtocol.MePatch, new { status = "active" }));
            await Task.WhenAny(arrived, Task.Delay(50, cts.Token));
        }

        Assert.NotNull(await arrived);
    }

    [Fact]
    public async Task AFrameAddressedToAPersonDoesNotReachAnotherPersonsStream()
    {
        TestPerson owner = TestIdentity.PersonFor("me-stream-owner");
        TestPerson other = TestIdentity.PersonFor("me-stream-other");

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me", factory.AccessTokenFor(other, Persona.None));
        using SseFrameReader frames = await SseTestHelpers.Frames(stream);

        Hub.PublishToAccount(owner.Subject, StreamProtocol.MeTopic, StreamProtocol.MeEntityKey,
            new StreamMessage(StreamProtocol.MeTopic, StreamProtocol.MePatch, new { status = "active" }));

        Assert.Null(await frames.WaitForFrame(IsMePatch, TimeSpan.FromMilliseconds(400)));
    }

    /// <summary>
    /// The stream is a request that outlives the framework's one authorization check, so it ends with the
    /// token that opened it: the client reconnects with a fresh one, which is where a changed role lands.
    /// </summary>
    [Fact]
    public async Task TheStreamEndsWhenTheTokenThatOpenedItExpires()
    {
        string token = factory.AccessTokenWith(
            TestIdentity.PersonFor("me-stream-expiry"), [], lifetime: TimeSpan.FromSeconds(2));

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=me", token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);

        using var cts = new CancellationTokenSource(Deadline);
        await using Stream body = await stream.Content.ReadAsStreamAsync(cts.Token);
        await body.CopyToAsync(Stream.Null, cts.Token);

        Assert.False(cts.IsCancellationRequested, "the stream outlived its token");
    }
}
