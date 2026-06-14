using TheKrystalShip.KGSM.Api.Models;

namespace TheKrystalShip.KGSM.Api.Services;

/// <summary>
/// Service for collecting system metrics such as CPU, memory, disk, and network usage
/// Provides both cached metrics and real-time streaming capabilities
/// </summary>
public interface ISystemMetricsService : IDisposable
{
    /// <summary>
    /// Gets the latest cached system metrics (updated continuously in background)
    /// </summary>
    /// <returns>Latest system metrics from cache</returns>
    SystemMetrics GetLatestMetrics();

    /// <summary>
    /// Gets comprehensive system metrics including CPU, memory, disk, and network
    /// </summary>
    /// <returns>Complete system metrics</returns>
    Task<SystemMetrics> GetSystemMetricsAsync();

    /// <summary>
    /// Gets CPU metrics including usage per core and historical data
    /// </summary>
    /// <returns>CPU metrics</returns>
    Task<CpuMetrics> GetCpuMetricsAsync();

    /// <summary>
    /// Gets memory metrics including total, used, and free memory
    /// </summary>
    /// <returns>Memory metrics</returns>
    Task<MemoryMetrics> GetMemoryMetricsAsync();

    /// <summary>
    /// Gets disk metrics including total, used, and free disk space
    /// </summary>
    /// <returns>Disk metrics</returns>
    Task<DiskMetrics> GetDiskMetricsAsync();

    /// <summary>
    /// Gets network metrics including throughput and historical data
    /// </summary>
    /// <returns>Network metrics</returns>
    Task<NetworkMetrics> GetNetworkMetricsAsync();

    /// <summary>
    /// Event raised when new metrics are available
    /// </summary>
    event EventHandler<SystemMetrics>? MetricsUpdated;

    /// <summary>
    /// Starts the background metrics collection
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the background metrics collection
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
