namespace TheKrystalShip.Api.Services.Transfers;

/// <summary>
/// The clock a transfer's ticket and records are dated by — its own seam, so a test can expire a ticket
/// without moving the clock every other service on the node reads.
/// </summary>
public sealed class TransferClock(TimeProvider time)
{
    public DateTimeOffset GetUtcNow() => time.GetUtcNow();
}
