namespace TheKrystalShip.KGSM.Api.Models;

/// <summary>
/// CPU core information
/// </summary>
public class CpuCoreInfo
{
    /// <summary>
    /// Core number
    /// </summary>
    public int Core { get; set; }

    /// <summary>
    /// Usage percentage
    /// </summary>
    public double Usage { get; set; }

    /// <summary>
    /// Core model
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Core speed in MHz
    /// </summary>
    public int Speed { get; set; }
}
