namespace TheKrystalShip.KGSM.Api.Constants;

/// <summary>
/// Constants for SignalR hubs, groups, and events
/// </summary>
public static class SignalRConstants
{
    /// <summary>
    /// SignalR hub endpoints
    /// </summary>
    public static class Hubs
    {
        public const string LogStreaming = "/hubs/logs";
        public const string SystemMetrics = "/hubs/metrics";
    }

    /// <summary>
    /// SignalR group names for organizing clients
    /// </summary>
    public static class Groups
    {
        public const string MetricsSubscribers = "MetricsSubscribers";
        public const string CpuSubscribers = "MetricsSubscribers_cpu";
        public const string MemorySubscribers = "MetricsSubscribers_memory";
        public const string DiskSubscribers = "MetricsSubscribers_disk";
        public const string NetworkSubscribers = "MetricsSubscribers_network";

        /// <summary>
        /// Gets the group name for a specific metric type
        /// </summary>
        public static string GetMetricTypeGroup(string metricType) => $"MetricsSubscribers_{metricType}";
    }

    /// <summary>
    /// SignalR event names sent to clients
    /// </summary>
    public static class Events
    {
        public const string MetricsUpdate = "MetricsUpdate";
        public const string CpuUpdate = "CpuUpdate";
        public const string MemoryUpdate = "MemoryUpdate";
        public const string DiskUpdate = "DiskUpdate";
        public const string NetworkUpdate = "NetworkUpdate";
        public const string LogMessage = "LogMessage";
        public const string LogDisconnect = "LogDisconnect";
    }

    /// <summary>
    /// SignalR method names that clients can invoke
    /// </summary>
    public static class Methods
    {
        public const string SubscribeToMetrics = "SubscribeToMetrics";
        public const string UnsubscribeFromMetrics = "UnsubscribeFromMetrics";
        public const string GetCurrentMetrics = "GetCurrentMetrics";
        public const string SubscribeToMetricType = "SubscribeToMetricType";
        public const string UnsubscribeFromMetricType = "UnsubscribeFromMetricType";
        public const string ConnectToInstance = "ConnectToInstance";
        public const string DisconnectFromInstance = "DisconnectFromInstance";
    }

    /// <summary>
    /// Metric type names for selective subscriptions
    /// </summary>
    public static class MetricTypes
    {
        public const string Cpu = "cpu";
        public const string Memory = "memory";
        public const string Disk = "disk";
        public const string Network = "network";
    }
}
