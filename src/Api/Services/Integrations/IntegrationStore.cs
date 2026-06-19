using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// The single reader/writer of the integration-config table (M8·c). A singleton that owns its own DI
/// scope per operation (the same pattern as <see cref="Audit.AuditService"/> — reads/writes can arrive
/// off any request path) and serializes writes behind a gate (SQLite is single-writer). The provider-
/// specific JSON columns (<c>Settings</c>, <c>Events</c>) are (de)serialized here so the rest of the
/// code works with the typed <see cref="IntegrationRecord"/>.
/// </summary>
public sealed class IntegrationStore(IServiceScopeFactory scopeFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <summary>Create the schema if absent (idempotent, EnsureCreated — see <see cref="AppDbContext"/>).
    /// Creating the whole model covers both the audit and integrations tables, so this is safe to call
    /// independently of the audit consumer's own EnsureCreated.</summary>
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        if (_ensured) return;
        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured) return;
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>The stored config for a provider, or <see cref="IntegrationRecord.Empty"/> if none.</summary>
    public async Task<IntegrationRecord> GetAsync(string provider, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IntegrationEntity? entity = await db.Integrations.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Provider == provider, ct).ConfigureAwait(false);
        return entity is null ? IntegrationRecord.Empty(provider) : ToRecord(entity);
    }

    /// <summary>Upsert a provider's config (serialized; SQLite single-writer).</summary>
    public async Task SaveAsync(IntegrationRecord record, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IntegrationEntity? entity = await db.Integrations
                .FirstOrDefaultAsync(i => i.Provider == record.Provider, ct).ConfigureAwait(false);
            if (entity is null)
            {
                entity = new IntegrationEntity { Provider = record.Provider };
                db.Integrations.Add(entity);
            }
            entity.Enabled = record.Enabled;
            entity.Secret = record.Secret;
            entity.ChannelLabel = record.ChannelLabel;
            entity.Settings = JsonSerializer.Serialize(record.Settings, Json);
            entity.Events = JsonSerializer.Serialize(record.Events, Json);
            entity.UpdatedAt = record.UpdatedAt ?? DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    private static IntegrationRecord ToRecord(IntegrationEntity e) =>
        new(e.Provider, e.Enabled, e.Secret, e.ChannelLabel,
            Deserialize<Dictionary<string, string>>(e.Settings) ?? new Dictionary<string, string>(),
            Deserialize<List<NotificationRule>>(e.Events) ?? [],
            e.UpdatedAt);

    private static T? Deserialize<T>(string? json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json);
}
