using Microsoft.AspNetCore.SignalR;
using TheKrystalShip.KGSM.Api.Models;
using TheKrystalShip.KGSM.Api.Services;
using TheKrystalShip.KGSM.Api.Constants;

namespace TheKrystalShip.KGSM.Api.Hubs;

/// <summary>
/// SignalR hub for real-time system metrics streaming
/// </summary>
public class SystemMetricsHub : Hub
{
    private readonly ISystemMetricsService _systemMetricsService;
    private readonly ILogger<SystemMetricsHub> _logger;

    public SystemMetricsHub(
        ISystemMetricsService systemMetricsService,
        ILogger<SystemMetricsHub> logger)
    {
        _systemMetricsService = systemMetricsService;
        _logger = logger;
    }

    /// <summary>
    /// Called when a client connects to the hub
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client {ConnectionId} connected to SystemMetrics hub", Context.ConnectionId);

        // Send current metrics immediately
        var currentMetrics = _systemMetricsService.GetLatestMetrics();
        await Clients.Caller.SendAsync(SignalRConstants.Events.MetricsUpdate, currentMetrics);

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Called when a client disconnects from the hub
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected from SystemMetrics hub", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to real-time metrics updates
    /// </summary>
    public async Task SubscribeToMetrics()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRConstants.Groups.MetricsSubscribers);
        _logger.LogDebug("Client {ConnectionId} subscribed to metrics updates", Context.ConnectionId);
    }

    /// <summary>
    /// Unsubscribe from real-time metrics updates
    /// </summary>
    public async Task UnsubscribeFromMetrics()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, SignalRConstants.Groups.MetricsSubscribers);
        _logger.LogDebug("Client {ConnectionId} unsubscribed from metrics updates", Context.ConnectionId);
    }

    /// <summary>
    /// Get current system metrics
    /// </summary>
    public async Task GetCurrentMetrics()
    {
        var metrics = _systemMetricsService.GetLatestMetrics();
        await Clients.Caller.SendAsync(SignalRConstants.Events.MetricsUpdate, metrics);
    }

    /// <summary>
    /// Subscribe to specific metric type only
    /// </summary>
    public async Task SubscribeToMetricType(string metricType)
    {
        var groupName = SignalRConstants.Groups.GetMetricTypeGroup(metricType);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogDebug("Client {ConnectionId} subscribed to {MetricType} updates", Context.ConnectionId, metricType);
    }

    /// <summary>
    /// Unsubscribe from specific metric type
    /// </summary>
    public async Task UnsubscribeFromMetricType(string metricType)
    {
        var groupName = SignalRConstants.Groups.GetMetricTypeGroup(metricType);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogDebug("Client {ConnectionId} unsubscribed from {MetricType} updates", Context.ConnectionId, metricType);
    }
}
