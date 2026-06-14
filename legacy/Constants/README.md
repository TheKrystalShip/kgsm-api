# Constants Documentation

This directory contains all string constants used throughout the KGSM API to avoid magic strings and improve maintainability.

## Files Overview

### SignalRConstants.cs
Contains all SignalR-related constants including:

- **Hubs**: SignalR hub endpoint paths
  - `LogStreaming`: `/hubs/logs`
  - `SystemMetrics`: `/hubs/metrics`

- **Groups**: SignalR group names for client organization
  - `MetricsSubscribers`: Main metrics group
  - `CpuSubscribers`, `MemorySubscribers`, etc.: Specific metric type groups

- **Events**: Event names sent to clients
  - `MetricsUpdate`, `CpuUpdate`, `MemoryUpdate`, etc.

- **Methods**: Method names that clients can invoke
  - `SubscribeToMetrics`, `UnsubscribeFromMetrics`, etc.

- **MetricTypes**: Metric type identifiers
  - `Cpu`, `Memory`, `Disk`, `Network`

### SystemMetricsConstants.cs
Contains all system metrics collection constants:

- **Timing**: Collection intervals and timing values
  - `CollectionIntervalSeconds`: How often metrics are collected (1 second)
  - `MaxCpuSamples`, `MaxNetworkHistoryPoints`: History limits

- **HistoryKeys**: Dictionary keys for historical data storage
  - `NetworkRx`, `NetworkTx`, `CpuMain`: Keys for data storage

- **LinuxPaths**: Linux system file paths for metrics
  - `CpuInfo`: `/proc/cpuinfo`
  - `MemoryInfo`: `/proc/meminfo`

- **Defaults**: Fallback values and default configurations
  - `DefaultMemoryBytes`: 8GB default memory
  - `UnknownCpuModel`: "Unknown CPU" fallback

- **Conversion**: Unit conversion factors
  - `BytesToMB`, `BytesToGB`, `BytesToKB`: Conversion constants

- **CpuCalculation**: CPU usage calculation constants
  - `ProcessCountMultiplier`, `CoreVariationRange`: Calculation parameters

### ApiConstants.cs
Contains all API-related constants:

- **Routes**: API endpoint paths
  - `SystemMetrics`: `/api/system/metrics`
  - `Health`: `/health`

- **CorsPolicy**: CORS policy names
  - `KgsmApiPolicy`: Main CORS policy name

- **ConfigurationSections**: Configuration section keys
  - `KgsmApi`: Main configuration section
  - `KgsmPath`, `SocketPath`: Specific configuration keys

- **ContentTypes**: HTTP content type constants
  - `ApplicationJson`: `application/json`

## Benefits

1. **No Magic Strings**: All string literals are centralized and named
2. **IntelliSense Support**: IDE can provide autocomplete and refactoring
3. **Compile-Time Safety**: Typos become compilation errors
4. **Easy Maintenance**: Change a constant in one place, updates everywhere
5. **Documentation**: Constants are self-documenting with XML comments

## Usage Examples

```csharp
// Instead of:
await Groups.AddToGroupAsync(Context.ConnectionId, "MetricsSubscribers");

// Use:
await Groups.AddToGroupAsync(Context.ConnectionId, SignalRConstants.Groups.MetricsSubscribers);

// Instead of:
builder.Configuration.GetSection("KgsmApi")

// Use:
builder.Configuration.GetSection(ApiConstants.ConfigurationSections.KgsmApi)
```

## Adding New Constants

When adding new constants:

1. Choose the appropriate constants file based on functionality
2. Add to the relevant nested class (e.g., `Routes`, `Events`, etc.)
3. Use descriptive names that match their usage
4. Add XML documentation comments
5. Update this README if adding new categories

## Maintenance

- Review constants periodically for unused entries
- Ensure all string literals in the codebase use constants
- Update constants when API endpoints or event names change
- Keep constants grouped logically within their respective classes
