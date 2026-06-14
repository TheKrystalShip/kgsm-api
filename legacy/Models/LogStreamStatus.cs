namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// Status information for a log stream
/// </summary>
public class LogStreamStatus
{
    /// <summary>
    /// Whether the stream is active
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Number of buffered lines
    /// </summary>
    public int BufferSize { get; set; }

    /// <summary>
    /// Number of connected clients
    /// </summary>
    public int ClientCount { get; set; }

    /// <summary>
    /// Last activity timestamp
    /// </summary>
    public DateTime LastActivity { get; set; }

    /// <summary>
    /// Connected client IDs
    /// </summary>
    public List<string> ClientIds { get; set; } = new();
}
