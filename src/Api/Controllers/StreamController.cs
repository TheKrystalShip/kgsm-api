using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;
using TheKrystalShip.Api.Infrastructure;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The per-host realtime endpoint (M2) — <c>GET /api/v1/stream</c> upgraded to a WebSocket. One socket
/// per host multiplexes that host's topics (<c>architecture.html §3·b</c>); the client subscribes to the
/// topics the active page needs and the pumps push <c>{ topic, type, data }</c> envelopes. The action
/// holds the request for the socket's lifetime, registering the connection with the <see cref="StreamHub"/>
/// for the pumps to fan out to, and unregistering on disconnect.
/// </summary>
[ApiController]
[Route("api/v1/stream")]
[Authorize(Policy = AuthPolicy.Viewer)] // a read surface — viewer and up; the WS bearer rides ?access_token= (M4·a)
public sealed class StreamController(
    StreamHub hub,
    IHostApplicationLifetime lifetime,
    ILogger<StreamController> logger) : ControllerBase
{
    [HttpGet]
    public async Task Get()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            // A plain GET here is a client error; emit the frozen { error } envelope, not an upgrade.
            await ApiErrors.WriteAsync(HttpContext, StatusCodes.Status400BadRequest, "bad_request",
                "The /stream endpoint requires a WebSocket upgrade.");
            return;
        }

        using WebSocket socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var connection = new StreamConnection(socket, hub.Json, logger);
        hub.Add(connection);
        try
        {
            // Tear down on app shutdown or client disconnect, whichever comes first.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.ApplicationStopping, HttpContext.RequestAborted);
            await connection.RunAsync(linked.Token);
        }
        finally
        {
            hub.Remove(connection);
        }
    }
}
