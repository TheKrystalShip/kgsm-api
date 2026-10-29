using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Data for <c>server.transfer.started</c> — the target's first line for a server arriving from another
/// node.
/// </summary>
/// <remarks>
/// Not instance-shaped: the server has no id on this node yet. It is named by where it came from.
/// </remarks>
public sealed class TransferStartedEventData : KgsmEventDataBase
{
    /// <summary>Gets or sets the transfer's id, the same on both nodes.</summary>
    public string TransferId { get; set; } = string.Empty;

    /// <summary>Gets or sets the source node's member id.</summary>
    public string SourceMember { get; set; } = string.Empty;

    /// <summary>Gets or sets the server's instance id on the source.</summary>
    public string SourceInstance { get; set; } = string.Empty;
}

/// <summary>
/// Data for <c>server.transfer.completed</c> — the source's last line for a server that now lives on
/// another node.
/// </summary>
public sealed class TransferCompletedEventData : EventDataBase
{
    /// <summary>Gets or sets the transfer's id, the same on both nodes.</summary>
    public string TransferId { get; set; } = string.Empty;

    /// <summary>Gets or sets the target node's member id.</summary>
    public string TargetMember { get; set; } = string.Empty;

    /// <summary>Gets or sets the instance id the target minted.</summary>
    public string TargetInstance { get; set; } = string.Empty;
}

/// <summary>
/// Data for <c>server.transfer.failed</c> — a transfer that ended without moving the server, as one node
/// observed it.
/// </summary>
/// <remarks>
/// Written by both sides: the source names its own instance as <see cref="EventDataBase.InstanceName"/>,
/// the target names the source's as <see cref="SourceInstance"/> and leaves <c>InstanceName</c> empty.
/// </remarks>
public sealed class TransferFailedEventData : EventDataBase
{
    /// <summary>Gets or sets the transfer's id, the same on both nodes.</summary>
    public string TransferId { get; set; } = string.Empty;

    /// <summary>Gets or sets the server's instance id on the source, when the target wrote the line.</summary>
    public string? SourceInstance { get; set; }

    /// <summary>Gets or sets the refusal code the transfer ended with.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets the step the transfer had reached.</summary>
    public string Phase { get; set; } = string.Empty;
}
