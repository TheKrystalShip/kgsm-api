using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Players;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// WS delivery for the permanent player roster (player-presence-contract.md §5), in-process
/// against the real pipeline — the same pattern as <c>AuditTests.AuditTopic_DeliversAppend</c>:
/// seed through <see cref="PlayerHistoryService"/> directly (resolved off the running app's DI,
/// same singleton <c>KgsmAuditConsumer</c> would drive) and assert what the real
/// <see cref="StreamHub"/> pushes over a real WebSocket. Covers the <c>players.reset</c> frame's
/// gate — only published when the reset actually had players, never for an already-empty roster.
/// </summary>
public sealed class PlayerRosterWsTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private PlayerHistoryService History => factory.Services.GetRequiredService<PlayerHistoryService>();

    [Fact]
    public async Task Reset_NonEmptyRoster_PublishesPlayersReset_WithServerId()
    {
        string serverId = $"ws-reset-{Guid.NewGuid():N}";
        string token = factory.AccessToken(AuthTier.Viewer);
        WebSocketClient wsClient = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, $"api/v1/stream?access_token={token}");
        using WebSocket socket = await wsClient.ConnectAsync(uri, CancellationToken.None);

        await Send(socket, """{"type":"subscribe","topics":["players"]}""");
        History.Join(serverId, "sess-1", "id-1", "Heisen", null, DateTimeOffset.UtcNow);

        // Reset repeatedly across the read window so the subscription is certainly live before the reset
        // we observe (no ack protocol; mirrors AuditTests' race-free polling pattern — no sleeps).
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        string? frame = null;
        while (DateTime.UtcNow < deadline)
        {
            History.Join(serverId, "sess-1", "id-1", "Heisen", null, DateTimeOffset.UtcNow); // re-arm if consumed
            History.Reset(serverId);
            string? got = await Receive(socket, TimeSpan.FromMilliseconds(400));
            if (got is not null && got.Contains("\"type\":\"players.reset\""))
            {
                frame = got;
                break;
            }
        }

        Assert.NotNull(frame);
        JsonElement env = JsonDocument.Parse(frame!).RootElement;
        Assert.Equal("players", env.GetProperty("topic").GetString());
        Assert.Equal("players.reset", env.GetProperty("type").GetString());
        Assert.Equal(serverId, env.GetProperty("data").GetProperty("serverId").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Reset_AlreadyEmptyRoster_PublishesNothing()
    {
        string serverId = $"ws-reset-empty-{Guid.NewGuid():N}";
        string token = factory.AccessToken(AuthTier.Viewer);
        WebSocketClient wsClient = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, $"api/v1/stream?access_token={token}");
        using WebSocket socket = await wsClient.ConnectAsync(uri, CancellationToken.None);

        await Send(socket, """{"type":"subscribe","topics":["players"]}""");

        // Give the subscribe a moment to land, then reset a server whose roster was never populated.
        await Task.Delay(200);
        History.Reset(serverId);

        // A short bounded wait proving silence, not a race — if a (buggy) frame were published it would
        // arrive well within this window since there is nothing else competing for the socket.
        string? got = await Receive(socket, TimeSpan.FromSeconds(1));
        Assert.Null(got);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private static Task Send(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string?> Receive(WebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buf = new byte[16384];
        var sb = new StringBuilder();
        try
        {
            WebSocketReceiveResult r;
            do
            {
                r = await socket.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            } while (!r.EndOfMessage);
            return sb.ToString();
        }
        catch (OperationCanceledException) { return null; }
    }
}
