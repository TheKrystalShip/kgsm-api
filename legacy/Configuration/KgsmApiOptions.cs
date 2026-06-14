namespace TheKrystalShip.KGSM.Api.Configuration;

/// <summary>
/// Configuration options for the KGSM API
/// </summary>
public class KgsmApiOptions
{
    /// <summary>
    /// Path to the KGSM executable
    /// </summary>
    public string KgsmPath { get; set; } = "kgsm";

    /// <summary>
    /// Path to the KGSM Unix socket for events
    /// </summary>
    public string SocketPath { get; set; } = "/tmp/kgsm.sock";

    /// <summary>
    /// Port for the API server
    /// </summary>
    public int Port { get; set; } = 5167;

    /// <summary>
    /// Allowed origins for CORS
    /// </summary>
    public string[] AllowedOrigins { get; set; } =
    {
        "http://localhost:3000",
        "http://127.0.0.1:3000"
    };

    /// <summary>
    /// Maximum number of log lines to buffer per instance
    /// </summary>
    public int MaxLogBufferLines { get; set; } = 1000;

    /// <summary>
    /// Log cleanup interval in seconds
    /// </summary>
    public int LogCleanupIntervalSeconds { get; set; } = 30;
}
