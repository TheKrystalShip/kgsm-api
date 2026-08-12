using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Integrations.WebPush;

/// <summary>
/// Stages the operations behind a notification's buttons and redeems them exactly once.
/// </summary>
/// <remarks>
/// <para>
/// The same model the assistant's <c>pending_confirmations</c> uses: the resolved operation is held
/// here and a device is given nothing but an opaque handle. What would be done never leaves this
/// process, so a request cannot describe an action — it can only name one.
/// </para>
/// <para>
/// <b>Redemption is single-use, and a mismatch is not.</b> A handle presented with the wrong device is
/// left standing rather than consumed: a wrong guess must not be able to destroy an action its owner is
/// about to tap. Everything else — unknown, expired, already redeemed — is one answer, because none of
/// them is an operation to run and telling them apart only helps somebody probing.
/// </para>
/// <para>
/// Same schema posture as its siblings here: EnsureCreated covers a fresh DB, an idempotent
/// <c>CREATE TABLE IF NOT EXISTS</c> covers the already-deployed one, because EnsureCreated no-ops
/// against an existing database and the shared audit log must never be wiped to add a table.
/// </para>
/// </remarks>
public sealed class PushActionStore(IServiceScopeFactory scopeFactory)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <summary>How long a staged action stays redeemable. A button on a notification is answered in
    /// the minutes after it arrives; beyond that the fact it was staged against has usually moved.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

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
                CREATE TABLE IF NOT EXISTS push_actions (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_push_actions" PRIMARY KEY,
                    "Kind" TEXT NOT NULL,
                    "Target" TEXT NOT NULL,
                    "Subject" TEXT NULL,
                    "UserHandle" TEXT NOT NULL,
                    "Username" TEXT NULL,
                    "Endpoint" TEXT NOT NULL,
                    "Label" TEXT NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "ExpiresAt" INTEGER NOT NULL
                );
                """, ct).ConfigureAwait(false);

            // And the same table on a host that already has it: CREATE TABLE IF NOT EXISTS is a no-op there,
            // so a column added later arrives this way or not at all. SQLite has no ADD COLUMN IF NOT
            // EXISTS, so the duplicate-column error IS the "already applied" answer.
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """ALTER TABLE push_actions ADD COLUMN "Subject" TEXT NULL;""", ct).ConfigureAwait(false);
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* already there */ }

            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>Stage one operation and return the handle that redeems it.</summary>
    /// <param name="subject">Who inside <paramref name="target"/> it acts on, for the kinds that need one.</param>
    public async Task<string> StageAsync(
        string kind, string target, string userHandle, string? username, string endpoint, string label,
        string? subject = null, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = NewHandle();

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Opportunistic cleanup, inline with the write: staging happens far less often than the
            // table is read, so this needs no timer of its own.
            await db.PushActions.Where(a => a.ExpiresAt < now).ExecuteDeleteAsync(ct).ConfigureAwait(false);

            db.PushActions.Add(new PushActionEntity
            {
                Id = id,
                Kind = kind,
                Target = target,
                Subject = subject,
                UserHandle = userHandle,
                Username = username,
                Endpoint = endpoint,
                Label = label,
                CreatedAt = now,
                ExpiresAt = now.Add(Lifetime),
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }

        return id;
    }

    /// <summary>
    /// Redeem <paramref name="id"/> on behalf of the device at <paramref name="endpoint"/>.
    /// </summary>
    /// <returns>The staged operation, or <see langword="null"/> for an unknown, expired, already-redeemed
    /// or wrong-device handle alike — a caller cannot tell which, and none of them is a thing to run.</returns>
    public async Task<PushActionEntity?> TakeAsync(string? id, string? endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(endpoint)) return null;
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            PushActionEntity? row = await db.PushActions
                .FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
            if (row is null) return null;

            // Somebody else's device leaves the row exactly as it was — refusing costs the caller
            // nothing, where consuming it would cancel an action its owner is looking at.
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(row.Endpoint),
                    System.Text.Encoding.UTF8.GetBytes(endpoint)))
                return null;

            // Otherwise single-use regardless of outcome: delete first, so an expired row cannot linger
            // for a second attempt and a redeemed one is never redeemed twice.
            db.PushActions.Remove(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (row.ExpiresAt < DateTimeOffset.UtcNow) return null;
            if (!PushActionKind.IsKnown(row.Kind)) return null; // a row this build cannot act on

            return row;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>16 bytes from the cryptographic RNG, hex-encoded. The handle IS the capability, so it is
    /// unguessable by construction and says nothing about what it redeems.</summary>
    private static string NewHandle()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
