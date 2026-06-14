using Microsoft.AspNetCore.SignalR;
using TheKrystalShip.KGSM.Api.Hubs;
using TheKrystalShip.KGSM.Api.Models;
using TheKrystalShip.KGSM.Api.Constants;

namespace TheKrystalShip.KGSM.Api.Services;

/// <summary>
/// Service that broadcasts system metrics updates to SignalR clients
/// </summary>
public class SystemMetricsBroadcastService : IHostedService
{
    private readonly ISystemMetricsService _systemMetricsService;
    private readonly IHubContext<SystemMetricsHub> _hubContext;
    private readonly ILogger<SystemMetricsBroadcastService> _logger;

    public SystemMetricsBroadcastService(
        ISystemMetricsService systemMetricsService,
        IHubContext<SystemMetricsHub> hubContext,
        ILogger<SystemMetricsBroadcastService> logger)
    {
        _systemMetricsService = systemMetricsService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting System Metrics Broadcast Service");

        // Subscribe to metrics updates
        _systemMetricsService.MetricsUpdated += OnMetricsUpdated;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping System Metrics Broadcast Service");

        // Unsubscribe from metrics updates
        _systemMetricsService.MetricsUpdated -= OnMetricsUpdated;

        return Task.CompletedTask;
    }

    private async void OnMetricsUpdated(object? sender, SystemMetrics metrics)
    {
        try
        {
            // Broadcast to all subscribers
            await _hubContext.Clients.Group(SignalRConstants.Groups.MetricsSubscribers)
                .SendAsync(SignalRConstants.Events.MetricsUpdate, metrics);

            // Broadcast specific metrics to targeted subscribers
            await BroadcastSpecificMetrics(metrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting metrics update");
        }
    }

    private async Task BroadcastSpecificMetrics(SystemMetrics metrics)
    {
        // Broadcast CPU metrics
        await _hubContext.Clients.Group(SignalRConstants.Groups.CpuSubscribers)
            .SendAsync(SignalRConstants.Events.CpuUpdate, metrics.Cpu);

        // Broadcast Memory metrics
        await _hubContext.Clients.Group(SignalRConstants.Groups.MemorySubscribers)
            .SendAsync(SignalRConstants.Events.MemoryUpdate, metrics.Memory);

        // Broadcast Disk metrics
        await _hubContext.Clients.Group(SignalRConstants.Groups.DiskSubscribers)
            .SendAsync(SignalRConstants.Events.DiskUpdate, metrics.Disk);

        // Broadcast Network metrics
        await _hubContext.Clients.Group(SignalRConstants.Groups.NetworkSubscribers)
            .SendAsync(SignalRConstants.Events.NetworkUpdate, metrics.Network);
    }
}
