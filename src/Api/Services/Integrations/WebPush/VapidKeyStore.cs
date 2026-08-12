using TheKrystalShip.KGSM.WebPush;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>
/// Get-or-create for this host's one VAPID key pair, persisted in the <c>webpush</c> integration row.
/// <para>
/// It lives there rather than in configuration for two reasons: the private half is a secret and the
/// integration row is already the place secrets go (masked on read, write-only on PATCH), and the pair
/// must <b>survive restarts unchanged</b> — the public key is embedded in every subscription a browser
/// has already created, so regenerating it silently orphans every registered device.
/// </para>
/// </summary>
public sealed class VapidKeyStore(IntegrationStore integrations)
{
    /// <summary>The provider id, and so the integration row's key.</summary>
    public const string ProviderId = "webpush";

    /// <summary>The <see cref="IntegrationRecord.Settings"/> key holding the public half. Public by
    /// nature — every browser needs it to subscribe — so it sits in Settings rather than in Secret,
    /// which is masked on read.</summary>
    public const string PublicKeySetting = "vapidPublicKey";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The stored pair, or <see langword="null"/> when this host has never generated one.</summary>
    public static VapidKeyPair? Read(IntegrationRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Secret)) return null;
        if (!record.Settings.TryGetValue(PublicKeySetting, out string? pub) || string.IsNullOrWhiteSpace(pub))
            return null;
        return new VapidKeyPair(record.Secret, pub);
    }

    /// <summary>
    /// This host's pair, generating and persisting one the first time. Serialized so two browsers
    /// subscribing at once cannot generate two pairs and leave the loser's devices unreachable.
    /// </summary>
    public async Task<VapidKeyPair> EnsureAsync(CancellationToken ct = default)
    {
        IntegrationRecord record = await integrations.GetAsync(ProviderId, ct).ConfigureAwait(false);
        if (Read(record) is { } existing) return existing;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read inside the gate: another caller may have generated while we waited.
            record = await integrations.GetAsync(ProviderId, ct).ConfigureAwait(false);
            if (Read(record) is { } raced) return raced;

            VapidKeyPair keys = VapidKeyPair.Generate();
            var settings = new Dictionary<string, string>(record.Settings, StringComparer.Ordinal)
            {
                [PublicKeySetting] = keys.PublicKey,
            };
            await integrations.SaveAsync(record with
            {
                Secret = keys.PrivateKey,
                Settings = settings,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
            return keys;
        }
        finally { _gate.Release(); }
    }
}
