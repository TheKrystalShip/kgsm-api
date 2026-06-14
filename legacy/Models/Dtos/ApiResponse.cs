namespace TheKrystalShip.KGSM.Api.Models.Dtos;

/// <summary>
/// Generic API response
/// </summary>
public class ApiResponse
{
    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Response message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Additional data
    /// </summary>
    public object? Data { get; set; }
}
