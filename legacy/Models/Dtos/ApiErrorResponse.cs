namespace TheKrystalShip.KGSM.Api.Models.Dtos;

/// <summary>
/// API error response
/// </summary>
public class ApiErrorResponse : ApiResponse
{
    /// <summary>
    /// Error details
    /// </summary>
    public string Error { get; set; } = string.Empty;

    public ApiErrorResponse(string error, string? message = null)
    {
        Success = false;
        Error = error;
        Message = message ?? error;
    }
}
