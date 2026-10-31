using System.Net;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The stream is a request that outlives the framework's one authorization check, so it ends with the
/// token that opened it: the client reconnects with a fresh one, which is where a changed role lands.
/// </summary>
public sealed class StreamExpiryTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TheStreamEndsWhenTheTokenThatOpenedItExpires()
    {
        string token = factory.AccessTokenWith(
            TestIdentity.PersonFor("stream-expiry"), [], lifetime: TimeSpan.FromSeconds(2));

        using HttpResponseMessage stream = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=servers", token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);

        using var cts = new CancellationTokenSource(Deadline);
        await using Stream body = await stream.Content.ReadAsStreamAsync(cts.Token);
        await body.CopyToAsync(Stream.Null, cts.Token);

        Assert.False(cts.IsCancellationRequested, "the stream outlived its token");
    }
}
