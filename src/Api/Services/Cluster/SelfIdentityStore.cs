using Microsoft.EntityFrameworkCore;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// What this node knows about itself: the addresses it answers at, and the browser origins an admin has
/// signed in from (<c>PLAN-peers.md</c> §2 #13b, P0.6).
/// <para>
/// A node cannot determine its own public address — behind NAT, a reverse proxy or a load balancer, the
/// address it is reached at leaves no local trace. So the addresses here are <b>reflected</b>: whoever
/// demonstrably reached this node reports where they reached it, and that statement is recorded. Two
/// sources carry weight, and both are browser-reachable by construction — the URL an operator pasted into
/// a panel (which a peer then proved answers), and the scheme and host a browser arrived on.
/// </para>
/// <para>
/// Configuration still wins when it is present: <c>Api__PublicBaseUrl</c> — this host's public address
/// behind a reverse proxy, which is the same fact a peer needs — and <c>Api__ClusterGossipUrl</c>, an
/// address only other nodes use, are merged in ahead of anything learned. That is what lets an operator
/// correct a topology where reflection reports the wrong thing. Both are read from options on every
/// resolve rather than copied into the table, so config stays the single statement of itself.
/// </para>
/// </summary>
public sealed class SelfIdentityStore(IServiceScopeFactory scopeFactory, ApiOptions options)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    // Reference assignment is atomic; a racing read may briefly see the prior list and converges on the
    // next read. The origins cache exists because CORS consults it on the request path.
    private IReadOnlyList<SelfFactEntity>? _facts;

    /// <summary>Provenance of an address a browser arrived on. It IS a browser-reachable address by
    /// construction, which is exactly what the roster's client URL needs.</summary>
    public const string BrowserObserved = "observed";

    /// <summary>Trust order for candidates. An operator's pasted URL is the strongest address statement in
    /// the system: a human wrote it down and a peer proved it answers. A peer-observed source address is
    /// the weakest — it is a socket address, not a URL, and nothing is allowed to depend on it.</summary>
    private static int Rank(string provenance) => provenance switch
    {
        "config-client" => 0,
        "operator" => 1,
        "observed" => 2,
        "config-node" => 3,
        _ => 4,
    };

    /// <summary>Normalise an address for comparison and storage: no trailing slash, scheme and host
    /// lower-cased, default ports dropped. Returns null when the input is not an absolute http(s) URL —
    /// an unusable address is discarded, never stored in a mangled form.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var builder = new UriBuilder(uri) { Path = "", Query = "", Fragment = "" };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    /// <summary>
    /// This node's own address candidates, most-trusted first: the configured overrides, then everything
    /// reflected onto it, de-duplicated by normalised address. Empty is an honest answer — a node nobody
    /// has reached yet and that carries no configured address knows of no way to be called, and says so
    /// rather than inventing one.
    /// </summary>
    public async Task<IReadOnlyList<NodeCandidate>> CandidatesAsync(CancellationToken ct)
    {
        var seen = new Dictionary<string, NodeCandidate>(StringComparer.OrdinalIgnoreCase);

        void Offer(string? raw, bool client)
        {
            string? url = Normalize(raw);
            if (url is null || seen.ContainsKey(url)) return;
            seen[url] = new NodeCandidate(url, client);
        }

        Offer(options.PublicBaseUrl, client: true);
        Offer(options.ClusterGossipUrl, client: false);

        foreach (SelfFactEntity fact in await FactsAsync(ct).ConfigureAwait(false))
        {
            if (!string.Equals(fact.Kind, SelfFactKinds.Candidate, StringComparison.Ordinal)) continue;
            Offer(fact.Value, fact.Client);
        }

        return [.. seen.Values];
    }

    /// <summary>Every browser origin an admin has signed in from. Consulted by CORS, so it is served from
    /// the in-memory cache rather than the DB on the request path.</summary>
    public async Task<IReadOnlyList<string>> PanelOriginsAsync(CancellationToken ct)
    {
        IReadOnlyList<SelfFactEntity> facts = await FactsAsync(ct).ConfigureAwait(false);
        return [.. facts
            .Where(f => string.Equals(f.Kind, SelfFactKinds.Origin, StringComparison.Ordinal))
            .Select(f => f.Value)];
    }

    /// <summary>
    /// Load what this node knows about itself into memory. Called once at startup, because the CORS check
    /// reads the cache synchronously on the request path and a cold cache reads as "nothing learned" — which
    /// would leave a node that HAS learned an origin answering as though it had not.
    /// </summary>
    public Task PrimeAsync(CancellationToken ct) => FactsAsync(ct);

    /// <summary>The cached origins, or null when nothing has been loaded yet. Lets the CORS check answer
    /// synchronously without blocking a request thread on the DB; a cold cache falls through to the
    /// configured allowlist, which is the same answer the node gave before it learned anything.</summary>
    public IReadOnlyList<string>? CachedPanelOrigins() =>
        _facts is null
            ? null
            : [.. _facts
                .Where(f => string.Equals(f.Kind, SelfFactKinds.Origin, StringComparison.Ordinal))
                .Select(f => f.Value)];

    /// <summary>Record an address this node was reached at. Re-recording refreshes the row's
    /// <see cref="SelfFactEntity.LastSeen"/> rather than adding a second one.</summary>
    public Task RecordCandidateAsync(string url, bool client, string provenance, CancellationToken ct) =>
        RecordAsync(SelfFactKinds.Candidate, url, client, provenance, ct);

    /// <summary>Record a browser origin an admin signed in from.</summary>
    public Task RecordPanelOriginAsync(string origin, CancellationToken ct) =>
        RecordAsync(SelfFactKinds.Origin, origin, client: true, BrowserObserved, ct);

    private async Task RecordAsync(string kind, string raw, bool client, string provenance, CancellationToken ct)
    {
        string? value = Normalize(raw);
        if (value is null) return;

        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            string id = kind + ":" + value;
            SelfFactEntity? row = await db.SelfFacts.FirstOrDefaultAsync(f => f.Id == id, ct).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (row is null)
            {
                db.SelfFacts.Add(new SelfFactEntity
                {
                    Id = id,
                    Kind = kind,
                    Value = value,
                    Client = client,
                    Provenance = provenance,
                    LastSeen = now,
                });
            }
            else
            {
                row.LastSeen = now;
                // A stronger statement upgrades a weaker one: an address first seen as a browser host and
                // later pasted by an operator is thereafter an operator address.
                if (Rank(provenance) < Rank(row.Provenance))
                {
                    row.Provenance = provenance;
                    row.Client = client;
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _facts = null;
        }
        finally { _writeGate.Release(); }
    }

    private async Task<IReadOnlyList<SelfFactEntity>> FactsAsync(CancellationToken ct)
    {
        if (_facts is { } cached) return cached;

        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<SelfFactEntity> rows = await db.SelfFacts
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        rows.Sort((a, b) =>
        {
            int byRank = Rank(a.Provenance).CompareTo(Rank(b.Provenance));
            return byRank != 0 ? byRank : b.LastSeen.CompareTo(a.LastSeen);
        });
        _facts = rows;
        return rows;
    }

    // EnsureCreated (fresh DB: the whole model incl. node_self) + an idempotent CREATE TABLE IF NOT EXISTS
    // (existing DB: the no-op above skipped our new table). Columns match SelfFactEntity/EF's mapping.
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
                CREATE TABLE IF NOT EXISTS node_self (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_node_self" PRIMARY KEY,
                    "Kind" TEXT NOT NULL,
                    "Value" TEXT NOT NULL,
                    "Client" INTEGER NOT NULL,
                    "Provenance" TEXT NOT NULL,
                    "LastSeen" INTEGER NOT NULL
                );
                """, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE INDEX IF NOT EXISTS "IX_node_self_Kind" ON "node_self" ("Kind");
                """, ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }
}
