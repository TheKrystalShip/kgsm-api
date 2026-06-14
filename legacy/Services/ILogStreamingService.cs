using TheKrystalShip.KGSM.Api.Models;

namespace TheKrystalShip.KGSM.Api.Services;

/// <summary>
/// Service for managing log streaming
/// </summary>
public interface ILogStreamingService : IDisposable
{
    /// <summary>
    /// Starts streaming logs for an instance
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <param name="connectionId">SignalR connection ID</param>
    /// <returns>Task representing the operation</returns>
    Task StartStreamingAsync(string instanceName, string connectionId);

    /// <summary>
    /// Stops streaming logs for a connection
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <param name="connectionId">SignalR connection ID</param>
    /// <returns>Task representing the operation</returns>
    Task StopStreamingAsync(string instanceName, string connectionId);

    /// <summary>
    /// Gets buffered logs for an instance
    /// </summary>
    /// <param name="instanceName">Name of the instance</param>
    /// <returns>Buffered log content</returns>
    Task<string> GetBufferedLogsAsync(string instanceName);

    /// <summary>
    /// Cleans up inactive log streams
    /// </summary>
    /// <returns>Task representing the operation</returns>
    Task CleanupInactiveStreamsAsync();

    /// <summary>
    /// Gets the status of all active log streams
    /// </summary>
    /// <returns>Dictionary of instance names to stream status</returns>
    Task<Dictionary<string, LogStreamStatus>> GetStreamStatusAsync();
}
