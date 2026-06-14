namespace TheKrystalShip.KGSM.Api.Constants;

/// <summary>
/// Constants for system metrics collection and processing
/// </summary>
public static class SystemMetricsConstants
{
    /// <summary>
    /// Collection intervals and timing
    /// </summary>
    public static class Timing
    {
        public const int CollectionIntervalSeconds = 1;
        public const int CpuSampleDelayMilliseconds = 100;
        public const int MaxCpuSamples = 10;
        public const int MaxNetworkHistoryPoints = 60;
        public const int MaxCpuHistoryPoints = 60;
    }

    /// <summary>
    /// Dictionary keys for storing historical data
    /// </summary>
    public static class HistoryKeys
    {
        public const string NetworkRx = "rx";
        public const string NetworkTx = "tx";
        public const string CpuMain = "main";
    }

    /// <summary>
    /// Linux system file paths for reading metrics
    /// </summary>
    public static class LinuxPaths
    {
        public const string CpuInfo = "/proc/cpuinfo";
        public const string MemoryInfo = "/proc/meminfo";
        public const string NetworkStats = "/proc/net/dev";
        public const string CpuStats = "/proc/stat";
    }

    /// <summary>
    /// Environment variable names
    /// </summary>
    public static class EnvironmentVariables
    {
        public const string ProcessorIdentifier = "PROCESSOR_IDENTIFIER";
    }

    /// <summary>
    /// Default values and fallbacks
    /// </summary>
    public static class Defaults
    {
        public const long DefaultMemoryBytes = 8L * 1024 * 1024 * 1024; // 8GB
        public const double DefaultCpuUsage = 25.0;
        public const double DefaultMemoryUsage = 45.0;
        public const double DefaultDiskUsage = 50.0;
        public const double FallbackDiskSizeGB = 100.0;
        public const string UnknownCpuModel = "Unknown CPU";
        public const string WindowsCpuModel = "Windows CPU";
        public const string LinuxCpuModel = "Linux CPU";
        public const string MacOsCpuModel = "macOS CPU";
        public const string GenericCpuModel = "CPU";
        public const string CpuCoreModel = "CPU Core";
    }

    /// <summary>
    /// Drive and filesystem constants
    /// </summary>
    public static class FileSystem
    {
        public const string LinuxRootPath = "/";
        public const string WindowsRootPath = "C:\\";
    }

    /// <summary>
    /// Conversion factors
    /// </summary>
    public static class Conversion
    {
        public const double BytesToMB = 1024.0 * 1024.0;
        public const double BytesToGB = 1024.0 * 1024.0 * 1024.0;
        public const double BytesToKB = 1024.0;
    }

    /// <summary>
    /// CPU usage calculation constants
    /// </summary>
    public static class CpuCalculation
    {
        public const double ProcessCountMultiplier = 0.3;
        public const double ProcessEstimateMultiplier = 0.5;
        public const double FallbackMaxUsage = 50.0;
        public const double CoreVariationRange = 15.0;
        public const double MaxCpuUsage = 100.0;
        public const double MinCpuUsage = 0.0;
    }
}
