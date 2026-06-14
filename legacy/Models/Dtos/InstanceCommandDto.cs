using System.ComponentModel.DataAnnotations;

namespace TheKrystalShip.KGSM.Api.Models.Dtos;

/// <summary>
/// DTO for sending commands to an instance
/// </summary>
public class InstanceCommandDto
{
    /// <summary>
    /// Command to send to the instance
    /// </summary>
    [Required]
    public string Command { get; set; } = string.Empty;
}
