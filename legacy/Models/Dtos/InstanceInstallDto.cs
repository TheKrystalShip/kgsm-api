using System.ComponentModel.DataAnnotations;

namespace TheKrystalShip.KGSM.Api.Models.Dtos;

/// <summary>
/// DTO for installing a new instance
/// </summary>
public class InstanceInstallDto
{
    /// <summary>
    /// Blueprint name to install
    /// </summary>
    [Required]
    public string Blueprint { get; set; } = string.Empty;

    /// <summary>
    /// Optional instance ID
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// Optional installation directory
    /// </summary>
    public string? InstallDir { get; set; }

    /// <summary>
    /// Optional version to install
    /// </summary>
    public string? Version { get; set; }
}
