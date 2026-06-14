using TheKrystalShip.KGSM.Api.Services;

namespace TheKrystalShip.KGSM.Api.Services;

/// <summary>
/// Background service that continuously collects system metrics and keeps the cache updated
/// </summary>
public class SystemMetricsBackgroundService : BackgroundService
{
    private readonly ISystemMetricsService _systemMetricsService;
    private readonly ILogger<SystemMetricsBackgroundService> _logger;

    public SystemMetricsBackgroundService(
        ISystemMetricsService systemMetricsService,
        ILogger<SystemMetricsBackgroundService> logger)
    {
        _systemMetricsService = systemMetricsService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("System Metrics Background Service starting");

        try
        {
            await _systemMetricsService.StartAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in System Metrics Background Service");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("System Metrics Background Service stopping");

        await _systemMetricsService.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
