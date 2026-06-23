using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// The single reader/writer of the <c>rawg_entry</c> cache table (the M8·a library increment). A singleton
/// that owns its own DI scope per operation (the <see cref="Audit.AuditService"/> / <see cref="Integrations.IntegrationStore"/>
/// pattern — reads arrive on the request path, writes on the hydration worker) and serializes writes behind a
/// gate (SQLite is single-writer). The genres/tags JSON columns are (de)serialized here so the rest of the
/// code works with <c>string[]</c>.
/// </summary>
public sealed class RawgStore(IServiceScopeFactory scopeFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <summary>Create the schema if absent (idempotent EnsureCreated — see <see cref="AppDbContext"/>). Creating
    /// the whole model covers the audit/integrations tables too, so this is safe to call independently.</summary>
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

    /// <summary>All cached rows keyed by blueprint id — the per-request read the aggregator joins against.</summary>
    public async Task<IReadOnlyDictionary<string, RawgEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<RawgEntry> rows = await db.RawgEntries.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.BlueprintId, StringComparer.Ordinal);
    }

    /// <summary>One cached row, or null.</summary>
    public async Task<RawgEntry?> GetAsync(string blueprintId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RawgEntries.AsNoTracking()
            .FirstOrDefaultAsync(r => r.BlueprintId == blueprintId, ct).ConfigureAwait(false);
    }

    /// <summary>Upsert a row (serialized; SQLite single-writer).</summary>
    public async Task UpsertAsync(RawgEntry row, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            RawgEntry? existing = await db.RawgEntries
                .FirstOrDefaultAsync(r => r.BlueprintId == row.BlueprintId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                db.RawgEntries.Add(row);
            }
            else
            {
                existing.Slug = row.Slug;
                existing.Description = row.Description;
                existing.Genres = row.Genres;
                existing.Tags = row.Tags;
                existing.CoverFile = row.CoverFile;
                existing.HeroFile = row.HeroFile;
                existing.CoverEtag = row.CoverEtag;
                existing.HeroEtag = row.HeroEtag;
                existing.Released = row.Released;
                existing.Rating = row.Rating;
                existing.Website = row.Website;
                existing.FetchedAt = row.FetchedAt;
                existing.Status = row.Status;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Serialize a string list to the JSON column form (never null — <c>"[]"</c> when empty, so an
    /// empty array is preserved on the wire, never a fabricated null).</summary>
    public static string SerializeList(IReadOnlyList<string> items) => JsonSerializer.Serialize(items, Json);

    /// <summary>Deserialize a JSON-column string list; null/blank/garbage → empty array (honest <c>[]</c>).</summary>
    public static IReadOnlyList<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }
}
