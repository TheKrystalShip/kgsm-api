using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TheKrystalShip.Api.Data;

/// <summary>
/// EF Core context for the API's own operational metadata — the small set of tables the
/// aggregator persists (the domain itself is live-scraped from the leaves, never stored).
/// As of M5 it holds the single append-only <see cref="AuditEntry"/> table (architecture.html
/// §3·d). M4 auth is stateless JWT — no session/user rows.
/// <para>
/// <b>Schema is created via <c>EnsureCreated</c>, not EF migrations.</b> The project has
/// greenfield/dev authority: on a schema change we wipe the dev DB rather than carry a
/// migration history (PLAN.md M5). <c>EnsureCreated</c> is a no-op against an existing DB,
/// so a schema change means deleting the DB file — there is no <c>__EFMigrationsHistory</c>.
/// </para>
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    /// <summary>M8·c — outbound-notification integration config, one row per provider (§3·e). The
    /// first non-audit table; created by the same <c>EnsureCreated</c> (delete the dev DB once when it
    /// lands — EnsureCreated no-ops on an existing DB).</summary>
    public DbSet<IntegrationEntity> Integrations => Set<IntegrationEntity>();

    /// <summary>The library RAWG.io metadata cache, one row per blueprint (cover/hero/description/genres/
    /// tags — the M8·a library increment). Created by the same <c>EnsureCreated</c>; the deployed DB needs a
    /// one-time wipe when it lands.</summary>
    public DbSet<RawgEntry> RawgEntries => Set<RawgEntry>();

    /// <summary>This host's operator-editable identity overrides (region/label) — one row, keyed by host id.
    /// Mapped here so EF reads/writes it; on an EXISTING DB it is instead created by
    /// <see cref="Services.Aggregation.HostSettingsStore"/>'s idempotent <c>CREATE TABLE IF NOT EXISTS</c>
    /// (EnsureCreated no-ops there, and we must not wipe the shared audit log).</summary>
    public DbSet<HostSettingsEntity> HostSettings => Set<HostSettingsEntity>();

    /// <summary>Each provisionable leaf's runtime provisioning flag (the leaf-runtime-provisioning feature) —
    /// one row per leaf, keyed by leaf id. Like <see cref="HostSettings"/> it is also created by
    /// <see cref="Services.Leaves.LeafRegistry"/>'s idempotent <c>CREATE TABLE IF NOT EXISTS</c> on an existing DB.</summary>
    public DbSet<LeafRegistryEntity> LeafRegistryEntries => Set<LeafRegistryEntity>();

    /// <summary>Per-leaf config overrides (the leaf-runtime-config feature) — composite-keyed by (leaf,key).
    /// Created by <see cref="Services.Leaves.LeafOverrideStore"/>'s idempotent <c>CREATE TABLE IF NOT EXISTS</c>
    /// on an existing DB (EnsureCreated no-ops there).</summary>
    public DbSet<LeafOverrideEntity> LeafOverrides => Set<LeafOverrideEntity>();

    /// <summary>The permanent player roster — one row per unique player per server.
    /// Once a player connects they are never removed; status toggles between online/offline/banned/unknown.
    /// Created by the same <c>EnsureCreated</c>; the deployed DB needs a one-time wipe when it lands.</summary>
    public DbSet<PlayerRecord> PlayerHistory => Set<PlayerRecord>();

    /// <summary>The M4·c session registry — one row per (login × device), keyed by the JWT <c>sid</c>
    /// claim (see <see cref="SessionEntry"/>). The authority the per-request validator reads (cached)
    /// to decide "is this session still alive" — what the stateless JWT alone cannot answer. Created
    /// automatically by <c>EnsureCreated</c> on a fresh DB (registered in <see cref="OnModelCreating"/>);
    /// on the already-deployed DB the table is added by a one-shot <c>sqlite3</c> command (D11), so
    /// the audit log is untouched. See <c>Services/Auth/CLAUDE.md</c>.</summary>
    public DbSet<SessionEntry> Sessions => Set<SessionEntry>();

    /// <summary>The cluster message bus's transactional outbox (Phase 1 foundation — see
    /// <see cref="OutboxMessage"/>; <c>docs/cluster-message-bus-plan.md §5</c>). One row per
    /// (message, target) delivery. Created automatically by <c>EnsureCreated</c> on a fresh DB
    /// (registered in <see cref="OnModelCreating"/>); on an already-deployed DB the table needs a
    /// one-shot creation the same way the session registry did (D11), never a wipe of the shared
    /// audit log. No writer exists yet — the enqueue path (<c>IClusterBus</c>) and the drainer are
    /// later phases.</summary>
    public DbSet<OutboxMessage> ClusterOutbox => Set<OutboxMessage>();

    /// <summary>The cluster message bus's inbox dedupe ledger (Phase 1 foundation — see
    /// <see cref="InboxMessage"/>; <c>docs/cluster-message-bus-plan.md §5</c>). One row per received
    /// envelope id. Same creation posture as <see cref="ClusterOutbox"/>. No writer exists yet — the
    /// <c>/peers/inbox</c> endpoint and its dispatch handler are later phases.</summary>
    public DbSet<InboxMessage> ClusterInbox => Set<InboxMessage>();

    /// <summary>The cluster membership roster (the peer-foundation milestone — see <see cref="PeerEntity"/>;
    /// <c>PLAN-peers.md §2</c> #12, P0). One row per known peer, this node's own copy (the mesh is
    /// masterless — there is no shared roster table). Created automatically by <c>EnsureCreated</c> on a
    /// fresh DB (registered in <see cref="OnModelCreating"/>); on an already-deployed DB the table is added
    /// by <see cref="Services.Cluster.PeersStore"/>'s idempotent <c>CREATE TABLE IF NOT EXISTS</c>, the same
    /// posture as <see cref="LeafRegistryEntries"/>/<see cref="HostSettings"/> — never a wipe of the shared
    /// audit log.</summary>
    public DbSet<PeerEntity> Peers => Set<PeerEntity>();

    /// <summary>Web Push subscriptions — one row per (user × device), keyed by the push service's
    /// endpoint URL. The shape that does not fit <see cref="Integrations"/>: an integration holds one
    /// host-wide secret, push holds a per-device credential the browser mints. Created by
    /// <c>EnsureCreated</c> on a fresh DB and by <see cref="Services.Integrations.WebPush.PushSubscriptionStore"/>'s
    /// idempotent <c>CREATE TABLE IF NOT EXISTS</c> on the already-deployed one — never a wipe of the
    /// shared audit log.</summary>
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();

    /// <summary>Per-account push preferences — which catalog events a person wants on their devices,
    /// keyed by (account, event). Holds only explicit choices: no row means the default, which is ON.
    /// Created by <c>EnsureCreated</c> on a fresh DB and by
    /// <see cref="Services.Integrations.WebPush.PushPreferenceStore"/>'s idempotent
    /// <c>CREATE TABLE IF NOT EXISTS</c> on an existing one.</summary>
    public DbSet<PushPreferenceEntity> PushPreferences => Set<PushPreferenceEntity>();

    /// <summary>Actions staged for a notification's buttons — the operation a tap redeems, held here so
    /// the device only ever carries an opaque handle to it. Same creation posture as the two above.</summary>
    public DbSet<PushActionEntity> PushActions => Set<PushActionEntity>();

    /// <summary>Per-account, per-condition push snoozes, keyed by (account, condition). Expiring rows
    /// only. Same creation posture as the two above.</summary>
    public DbSet<PushSnoozeEntity> PushSnoozes => Set<PushSnoozeEntity>();

    /// <summary>Per-account quiet windows — when somebody does not want waking, and what still wakes them.
    /// Same creation posture as the rest of the push tables.</summary>
    public DbSet<PushQuietHoursEntity> PushQuietHours => Set<PushQuietHoursEntity>();

    /// <summary>Facts held back for a provider whose rule for them says <c>digest</c>. Durable because a
    /// summary lost to a restart was never delivered and never reported undelivered.</summary>
    public DbSet<NotificationDigestEntity> NotificationDigests => Set<NotificationDigestEntity>();

    /// <summary>One verb applied to a set of this host's servers (see <see cref="BatchEntity"/>). Durable
    /// because the work outlives both the request that asked for it and the client that watched: a batch
    /// paced two at a time runs for as long as the servers take, and a client is free to leave. Created by
    /// <c>EnsureCreated</c> on a fresh DB and by <see cref="Services.Commands.BatchStore"/>'s idempotent
    /// <c>CREATE TABLE IF NOT EXISTS</c> on an existing one — never a wipe of the shared audit log.</summary>
    public DbSet<BatchEntity> Batches => Set<BatchEntity>();

    /// <summary>One server's place in a batch (see <see cref="BatchMemberEntity"/>), keyed by (batch, server).
    /// Same creation posture as <see cref="Batches"/>.</summary>
    public DbSet<BatchMemberEntity> BatchMembers => Set<BatchMemberEntity>();

    /// <summary>The general per-account preference store — one row per (account, device, key), plus the
    /// <c>""</c>-device row every device reads while sync is on. Keys are opaque here: the API stores
    /// what it is handed and knows nothing about what a widget or a palette is. Created by
    /// <c>EnsureCreated</c> on a fresh DB and by <see cref="Services.Preferences.UserPreferenceStore"/>'s
    /// idempotent <c>CREATE TABLE IF NOT EXISTS</c> on an existing one — never a wipe of the shared
    /// audit log.</summary>
    public DbSet<UserPreferenceEntity> UserPreferences => Set<UserPreferenceEntity>();

    /// <summary>One row per account: whether that person's preferences follow them across devices, and
    /// which device switched it on. Same creation posture as <see cref="UserPreferences"/>.</summary>
    public DbSet<UserSyncEntity> UserSync => Set<UserSyncEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BatchEntity>(e =>
        {
            e.ToTable("batches");
            e.HasKey(b => b.Id);
            // Timestamps as UTC ticks — the audit/host_settings posture (SQLite has no date type, and a
            // stored DateTimeOffset cannot be compared server-side without a converter).
            e.Property(b => b.CreatedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(b => b.SettledAt).HasConversion(
                v => v!.Value.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            // The two reads that exist: the active set (the worker's pump and GET /batches?active=true),
            // and a run's share on this node (a client reassembling a cluster-wide run by its runId).
            e.HasIndex(b => b.State);
            e.HasIndex(b => b.RunId);
        });

        modelBuilder.Entity<BatchMemberEntity>(e =>
        {
            e.ToTable("batch_members");
            e.HasKey(m => new { m.BatchId, m.ServerId });
            e.Property(m => m.SettledAt).HasConversion(
                v => v!.Value.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            // Every member read is "this batch's members", which the key's leading column already covers.
            // The state index serves the worker's one cross-batch question: what is still pending.
            e.HasIndex(m => m.State);
        });

        modelBuilder.Entity<UserPreferenceEntity>(e =>
        {
            e.ToTable("user_preferences");
            e.HasKey(p => new { p.UserId, p.DeviceId, p.Key });
            e.Property(p => p.Updated).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            // Every read is "this account's rows for one key" (the version bump) or "this account's
            // rows" (the effective set, the sync rewrites) — the key's leading column covers both, and
            // the per-key index covers the merge lookup without scanning an account's whole set.
            e.HasIndex(p => new { p.UserId, p.Key });
        });

        modelBuilder.Entity<UserSyncEntity>(e =>
        {
            e.ToTable("user_sync");
            e.HasKey(s => s.UserId);
            e.Property(s => s.Updated).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<PushActionEntity>(e =>
        {
            e.ToTable("push_actions");
            e.HasKey(p => p.Id);
            e.Property(p => p.CreatedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(p => p.ExpiresAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<NotificationDigestEntity>(e =>
        {
            e.ToTable("notification_digest");
            e.HasKey(d => d.Id);
            e.Property(d => d.Ts).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<PushQuietHoursEntity>(e =>
        {
            e.ToTable("push_quiet_hours");
            e.HasKey(q => q.UserSubject);
            e.Property(q => q.UpdatedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<PushSnoozeEntity>(e =>
        {
            e.ToTable("push_snoozes");
            e.HasKey(p => new { p.UserSubject, p.SubjectKey });
            e.Property(p => p.ExpiresAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<PushPreferenceEntity>(e =>
        {
            e.ToTable("push_preferences");
            e.HasKey(p => new { p.UserSubject, p.CatalogId });
            e.Property(p => p.UpdatedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<PushSubscriptionEntity>(e =>
        {
            e.ToTable("push_subscriptions");
            e.HasKey(p => p.Endpoint);
            // Timestamps as UTC ticks — the host_settings/audit posture (SQLite has no date type).
            e.Property(p => p.CreatedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(p => p.LastSeenAt).HasConversion(
                new ValueConverter<DateTimeOffset, long>(
                    v => v.UtcTicks,
                    v => new DateTimeOffset(v, TimeSpan.Zero)));
        });

        modelBuilder.Entity<IntegrationEntity>(e =>
        {
            e.ToTable("integrations");
            e.HasKey(i => i.Provider);
        });

        modelBuilder.Entity<RawgEntry>(e =>
        {
            e.ToTable("rawg_entry");
            e.HasKey(r => r.BlueprintId);
            // Store FetchedAt as UTC ticks (long) so the worker's 30-day-old comparison is a translatable
            // INTEGER >= (the same posture as AuditEntry.Ts — SQLite has no date type / EF can't translate a
            // DateTimeOffset comparison stored as TEXT). Round-trips to a UTC DateTimeOffset on read.
            e.Property(r => r.FetchedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<HostSettingsEntity>(e =>
        {
            e.ToTable("host_settings");
            e.HasKey(h => h.Id);
            // Store UpdatedAt as UTC ticks (long) — the same posture as AuditEntry.Ts / RawgEntry.FetchedAt
            // (SQLite has no date type). A ValueConverter on the non-nullable underlying type; EF composes
            // it with the property's nullability (NULL stays NULL). Round-trips to a UTC DateTimeOffset.
            e.Property(h => h.UpdatedAt).HasConversion(
                new ValueConverter<DateTimeOffset, long>(
                    v => v.UtcTicks,
                    v => new DateTimeOffset(v, TimeSpan.Zero)));
        });

        modelBuilder.Entity<LeafRegistryEntity>(e =>
        {
            e.ToTable("leaf_registry");
            e.HasKey(l => l.LeafId);
            // UpdatedAt as UTC ticks (long) — the host_settings/audit posture (SQLite has no date type).
            e.Property(l => l.UpdatedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<LeafOverrideEntity>(e =>
        {
            e.ToTable("leaf_override");
            e.HasKey(o => new { o.LeafId, o.Key }); // one row per (leaf, manifest key)
            e.Property(o => o.UpdatedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<PlayerRecord>(e =>
        {
            e.ToTable("player_history");
            e.HasKey(p => p.RowId);
            e.Property(p => p.RowId).ValueGeneratedOnAdd();

            // One row per (server, player identity) — the composite unique dedup key.
            e.HasIndex(p => new { p.ServerId, p.PlayerIdentity }).IsUnique();

            // Fast filter: "all online on this server", "all banned across servers", etc.
            e.HasIndex(p => new { p.ServerId, p.Status });

            // FirstSeen/LastSeen stored as UTC ticks (long) — same pattern as AuditEntry.Ts
            // (SQLite has no date type; ticks make >= comparisons translatable).
            e.Property(p => p.FirstSeen).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(p => p.LastSeen).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit");
            e.HasKey(a => a.RowId);
            // long key -> SQLite INTEGER PRIMARY KEY (a rowid alias), store-generated on insert.
            e.Property(a => a.RowId).ValueGeneratedOnAdd();
            e.HasIndex(a => a.Id).IsUnique();

            // Store Ts as UTC ticks (long). SQLite has no date type and EF Core can't translate a
            // DateTimeOffset `>=` comparison (it stores it as TEXT but emits no comparison SQL) — which
            // the `?since=` time-range filter needs. As ticks the comparison is a plain INTEGER `>=`,
            // fully translatable, and the value round-trips to a UTC DateTimeOffset on read (every audit
            // timestamp is UTC). Ordering is unaffected (keyset is on RowId, not Ts).
            e.Property(a => a.Ts).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));

            // Scope/filter indexes, each descending on RowId so a keyset page (RowId < cursor,
            // newest-first) is index-friendly — mirrors the §3·d CREATE INDEX … (col, rowid DESC) set.
            e.HasIndex(a => new { a.ServerId, a.RowId }).IsDescending(false, true);
            e.HasIndex(a => new { a.HostId, a.RowId }).IsDescending(false, true);
            e.HasIndex(a => new { a.ActorName, a.RowId }).IsDescending(false, true);
            e.HasIndex(a => new { a.Severity, a.RowId }).IsDescending(false, true);
        });

        // The session registry (see SessionEntry + Services/Auth/CLAUDE.md). Timestamps are stored as UTC ticks
        // (INTEGER) via ValueConverter — the same posture as AuditEntry.Ts/HostSettingsEntity.UpdatedAt
        // — because SQLite has no date type and EF can't translate a DateTimeOffset >= stored as TEXT,
        // which the per-request validator's `Expires > now` query needs (SQLite single-writer style: the
        // 5s-cached check bounds hot-path DB reads). The active-set query is WHERE UserId=? AND Revoked=0
        // AND Expires>now — ix_sessions_user covers it.
        modelBuilder.Entity<SessionEntry>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(s => s.Id);
            // Created/LastSeen/Expires as UTC ticks (long). Round-trips to a UTC DateTimeOffset on read.
            e.Property(s => s.Created).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(s => s.LastSeen).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(s => s.Expires).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(s => s.RevokedAt).HasConversion(
                new ValueConverter<DateTimeOffset, long>(
                    v => v.UtcTicks,
                    v => new DateTimeOffset(v, TimeSpan.Zero)));
            // The active-sessions lookup: WHERE UserId=? AND Revoked=? AND Expires>? — covered by the
            // composite index. A second index backs the GC worker's expired-row sweep (Expires < now).
            e.HasIndex(s => new { s.UserId, s.Revoked, s.Expires });
            e.HasIndex(s => s.Expires);
        });

        // Cluster message bus — Phase 1 foundation tables (docs/cluster-message-bus-plan.md §5).
        // Timestamps as UTC ticks (long) via ValueConverter, the same posture as every other table
        // here (SQLite has no date type; the drainer's/GC's due-scans need a translatable INTEGER
        // compare). No writer/reader exists yet — the drainer, IClusterBus, and the inbox endpoint
        // are later phases; these mappings just get the schema in place ahead of them.
        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("cluster_outbox");
            e.HasKey(o => o.Id);
            e.Property(o => o.NextAttemptAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(o => o.CreatedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(o => o.DeliveredAt).HasConversion(
                new ValueConverter<DateTimeOffset, long>(
                    v => v.UtcTicks,
                    v => new DateTimeOffset(v, TimeSpan.Zero)));
            // The drainer's due-scan: WHERE Status='pending' AND NextAttemptAt<=now, grouped by target.
            e.HasIndex(o => new { o.Status, o.NextAttemptAt });
            // The GC sweep: delivered/dead rows older than the retention window.
            e.HasIndex(o => new { o.Status, o.CreatedAt });
        });

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.ToTable("cluster_inbox");
            e.HasKey(i => i.Id);
            e.Property(i => i.ReceivedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(i => i.ProcessedAt).HasConversion(
                new ValueConverter<DateTimeOffset, long>(
                    v => v.UtcTicks,
                    v => new DateTimeOffset(v, TimeSpan.Zero)));
            // The GC sweep: rows older than the retention window (≥ the outbox retry TTL + margin).
            e.HasIndex(i => i.ReceivedAt);
        });

        // Cluster membership roster — the peer-foundation milestone (PLAN-peers.md §2 #12, P0). This
        // node's own copy of the mesh (masterless — no shared table). LastSeen as UTC ticks (long) via the
        // nullable-converter form (the HostSettingsEntity.UpdatedAt posture) — SQLite has no date type.
        modelBuilder.Entity<PeerEntity>(e =>
        {
            e.ToTable("peers");
            e.HasKey(p => p.Id);
            // The disable-list gate's and the outbox/poller's "enabled peers only" read.
            e.HasIndex(p => p.Enabled);
            e.Property(p => p.LastSeen).HasConversion(
                new ValueConverter<DateTimeOffset, long>(
                    v => v.UtcTicks,
                    v => new DateTimeOffset(v, TimeSpan.Zero)));
            // StateChangedAt — the gossip failure-timer clock (PLAN-peers.md §2·b, G5). Same nullable-ticks
            // posture as LastSeen; SQLite has no date type.
            e.Property(p => p.StateChangedAt).HasConversion(
                new ValueConverter<DateTimeOffset, long>(
                    v => v.UtcTicks,
                    v => new DateTimeOffset(v, TimeSpan.Zero)));
        });
    }
}
