using System.ComponentModel.DataAnnotations;

namespace TheKrystalShip.KGSM.Api.Models.Dtos;

/// <summary>
/// DTO for log disconnect request
/// </summary>
public class LogDisconnectDto
{
    /// <summary>
    /// Client ID to disconnect
    /// </summary>
    [Required]
    public string ClientId { get; set; } = string.Empty;
}
