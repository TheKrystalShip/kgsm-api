using Microsoft.AspNetCore.SignalR;
using TheKrystalShip.KGSM.Api.Services;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Api.Hubs;

/// <summary>
/// SignalR hub for real-time log streaming with KGSM library integration
/// </summary>
public class LogStreamingHub : Hub
{
    private readonly ILogStreamingService _logStreamingService;
    private readonly ILogger<LogStreamingHub> _logger;

    public LogStreamingHub(ILogStreamingService logStreamingService, ILogger<LogStreamingHub> logger)
    {
        _logStreamingService = logStreamingService;
        _logger = logger;
    }

    /// <summary>
    /// Subscribe to logs for a specific instance
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <returns>Task representing the operation</returns>
    public async Task SubscribeLogs(string instanceName)
    {
        try
        {
            _logger.LogInformation("Client {ConnectionId} subscribing to logs for instance: {InstanceName}",
                Context.ConnectionId, instanceName);

            await _logStreamingService.StartStreamingAsync(instanceName, Context.ConnectionId);

            _logger.LogDebug("Client {ConnectionId} successfully subscribed to logs for instance: {InstanceName}",
                Context.ConnectionId, instanceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscribing client {ConnectionId} to logs for instance: {InstanceName}",
                Context.ConnectionId, instanceName);

            await Clients.Caller.SendAsync("SubscriptionError", new { instanceName, error = ex.Message });
        }
    }

    /// <summary>
    /// Subscribe to logs for a specific instance with filtering options
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <param name="minimumLogLevel">Minimum log level to receive (optional)</param>
    /// <param name="includeRawLines">Whether to include raw log lines (optional)</param>
    /// <returns>Task representing the operation</returns>
    public async Task SubscribeLogsFiltered(string instanceName, string? minimumLogLevel = null, bool includeRawLines = true)
    {
        try
        {
            _logger.LogInformation("Client {ConnectionId} subscribing to filtered logs for instance: {InstanceName} (MinLevel: {MinLevel}, IncludeRaw: {IncludeRaw})",
                Context.ConnectionId, instanceName, minimumLogLevel ?? "All", includeRawLines);

            // Parse log level if provided
            Core.Models.LogLevel? logLevel = null;
            if (!string.IsNullOrEmpty(minimumLogLevel) && Enum.TryParse<Core.Models.LogLevel>(minimumLogLevel, true, out var parsedLevel))
            {
                logLevel = parsedLevel;
            }

            // For now, start basic streaming - filtering will be handled in future enhancement
            await _logStreamingService.StartStreamingAsync(instanceName, Context.ConnectionId);

            // Store client preferences for filtering (could be enhanced to store in user context)
            await Groups.AddToGroupAsync(Context.ConnectionId, $"logs_{instanceName}");

            if (logLevel.HasValue)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"logs_{instanceName}_{logLevel.Value}");
            }

            _logger.LogDebug("Client {ConnectionId} successfully subscribed to filtered logs for instance: {InstanceName}",
                Context.ConnectionId, instanceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscribing client {ConnectionId} to filtered logs for instance: {InstanceName}",
                Context.ConnectionId, instanceName);

            await Clients.Caller.SendAsync("SubscriptionError", new { instanceName, error = ex.Message });
        }
    }

    /// <summary>
    /// Unsubscribe from logs for a specific instance
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <returns>Task representing the operation</returns>
    public async Task UnsubscribeLogs(string instanceName)
    {
        try
        {
            _logger.LogInformation("Client {ConnectionId} unsubscribing from logs for instance: {InstanceName}",
                Context.ConnectionId, instanceName);

            await _logStreamingService.StopStreamingAsync(instanceName, Context.ConnectionId);

            // Remove from groups
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"logs_{instanceName}");

            // Remove from all log level groups for this instance
            foreach (Core.Models.LogLevel level in Enum.GetValues<Core.Models.LogLevel>())
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"logs_{instanceName}_{level}");
            }

            await Clients.Caller.SendAsync("UnsubscriptionConfirmed", new { instanceName });

            _logger.LogDebug("Client {ConnectionId} successfully unsubscribed from logs for instance: {InstanceName}",
                Context.ConnectionId, instanceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unsubscribing client {ConnectionId} from logs for instance: {InstanceName}",
                Context.ConnectionId, instanceName);

            await Clients.Caller.SendAsync("UnsubscriptionError", new { instanceName, error = ex.Message });
        }
    }

    /// <summary>
    /// Get the current status of log streams
    /// </summary>
    /// <returns>Task representing the operation</returns>
    public async Task GetStreamStatus()
    {
        try
        {
            _logger.LogDebug("Client {ConnectionId} requesting stream status", Context.ConnectionId);

            var status = await _logStreamingService.GetStreamStatusAsync();
            await Clients.Caller.SendAsync("StreamStatus", status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting stream status for client {ConnectionId}", Context.ConnectionId);
            await Clients.Caller.SendAsync("StreamStatusError", new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get buffered logs for an instance
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <returns>Task representing the operation</returns>
    public async Task GetBufferedLogs(string instanceName)
    {
        try
        {
            _logger.LogDebug("Client {ConnectionId} requesting buffered logs for instance: {InstanceName}",
                Context.ConnectionId, instanceName);

            var logs = await _logStreamingService.GetBufferedLogsAsync(instanceName);
            await Clients.Caller.SendAsync("BufferedLogs", new { instanceName, logs });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting buffered logs for client {ConnectionId}, instance: {InstanceName}",
                Context.ConnectionId, instanceName);

            await Clients.Caller.SendAsync("BufferedLogsError", new { instanceName, error = ex.Message });
        }
    }

    /// <summary>
    /// Handle client connection
    /// </summary>
    /// <returns>Task representing the operation</returns>
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client {ConnectionId} connected to log streaming hub", Context.ConnectionId);

        // Send welcome message with available methods
        await Clients.Caller.SendAsync("Connected", new
        {
            connectionId = Context.ConnectionId,
            availableMethods = new[]
            {
                "SubscribeLogs",
                "SubscribeLogsFiltered",
                "UnsubscribeLogs",
                "GetStreamStatus",
                "GetBufferedLogs",
                "Ping"
            },
            events = new[]
            {
                "LogMessage",
                "LogError",
                "LogStatusChange",
                "LogHistory",
                "SubscriptionConfirmed",
                "SubscriptionError"
            }
        });

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Handle client disconnection
    /// </summary>
    /// <param name="exception">Exception that caused the disconnect (if any)</param>
    /// <returns>Task representing the operation</returns>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            _logger.LogInformation("Client {ConnectionId} disconnected from log streaming hub", Context.ConnectionId);

            // Get all streams and remove this connection
            var streamStatus = await _logStreamingService.GetStreamStatusAsync();

            foreach (var kvp in streamStatus)
            {
                if (kvp.Value.ClientIds.Contains(Context.ConnectionId))
                {
                    await _logStreamingService.StopStreamingAsync(kvp.Key, Context.ConnectionId);
                    _logger.LogDebug("Removed client {ConnectionId} from instance {InstanceName} stream during disconnect",
                        Context.ConnectionId, kvp.Key);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client {ConnectionId} disconnect", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Handle ping from client for connection health
    /// </summary>
    /// <returns>Task representing the operation</returns>
    public async Task Ping()
    {
        await Clients.Caller.SendAsync("Pong", new { timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Handle client requesting connection info
    /// </summary>
    /// <returns>Task representing the operation</returns>
    public async Task GetConnectionInfo()
    {
        try
        {
            var streamStatus = await _logStreamingService.GetStreamStatusAsync();
            var clientStreams = streamStatus
                .Where(kvp => kvp.Value.ClientIds.Contains(Context.ConnectionId))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            await Clients.Caller.SendAsync("ConnectionInfo", new
            {
                connectionId = Context.ConnectionId,
                connectedAt = DateTime.UtcNow, // This would ideally be tracked
                activeStreams = clientStreams.Keys.ToArray(),
                totalActiveStreams = streamStatus.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting connection info for client {ConnectionId}", Context.ConnectionId);
            await Clients.Caller.SendAsync("ConnectionInfoError", new { error = ex.Message });
        }
    }
}
