using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Players;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// SSE delivery for the permanent player roster (player-presence-contract.md §5), in-process
/// against the real pipeline — the same pattern as <c>AuditTests.AuditTopic_DeliversAppend</c>:
/// seed through <see cref="PlayerHistoryService"/> directly (resolved off the running app's DI,
/// same singleton <c>KgsmAuditConsumer</c> would drive) and assert what the real
/// <see cref="StreamHub"/> pushes over the SSE stream. Covers the <c>players.reset</c> frame's
/// gate — only published when the reset actually had players, never for an already-empty roster.
/// </summary>
public sealed class PlayerRosterStreamTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private PlayerHistoryService History => factory.Services.GetRequiredService<PlayerHistoryService>();

    [Fact]
    public async Task Reset_NonEmptyRoster_PublishesPlayersReset_WithServerId()
    {
        string serverId = $"sse-reset-{Guid.NewGuid():N}";
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=players", factory.AccessToken(AuthTier.Viewer));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(resp);

        History.Join(serverId, "sess-1", "id-1", "Heisen", null, DateTimeOffset.UtcNow);

        // Reset repeatedly across the read window so the subscription is certainly live before the reset
        // we observe (no ack protocol; mirrors AuditTests' race-free polling pattern — no sleeps).
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        JsonElement? frame = null;
        while (DateTime.UtcNow < deadline)
        {
            History.Join(serverId, "sess-1", "id-1", "Heisen", null, DateTimeOffset.UtcNow); // re-arm if consumed
            History.Reset(serverId);
            JsonElement? got = await frames.WaitForFrame(
                f => f.GetProperty("type").GetString() == "players.reset", TimeSpan.FromMilliseconds(400));
            if (got is not null)
            {
                frame = got;
                break;
            }
        }

        Assert.NotNull(frame);
        JsonElement env = frame!.Value;
        Assert.Equal("players", env.GetProperty("topic").GetString());
        Assert.Equal("players.reset", env.GetProperty("type").GetString());
        Assert.Equal(serverId, env.GetProperty("data").GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task Reset_AlreadyEmptyRoster_PublishesNothing()
    {
        string serverId = $"sse-reset-empty-{Guid.NewGuid():N}";
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=players", factory.AccessToken(AuthTier.Viewer));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(resp);

        // Give the connection a moment to land, then reset a server whose roster was never populated.
        await Task.Delay(200);
        History.Reset(serverId);

        // A short bounded wait proving silence, not a race — if a (buggy) frame were published it would
        // arrive well within this window since there is nothing else competing for the stream.
        JsonElement? got = await frames.WaitForFrame(_ => true, TimeSpan.FromSeconds(1));
        Assert.Null(got);
    }
}
