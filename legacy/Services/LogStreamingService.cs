using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using TheKrystalShip.KGSM.Api.Hubs;
using TheKrystalShip.KGSM.Api.Models;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Api.Services;

/// <summary>
/// Implementation of the log streaming service using KGSM library's log subscription functionality
/// </summary>
public class LogStreamingService : ILogStreamingService, IDisposable
{
    private readonly IHubContext<LogStreamingHub> _hubContext;
    private readonly IInstanceService _instanceService;
    private readonly ILogger<LogStreamingService> _logger;
    private readonly ConcurrentDictionary<string, LogStreamInfo> _streams = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed = false;

    public LogStreamingService(
        IHubContext<LogStreamingHub> hubContext,
        IInstanceService instanceService,
        ILogger<LogStreamingService> logger)
    {
        _hubContext = hubContext;
        _instanceService = instanceService;
        _logger = logger;

        // Setup cleanup timer to run every 30 seconds
        _cleanupTimer = new Timer(async _ => await CleanupInactiveStreamsAsync(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <inheritdoc/>
    public async Task StartStreamingAsync(string instanceName, string connectionId)
    {
        _logger.LogDebug("Starting log streaming for instance {InstanceName}, connection {ConnectionId}",
            instanceName, connectionId);

        try
        {
            var stream = await _streams.GetOrAddAsync(instanceName, async _ =>
            {
                var newStream = new LogStreamInfo(instanceName);

                // Create KGSM log subscription
                try
                {
                    var subscription = await _instanceService.SubscribeToLogsAsync(instanceName);
                    newStream.Subscription = subscription;

                    // Wire up event handlers
                    subscription.LogReceived += (sender, args) => OnLogReceived(instanceName, args);
                    subscription.ErrorOccurred += (sender, args) => OnErrorOccurred(instanceName, args);
                    subscription.StatusChanged += (sender, args) => OnStatusChanged(instanceName, args);

                    _logger.LogInformation("Created KGSM log subscription for instance {InstanceName}", instanceName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create KGSM log subscription for instance {InstanceName}", instanceName);
                    throw;
                }

                return newStream;
            });

            lock (stream)
            {
                if (!stream.ClientIds.Contains(connectionId))
                {
                    stream.ClientIds.Add(connectionId);
                    stream.LastActivity = DateTime.UtcNow;

                    _logger.LogInformation("Added client {ConnectionId} to stream for instance {InstanceName}. Total clients: {ClientCount}",
                        connectionId, instanceName, stream.ClientIds.Count);
                }
            }

            // Send buffered logs to the new client
            if (stream.Buffer.Count > 0)
            {
                var historyData = string.Join("\n", stream.Buffer);
                await _hubContext.Clients.Client(connectionId).SendAsync("LogHistory", historyData);
            }

            // Send subscription confirmation with stream status
            var subscriptionInfo = new
            {
                instanceName,
                bufferSize = stream.Buffer.Count,
                isActive = stream.Subscription?.IsActive ?? false,
                subscriptionId = stream.Subscription?.SubscriptionId ?? Guid.Empty
            };

            await _hubContext.Clients.Client(connectionId).SendAsync("SubscriptionConfirmed", subscriptionInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start log streaming for instance {InstanceName}, connection {ConnectionId}",
                instanceName, connectionId);

            await _hubContext.Clients.Client(connectionId).SendAsync("SubscriptionError",
                new { instanceName, error = ex.Message });
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopStreamingAsync(string instanceName, string connectionId)
    {
        _logger.LogDebug("Stopping log streaming for instance {InstanceName}, connection {ConnectionId}",
            instanceName, connectionId);

        if (_streams.TryGetValue(instanceName, out var stream))
        {
            lock (stream)
            {
                stream.ClientIds.Remove(connectionId);
                _logger.LogInformation("Removed client {ConnectionId} from stream for instance {InstanceName}. Remaining clients: {ClientCount}",
                    connectionId, instanceName, stream.ClientIds.Count);

                // If no more clients, mark for cleanup
                if (stream.ClientIds.Count == 0)
                {
                    stream.LastActivity = DateTime.UtcNow;
                    _logger.LogDebug("No more clients for instance {InstanceName}, marking for cleanup", instanceName);
                }
            }
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<string> GetBufferedLogsAsync(string instanceName)
    {
        _logger.LogDebug("Getting buffered logs for instance {InstanceName}", instanceName);

        if (_streams.TryGetValue(instanceName, out var stream))
        {
            lock (stream)
            {
                if (stream.Buffer.Count > 0)
                {
                    return Task.FromResult(string.Join("\n", stream.Buffer));
                }
            }
        }

        // If no buffered logs, try to get recent logs from KGSM directly
        try
        {
            var result = _instanceService.GetLogs(instanceName);
            if (result.IsSuccess)
            {
                return Task.FromResult(result.Stdout);
            }
            else
            {
                _logger.LogWarning("Failed to get logs for instance {InstanceName}: {Error}", instanceName, result.Stderr);
                return Task.FromResult("No logs available or instance is not accessible");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get logs for instance {InstanceName}", instanceName);
            return Task.FromResult("No logs available or instance is not accessible");
        }
    }

    /// <inheritdoc/>
    public async Task CleanupInactiveStreamsAsync()
    {
        _logger.LogDebug("Running cleanup for inactive streams");

        var inactiveThreshold = TimeSpan.FromMinutes(5);
        var now = DateTime.UtcNow;
        var streamsToRemove = new List<string>();

        foreach (var kvp in _streams)
        {
            var stream = kvp.Value;
            lock (stream)
            {
                if (stream.ClientIds.Count == 0 && (now - stream.LastActivity) > inactiveThreshold)
                {
                    streamsToRemove.Add(kvp.Key);
                }
            }
        }

        foreach (var instanceName in streamsToRemove)
        {
            if (_streams.TryRemove(instanceName, out var removedStream))
            {
                _logger.LogInformation("Cleaning up inactive stream for instance {InstanceName}", instanceName);

                // Stop the KGSM log subscription
                try
                {
                    removedStream.Subscription?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing log subscription for instance {InstanceName}", instanceName);
                }
            }
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, LogStreamStatus>> GetStreamStatusAsync()
    {
        var status = new Dictionary<string, LogStreamStatus>();

        foreach (var kvp in _streams)
        {
            var stream = kvp.Value;
            lock (stream)
            {
                status[kvp.Key] = new LogStreamStatus
                {
                    IsActive = stream.ClientIds.Count > 0 && (stream.Subscription?.IsActive ?? false),
                    BufferSize = stream.Buffer.Count,
                    ClientCount = stream.ClientIds.Count,
                    LastActivity = stream.LastActivity,
                    ClientIds = new List<string>(stream.ClientIds)
                };
            }
        }

        return await Task.FromResult(status);
    }

    /// <summary>
    /// Handles log received events from KGSM subscription
    /// </summary>
    private async void OnLogReceived(string instanceName, LogStreamEventArgs args)
    {
        try
        {
            if (_streams.TryGetValue(instanceName, out var stream))
            {
                var logLine = args.LogEntry.ToString();

                lock (stream)
                {
                    // Add to buffer (keep last 1000 lines)
                    stream.Buffer.Enqueue(logLine);
                    while (stream.Buffer.Count > 1000)
                    {
                        stream.Buffer.Dequeue();
                    }

                    stream.LastActivity = DateTime.UtcNow;
                }

                // Broadcast to all connected clients for this instance
                var clientIds = stream.ClientIds.ToList();
                if (clientIds.Count > 0)
                {
                    var logData = new
                    {
                        instanceName,
                        timestamp = args.LogEntry.Timestamp,
                        level = args.LogEntry.Level.ToString(),
                        message = args.LogEntry.Message,
                        source = args.LogEntry.Source,
                        threadId = args.LogEntry.ThreadId,
                        rawLine = logLine
                    };

                    await _hubContext.Clients.Clients(clientIds).SendAsync("LogMessage", logData);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling log received event for instance {InstanceName}", instanceName);
        }
    }

    /// <summary>
    /// Handles error events from KGSM subscription
    /// </summary>
    private async void OnErrorOccurred(string instanceName, LogStreamErrorEventArgs args)
    {
        try
        {
            _logger.LogError(args.Exception, "Log streaming error for instance {InstanceName}", instanceName);

            if (_streams.TryGetValue(instanceName, out var stream))
            {
                var clientIds = stream.ClientIds.ToList();
                if (clientIds.Count > 0)
                {
                    var errorData = new
                    {
                        instanceName,
                        error = args.Exception.Message,
                        timestamp = DateTime.UtcNow
                    };

                    await _hubContext.Clients.Clients(clientIds).SendAsync("LogError", errorData);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling log stream error event for instance {InstanceName}", instanceName);
        }
    }

        /// <summary>
    /// Handles status change events from KGSM subscription
    /// </summary>
    private async void OnStatusChanged(string instanceName, LogStreamStatusEventArgs args)
    {
        try
        {
            _logger.LogDebug("Log stream status changed for instance {InstanceName}: {IsConnected} - {Message}",
                instanceName, args.IsConnected, args.Message);

            if (_streams.TryGetValue(instanceName, out var stream))
            {
                var clientIds = stream.ClientIds.ToList();
                if (clientIds.Count > 0)
                {
                    var statusData = new
                    {
                        instanceName,
                        status = args.IsConnected ? "Connected" : "Disconnected",
                        message = args.Message,
                        timestamp = DateTime.UtcNow
                    };

                    await _hubContext.Clients.Clients(clientIds).SendAsync("LogStatusChange", statusData);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling log stream status change event for instance {InstanceName}", instanceName);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _cleanupTimer?.Dispose();

            // Dispose all active subscriptions
            foreach (var stream in _streams.Values)
            {
                try
                {
                    stream.Subscription?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing log subscription for instance {InstanceName}", stream.InstanceName);
                }
            }

            _streams.Clear();
            _disposed = true;
        }
    }
}

/// <summary>
/// Internal class to track log stream information with KGSM subscription
/// </summary>
internal class LogStreamInfo
{
    public string InstanceName { get; }
    public List<string> ClientIds { get; } = new();
    public Queue<string> Buffer { get; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public LogSubscription? Subscription { get; set; }
    public bool IsActive => ClientIds.Count > 0 && (Subscription?.IsActive ?? false);

    public LogStreamInfo(string instanceName)
    {
        InstanceName = instanceName;
    }
}

/// <summary>
/// Extension methods for ConcurrentDictionary to support async operations
/// </summary>
internal static class ConcurrentDictionaryExtensions
{
    public static async Task<TValue> GetOrAddAsync<TKey, TValue>(
        this ConcurrentDictionary<TKey, TValue> dictionary,
        TKey key,
        Func<TKey, Task<TValue>> valueFactory) where TKey : notnull
    {
        if (dictionary.TryGetValue(key, out var existingValue))
        {
            return existingValue;
        }

        var newValue = await valueFactory(key);
        return dictionary.GetOrAdd(key, newValue);
    }
}
