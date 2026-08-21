using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Preferences;

/// <summary>One preference as it is read back — the value plus the provenance a merge and a settings
/// card need.</summary>
public sealed record PreferenceRow(
    string Key, string Value, long Version, string OriginDevice, DateTimeOffset Updated);

/// <summary>Whether an account's preferences follow the person, and which device decided that.
/// <paramref name="SourceDevice"/> and <paramref name="Updated"/> are <see langword="null"/> for an
/// account that has never touched the switch — honest absence, not a fabricated device.</summary>
public sealed record SyncState(bool Enabled, string? SourceDevice, DateTimeOffset? Updated);

/// <summary>
/// The general per-account preference store: what a person has set, per device, with an account-level
/// switch that makes one device's set authoritative for all of them.
/// <para>
/// <b>It knows nothing about what a preference means.</b> Keys are opaque strings and values are JSON
/// text handed straight back, so a new preference — a dashboard layout, a palette, a density — is zero
/// work here. The moment this store learns what a widget is, every new one becomes a backend change.
/// </para>
/// <para>
/// Same posture as <see cref="Integrations.WebPush.PushPreferenceStore"/>: a singleton owning a scope
/// per operation, writes behind a gate, and an idempotent <c>CREATE TABLE IF NOT EXISTS</c> beside
/// <c>EnsureCreated</c> so the tables also appear on an already-deployed database.
/// </para>
/// </summary>
public sealed class UserPreferenceStore(IServiceScopeFactory scopeFactory)
{
    /// <summary>The synced record's device slot — the row every device reads while sync is on. No
    /// client can name it: a device id is rejected before it reaches here unless it is non-empty.</summary>
    public const string SyncedSlot = "";

    /// <summary>
    /// How many distinct keys one slot may hold. A bound rather than a limit anybody will meet: the
    /// panel's own set is a handful, and without one an authenticated caller can grow a table nothing
    /// ever reads.
    /// </summary>
    public const int MaxKeysPerSlot = 100;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    private async Task<AppDbContext> OpenAsync(IServiceScope scope, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_ensured) return;
        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured) return;
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS user_preferences (
                    "UserId" TEXT NOT NULL,
                    "DeviceId" TEXT NOT NULL,
                    "Key" TEXT NOT NULL,
                    "Value" TEXT NOT NULL,
                    "Version" INTEGER NOT NULL,
                    "OriginDevice" TEXT NOT NULL,
                    "Updated" INTEGER NOT NULL,
                    CONSTRAINT "PK_user_preferences" PRIMARY KEY ("UserId", "DeviceId", "Key")
                );
                """, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE INDEX IF NOT EXISTS "IX_user_preferences_UserId_Key"
                    ON user_preferences ("UserId", "Key");
                """, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS user_sync (
                    "UserId" TEXT NOT NULL,
                    "Enabled" INTEGER NOT NULL,
                    "SourceDevice" TEXT NOT NULL,
                    "Updated" INTEGER NOT NULL,
                    CONSTRAINT "PK_user_sync" PRIMARY KEY ("UserId")
                );
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>The account's switch. An account with no row has never touched it, which is off.</summary>
    public async Task<SyncState> SyncStateAsync(string userId, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = await OpenAsync(scope, ct).ConfigureAwait(false);
        UserSyncEntity? row = await db.UserSync.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct).ConfigureAwait(false);
        return ToState(row);
    }

    private static SyncState ToState(UserSyncEntity? row) =>
        row is null
            ? new SyncState(false, null, null)
            : new SyncState(row.Enabled, row.SourceDevice.Length == 0 ? null : row.SourceDevice, row.Updated);

    /// <summary>
    /// What <paramref name="deviceId"/> should be reading: the synced record when the account's switch
    /// is on, this device's own rows when it is off.
    /// </summary>
    public async Task<(SyncState Sync, IReadOnlyList<PreferenceRow> Rows)> EffectiveAsync(
        string userId, string deviceId, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = await OpenAsync(scope, ct).ConfigureAwait(false);
        SyncState sync = ToState(await db.UserSync.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct).ConfigureAwait(false));
        string slot = sync.Enabled ? SyncedSlot : deviceId;
        return (sync, await SlotAsync(db, userId, slot, ct).ConfigureAwait(false));
    }

    /// <summary>One slot's rows, whatever the switch says — the device's own set, or the synced record
    /// when handed <see cref="SyncedSlot"/>.</summary>
    public async Task<IReadOnlyList<PreferenceRow>> SlotAsync(
        string userId, string deviceId, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = await OpenAsync(scope, ct).ConfigureAwait(false);
        return await SlotAsync(db, userId, deviceId, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PreferenceRow>> SlotAsync(
        AppDbContext db, string userId, string deviceId, CancellationToken ct) =>
        await db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId && p.DeviceId == deviceId)
            .OrderBy(p => p.Key)
            .Select(p => new PreferenceRow(p.Key, p.Value, p.Version, p.OriginDevice, p.Updated))
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>Every device this account has ever written a preference from. The synced slot is not a
    /// device and is excluded.</summary>
    public async Task<IReadOnlyList<string>> DevicesAsync(string userId, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = await OpenAsync(scope, ct).ConfigureAwait(false);
        return await DevicesAsync(db, userId, ct).ConfigureAwait(false);
    }

    private static async Task<List<string>> DevicesAsync(AppDbContext db, string userId, CancellationToken ct) =>
        await db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId && p.DeviceId != SyncedSlot)
            .Select(p => p.DeviceId).Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Write one preference for <paramref name="deviceId"/> — or for the synced record, when the
    /// account's switch is on, because that is what every device is reading.
    /// </summary>
    /// <returns>
    /// The row as stored, or <see langword="null"/> when the slot already holds
    /// <see cref="MaxKeysPerSlot"/> distinct keys and this is a new one.
    /// </returns>
    public async Task<PreferenceRow?> SetAsync(
        string userId, string deviceId, string key, string valueJson, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = await OpenAsync(scope, ct).ConfigureAwait(false);
            SyncState sync = ToState(await db.UserSync.AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == userId, ct).ConfigureAwait(false));
            string slot = sync.Enabled ? SyncedSlot : deviceId;

            long version = await NextVersionAsync(db, userId, key, ct).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            UserPreferenceEntity? row = await db.UserPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId && p.DeviceId == slot && p.Key == key, ct)
                .ConfigureAwait(false);

            if (row is null)
            {
                int held = await db.UserPreferences
                    .CountAsync(p => p.UserId == userId && p.DeviceId == slot, ct).ConfigureAwait(false);
                if (held >= MaxKeysPerSlot) return null;

                row = new UserPreferenceEntity { UserId = userId, DeviceId = slot, Key = key };
                db.UserPreferences.Add(row);
            }

            row.Value = valueJson;
            row.Version = version;
            // The ORIGIN is always the device that made the call, never the slot it landed in: a write
            // that goes to the synced record still came from one machine, and that is the tiebreak a
            // peer needs when two nodes reach the same version.
            row.OriginDevice = deviceId;
            row.Updated = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return new PreferenceRow(key, row.Value, row.Version, row.OriginDevice, row.Updated);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// The next version for <c>(userId, key)</c> — one past the highest that key holds in any slot, so
    /// the counter is monotonic across every device rather than per row.
    /// </summary>
    private static async Task<long> NextVersionAsync(
        AppDbContext db, string userId, string key, CancellationToken ct)
    {
        // Max over an empty set is null under SQL, hence the nullable projection; a key nobody has ever
        // written starts at 1.
        long? highest = await db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId && p.Key == key)
            .MaxAsync(p => (long?)p.Version, ct).ConfigureAwait(false);
        return (highest ?? 0) + 1;
    }

    /// <summary>
    /// Turn sync on, from <paramref name="sourceDevice"/>: that device's set becomes the synced record
    /// and overwrites every other device's rows, and writes from then on land on the synced record.
    /// </summary>
    /// <remarks>
    /// The overwrite is deliberate rather than a cleanup. Somebody switching this on is saying "this
    /// machine's arrangement is the one I want", and leaving the others as they were would mean turning
    /// sync off later hands each device back a set nobody chose.
    /// <para/>
    /// A source device with nothing stored is the one case that does not overwrite: there is nothing to
    /// copy, and emptying every other device because a fresh browser flipped a switch would destroy
    /// exactly what turning it off again is supposed to give back. The synced record is empty either
    /// way, so nothing reads differently while it is on.
    /// </remarks>
    public async Task<SyncState> EnableSyncAsync(
        string userId, string sourceDevice, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = await OpenAsync(scope, ct).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            IReadOnlyList<PreferenceRow> source =
                await SlotAsync(db, userId, sourceDevice, ct).ConfigureAwait(false);

            if (source.Count > 0)
            {
                List<string> targets = [SyncedSlot,
                    .. (await DevicesAsync(db, userId, ct).ConfigureAwait(false))
                        .Where(d => !string.Equals(d, sourceDevice, StringComparison.Ordinal))];
                await CopyIntoAsync(db, userId, source, targets, sourceDevice, now, ct).ConfigureAwait(false);
            }

            SyncState state = await SetSwitchAsync(db, userId, true, sourceDevice, now, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return state;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Turn sync off, from <paramref name="callingDevice"/>: every known device — the caller included —
    /// is seeded from the synced record first, so nobody lands on an empty dashboard the moment the
    /// switch moves.
    /// </summary>
    /// <remarks>
    /// The synced record itself is kept. It is the slot sync writes to and nothing else reads while the
    /// switch is off, so deleting it would only cost the next enable its starting point.
    /// </remarks>
    public async Task<SyncState> DisableSyncAsync(
        string userId, string callingDevice, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = await OpenAsync(scope, ct).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            IReadOnlyList<PreferenceRow> synced =
                await SlotAsync(db, userId, SyncedSlot, ct).ConfigureAwait(false);

            if (synced.Count > 0)
            {
                List<string> devices = await DevicesAsync(db, userId, ct).ConfigureAwait(false);
                if (!devices.Contains(callingDevice, StringComparer.Ordinal))
                    devices.Add(callingDevice);
                await CopyIntoAsync(db, userId, synced, devices, callingDevice, now, ct).ConfigureAwait(false);
            }

            SyncState state = await SetSwitchAsync(db, userId, false, SyncedSlot, now, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return state;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Make each of <paramref name="targets"/> hold exactly <paramref name="source"/> — upserting what
    /// the source has and dropping what it does not.
    /// </summary>
    /// <remarks>
    /// Every copy of a key gets ONE new version, shared. Bumping per target would make the last device
    /// written the winner of a merge it took no part in, and the point of the copy is that they all
    /// agree.
    /// </remarks>
    private static async Task CopyIntoAsync(
        AppDbContext db, string userId, IReadOnlyList<PreferenceRow> source, IReadOnlyList<string> targets,
        string originDevice, DateTimeOffset now, CancellationToken ct)
    {
        if (targets.Count == 0) return;

        Dictionary<string, long> versions = [];
        foreach (PreferenceRow row in source)
            versions[row.Key] = await NextVersionAsync(db, userId, row.Key, ct).ConfigureAwait(false);

        HashSet<string> keep = [.. source.Select(r => r.Key)];

        foreach (string target in targets)
        {
            List<UserPreferenceEntity> existing = await db.UserPreferences
                .Where(p => p.UserId == userId && p.DeviceId == target)
                .ToListAsync(ct).ConfigureAwait(false);

            db.UserPreferences.RemoveRange(existing.Where(p => !keep.Contains(p.Key)));

            foreach (PreferenceRow row in source)
            {
                UserPreferenceEntity? entity = existing.Find(p => p.Key == row.Key);
                if (entity is null)
                {
                    entity = new UserPreferenceEntity { UserId = userId, DeviceId = target, Key = row.Key };
                    db.UserPreferences.Add(entity);
                }
                entity.Value = row.Value;
                entity.Version = versions[row.Key];
                entity.OriginDevice = originDevice;
                entity.Updated = now;
            }
        }
    }

    private static async Task<SyncState> SetSwitchAsync(
        AppDbContext db, string userId, bool enabled, string sourceDevice, DateTimeOffset now,
        CancellationToken ct)
    {
        UserSyncEntity? row = await db.UserSync
            .FirstOrDefaultAsync(s => s.UserId == userId, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new UserSyncEntity { UserId = userId };
            db.UserSync.Add(row);
        }
        row.Enabled = enabled;
        row.SourceDevice = sourceDevice;
        row.Updated = now;
        return ToState(row);
    }
}
