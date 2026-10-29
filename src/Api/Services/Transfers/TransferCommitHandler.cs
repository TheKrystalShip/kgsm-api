using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Cluster.Messaging;

namespace TheKrystalShip.Api.Services.Transfers;

/// <summary>
/// The source's handler for <see cref="TransferMessageTypes.Commit"/>: the target holds the server now, so
/// the source removes its copy.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent, as every bus handler is: a commit already applied acknowledges and does nothing. A
/// transient engine failure throws, so the inbox answers <c>500</c> and the target's outbox retries; a
/// commit that can never apply — a payload that does not read, a transfer this node released — is
/// acknowledged, because no retry changes it (<see cref="TransferSource.ApplyCommitAsync"/>).
/// </para>
/// <para>
/// The sender is the envelope's <c>from</c>, which the inbox has already checked against the service
/// token's issuer, and it must be the transfer's target.
/// </para>
/// </remarks>
public sealed class TransferCommitHandler(TransferSource source, ILogger<TransferCommitHandler> logger)
    : IClusterMessageHandler
{
    public string Type => TransferMessageTypes.Commit;

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        TransferCommit? commit;
        try
        {
            commit = envelope.Payload.Deserialize(ApiContractsJson.Default.TransferCommit);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "a transfer commit from {From} could not be read; dropped", envelope.From);
            return;
        }

        if (commit is null || string.IsNullOrEmpty(commit.TransferId) || string.IsNullOrEmpty(commit.TargetInstanceId))
        {
            logger.LogWarning("a transfer commit from {From} named no transfer or no instance; dropped", envelope.From);
            return;
        }

        await source.ApplyCommitAsync(commit, envelope.From, ct).ConfigureAwait(false);
    }
}
