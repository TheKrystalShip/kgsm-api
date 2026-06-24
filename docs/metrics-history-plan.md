# Metrics History & Aggregation — Milestone Plan (M9)

**Status:** Decisions LOCKED 2026-06-24 · build not started · **Scope:** kgsm-api only
· **Resolves:** the long-deferred "metrics-history store" open decision
(`PLAN.md §1` non-goals, `§5` open decisions, keystone O-ledger)

> **Cold-resume contract.** This doc is self-contained: a fresh session with no prior
> memory can read §0 (where we are) → §1–4 (why) → §5 (locked decisions) → §6 (the
> increments) and pick up exactly where the last one stopped. **Every increment ends in a
> Definition-of-Done; when you finish one, flip its box in §0 and write the commit/verify
> result into its "Done" line.** Trust §0's ledger over any assumption about progress.

---

## 0 · Progress ledger (update this as you go)

| # | Increment | Status | Done-marker (commit / verify result) |
|---|---|---|---|
| 1 | Durable write path — sampler → `metrics.db` Tier-1 raw | ☑ **BUILT** | tests 365 / smoke 68; uncommitted |
| 2 | Rollup + retention maintenance (Tier-1 → Tier-2, pruning) | ☑ **BUILT** | built alongside Increment 1 (shared store) |
| 3 | Read endpoint `GET /{servers,hosts}/{id}/metrics/history?range=` + contract freeze | ☑ **BUILT** | 9 endpoint tests + 3 smoke checks |
| 4 | In-memory hot ring (Tier-0) + startup warm *(optional polish)* | ☐ deferred | |
| 5 | Frontend gate — restore PerformanceTab range selectors on real history | ☑ **BUILT** | kgsm-web build green; range selector + history fetch + min/max band |

**Current state:** Increments 1–3 + 5 built, tested, 0-warn. Increment 4 deferred (optional optimization).
**Dependency note:** Increments 1→2→3 are strictly ordered (2 needs raw rows; 3 needs both
tiers). 4 is optional and can land any time after 1. 5 is the frontend gate after 3.

---

## 1 · The problem

The metrics path is **latest-frame-only, end to end**:

- **kgsm-monitor** is stateless: self-ticks ~1 Hz, samples `/proc`/`/sys`/cgroups, serves
  **only the latest** `Snapshot` over its unix socket. It keeps **no** history — and must
  not (§4).
- **kgsm-api** scrapes the latest frame (`MonitorClient.GetLatestAsync`), joins it with
  kgsm-lib domain truth (`ServerAggregator`), serves it on `GET /servers/{id}` + streams it
  on the `servers/{id}/metrics` / `hosts/{id}/metrics` WS topics. The internal `MetricsPump`
  (`src/Api/Realtime/MetricsPump.cs`, `PeriodicTimer` ~1 s) is the only poller — it pushes
  each frame to WS subscribers and then **discards it**.
- **kgsm-web** `PerformanceTab` is a **transient live rolling window**: seeds from the REST
  `metrics` block, appends each `metrics.tick`, **throws it away on unmount**. The
  1h/24h/7d/30d range selector + "compare to last week" were **removed** (2026-06-24) for
  lack of a source.

The gap: **any chart with a time axis longer than "since you opened the tab" has no
source.** This plan gives the API a durable history so those ranges come back honestly.

---

## 2 · What the data is

Per-server (`ServerMetrics`, `Monitor.Contracts`): `cpuPctCore` (double, % of one core,
uncapped), `memBytes` (long), `ioReadBps`/`ioWriteBps` (long?, **null** when cgroup `io`
off), `pids` (int), `diskBytes` (long?, ~60 s walk, null until walked).
Host (`Snapshot`): CPU total + loadavg, memory, per-mount disk + disk IO bps, per-interface
net rates, hwmon sensors. (Per-core CPU and sensor arrays are **excluded** from history —
trend-irrelevant and unbounded; persist scalar headline metrics only.)

Textbook time-series: regularly-sampled numeric points keyed `(entity, metric, ts)`,
**append-mostly, never updated, aged out.**

**Volume (homelab, ~10 servers + host):** at the 1 Hz *stream* cadence that's ~950 k
rows/day raw — which is exactly why we **persist coarse and roll up**, never store raw 1 Hz.
At the locked 15 s persist cadence, long schema: ~10 servers × 6 metrics × 5 760/day +
host ≈ **~400 k raw rows steady-state at 24 h retention** (fine for SQLite); Tier-2 5-min
buckets × 30 d ≈ **~600 k rollup rows steady-state**. Both bounded by the maintenance job.

---

## 3 · Industry standard (the transferable techniques)

Products: **RRDtool** (fixed-size circular file, automatic multi-resolution consolidation —
the pattern we copy), **Prometheus** (the de-facto modern standard, *pull/scrape* — which the
API already is in miniature — + remote downsampling, but a whole separate process + PromQL +
Grafana, overkill for one host), **InfluxDB / TimescaleDB / VictoriaMetrics** (purpose-built
TSDBs, each a separate service/dependency).

The universal techniques every one of them uses, which we adopt:

1. **Fixed-interval sampling** — regular cadence, so a range query knows its step.
2. **Downsampling / rollup** — aggregate raw into coarser buckets (avg + min + max + count)
   as data ages; a 30-day chart reads hundreds of rows, not millions.
3. **Multi-resolution retention tiers** — high-res recent, low-res old, drop oldest →
   storage permanently bounded.
4. **Gap-awareness** — a missed scrape is a **gap**, never a zero or a carried-forward stale
   value. (Maps 1:1 onto this ecosystem's never-fabricate invariant.)
5. **Query at a range-appropriate step** — the server picks the tier whose resolution matches
   the requested range so payloads stay bounded.

**We do not adopt a TSDB *product*** — we apply these five techniques in the smallest
self-contained way that fits a per-host control panel.

---

## 4 · Where history lives (and must not)

**Not the monitor.** It would violate keystone §4 (*"the monitor samples once and serves
latest"*, *"cadence/caching/persistence live in the consumers"*) and its consumer-agnostic
neutrality (retention is a *consumer's* product call). Monitor stays latest-only.

**The API — the correct home.** It is already the aggregator, already polls at 1 Hz
(`MetricsPump`), already owns the only SQLite/EF dependency, and invariant #5 already says it
*"persists its own operational metadata."* History taps the frame the pump **already
fetches** — no new poller, no new dependency, no new process.

---

## 5 · Locked decisions

| # | Decision | Value | Status |
|---|---|---|---|
| D1 | **Durability** | History **MUST survive an API restart** → SQLite is the system of record (in-memory is only an optimization on top) | **LOCKED** (user, 2026-06-24): *"otherwise the metrics are useless as a historical fact check"* |
| D2 | **Retention tiers** | **Tier-1 raw: 24 h @ 15 s** · **Tier-2 rolled-up: 30 d @ 5 min** (avg+min+max+n per bucket) | **LOCKED** (user, 2026-06-24) |
| D3 | **Storage medium** | **SQLite** (durable+queryable+already in the build) for Tiers 1–2; in-memory ring for Tier-0. **Not** flat files, **not** an external TSDB | **LOCKED** (§3–4 rationale) |
| D4 | **Separate DB file** | A dedicated **`metrics.db`**, *not* the audit DB | **LOCKED (impl call):** audit is precious/append-only/low-volume; metrics are high-churn/prunable; SQLite is single-writer **per file** → isolating avoids contention and lets metrics rotate/wipe without touching audit. Internal & reversible. |
| D5 | **Schema** | **Narrow/long** `(entity_kind, entity_id, metric, ts, value)` — *not* wide-per-entity | **LOCKED (impl call):** rollup becomes one generic `GROUP BY` over all metrics; adding a metric is zero-schema-change; null metrics → row simply absent (honest gap). Wide rejected (column explosion across host vs server shapes). |
| D6 | **Entities covered** | **Both** server and host (same table, `entity_kind` discriminator) | **LOCKED:** identical code; host is one cheap extra entity; the host deep-dive wants history too |
| D7 | **Live path unchanged** | The existing 1 Hz WS stream + REST latest `metrics` block are **untouched**; history is a **new REST endpoint** | **LOCKED:** live and historical are separate concerns; don't perturb the proven M2 path |
| D8 | **Sequencing** | A standalone milestone (**M9**), fast-follow — not blocking the M1–M8 spine | Plan-level; ship order is a scheduling call at build time |

---

## 6 · The increments

> Conventions inherited from the repo: JSON camelCase + ISO-8601 UTC `Z`; the `{error}`
> envelope; `EnsureCreated` not migrations (dev authority — wipe the DB on schema change);
> config via `appsettings.json` keys each overridable by a same-named `KGSM_API_*` env var;
> hosted services registered in `src/Api/Startup.cs`; smoke (`scripts/smoke.sh`) + xUnit
> (`tests/Api.Tests/`) are the two proof surfaces. Degrade gracefully: history disabled or
> the monitor absent must never 500 — endpoints return an empty/`null` series, never invent.

### Config keys (introduced across the increments; document all in `appsettings.json`)

| Key | Default | Meaning | Increment |
|---|---|---|---|
| `KGSM_API_METRICS_HISTORY_ENABLED` | `true` | master switch; `false` → store inert, endpoint returns empty | 1 |
| `KGSM_API_METRICS_HISTORY_DB` | `metrics.db` (in the StateDirectory beside the audit DB) | the dedicated SQLite file (D4) | 1 |
| `KGSM_API_METRICS_PERSIST_MS` | `15000` (floor `5000`) | Tier-1 persist cadence (D2) — **decoupled from the 1 Hz stream** | 1 |
| `KGSM_API_METRICS_RAW_RETENTION_HOURS` | `24` | Tier-1 raw retention (D2) | 2 |
| `KGSM_API_METRICS_ROLLUP_STEP_MIN` | `5` | Tier-2 bucket width (D2) | 2 |
| `KGSM_API_METRICS_ROLLUP_RETENTION_DAYS` | `30` | Tier-2 retention (D2) | 2 |
| `KGSM_API_METRICS_MAINT_MS` | `60000` | how often the maintenance job rolls up + prunes | 2 |
| `KGSM_API_METRICS_HOT_WINDOW_MIN` | `15` | Tier-0 in-memory ring window | 4 |

### Schema (D5) — created via a dedicated `MetricsDbContext` + `EnsureCreated`, `auto_vacuum=INCREMENTAL`, WAL

```sql
-- Tier 1: raw samples. One row per (entity, metric, sample-time). Null metrics NOT written.
CREATE TABLE sample (
  entity_kind TEXT    NOT NULL,            -- 'server' | 'host'
  entity_id   TEXT    NOT NULL,            -- instance id, or the host id
  metric      TEXT    NOT NULL,            -- DTO field name: 'cpuPctCore','memBytes','ioReadBps',...
  ts          INTEGER NOT NULL,            -- unix ms, UTC
  value       REAL    NOT NULL,
  PRIMARY KEY (entity_kind, entity_id, metric, ts)
);

-- Tier 2: rolled-up buckets (avg+min+max+n). One row per (entity, metric, bucket).
CREATE TABLE rollup (
  entity_kind TEXT    NOT NULL,
  entity_id   TEXT    NOT NULL,
  metric      TEXT    NOT NULL,
  bucket_ts   INTEGER NOT NULL,            -- bucket start, unix ms (floor to ROLLUP_STEP)
  avg         REAL    NOT NULL,
  min         REAL    NOT NULL,
  max         REAL    NOT NULL,
  n           INTEGER NOT NULL,            -- samples in bucket → partial-coverage honesty
  PRIMARY KEY (entity_kind, entity_id, metric, bucket_ts)
);
```

---

### Increment 1 — Durable write path (Tier-1 raw)  ·  ☐
- **Goal:** every monitor frame the API already fetches is persisted to `metrics.db` at the
  15 s cadence, surviving restart. **This alone satisfies the locked durability requirement
  for recent data.**
- **Build:**
  - New `src/Api/Services/Metrics/` (mirror the `Services/Audit/` shape): `MetricsDbContext`
    (the two tables, `EnsureCreated`, WAL, `auto_vacuum=INCREMENTAL`), `MetricsHistoryStore`
    (the batched writer — own DI scope per flush, like `AuditService`), `MetricsSampler`
    (the `IHostedService` that drives persistence).
  - **Reuse the existing frame source — do NOT add a second scrape** (keystone O(1)-in-clients).
    Either consume `MonitorClient.GetLatestAsync` on the sampler's own `PeriodicTimer`
    (`KGSM_API_METRICS_PERSIST_MS`), or fan out off `MetricsPump`. Map each `ServerMetrics` +
    the host `Snapshot` into `sample` rows reusing the field names from `MetricsMapping`.
  - **Honest gaps:** a null metric (`ioReadBps` etc.) or a null/absent frame (monitor down) →
    **write nothing** for it. Never write 0, never carry forward.
  - Register the sampler in `Startup.cs` behind `KGSM_API_METRICS_HISTORY_ENABLED`.
  - Write path: batched multi-row `INSERT` (consider a thin `Microsoft.Data.Sqlite`/Dapper
    fast path rather than per-row EF `SaveChanges` at cadence).
- **Verify:** unit test the frame→rows mapping incl. the null-metric→absent-row case; a
  smoke phase that runs the stub monitor, waits > one persist interval, and asserts rows
  landed in `SMOKE_METRICS_DB` (`rm -f`'d at start like `SMOKE_DB`). Restart the process and
  confirm rows persist.
- **Done when:** frames persist at cadence, gaps are absent rows (not zeros), restart keeps
  data, build is 0-warning, disabled-switch makes it inert. *(Record commit + smoke/test
  counts in §0.)*

### Increment 2 — Rollup + retention maintenance (Tier-2 + pruning)  ·  ☐
- **Goal:** bound storage permanently; produce the 5-min rolled-up tier for long ranges.
- **Build:** `MetricsMaintenanceService` (`IHostedService`, `PeriodicTimer`
  `KGSM_API_METRICS_MAINT_MS`):
  1. **Roll up** *complete* buckets: `INSERT OR REPLACE INTO rollup SELECT entity_kind,
     entity_id, metric, <floor(ts, step)> AS bucket_ts, AVG(value), MIN(value), MAX(value),
     COUNT(*) FROM sample WHERE ts ≥ <last-rolled> AND ts < <current bucket start> GROUP BY
     entity_kind, entity_id, metric, bucket_ts` — idempotent; only buckets that are closed.
  2. **Prune raw** older than `RAW_RETENTION_HOURS`; **prune rollup** older than
     `ROLLUP_RETENTION_DAYS`. Cheap indexed deletes; periodic `PRAGMA incremental_vacuum` so
     the file actually shrinks.
  - On startup, run once to **catch up** after downtime (downtime = honest gaps, not
    backfilled).
- **Verify:** unit test the bucketing math (floor-to-step, avg/min/max/n over a seeded raw
  set, partial-bucket `n`) and that pruning respects the windows; smoke asserts old raw is
  gone and rollup rows exist after a forced maintenance pass.
- **Done when:** raw never exceeds 24 h, rollup never exceeds 30 d, the file size stays
  bounded across a long run, rollup aggregates are correct, idempotent re-runs don't
  double-count.

### Increment 3 — Read endpoint + contract freeze  ·  ☐
- **Goal:** serve the history. `GET /servers/{id}/metrics?range=…` and
  `GET /hosts/{id}/metrics?range=…`, **viewer-gated**, additive within `/api/v1`.
- **Tier selection (auto by range):** `range ≤ RAW_RETENTION` (≤24 h) → serve **raw**
  (`sample`, ~15 s step); `range > 24 h` → serve **rollup** (`rollup`, 5 min step). Report
  the actual `tier` + `step` so the client never assumes resolution.
- **Frozen response shape (register in `PLAN.md §6`):**
  ```json
  {
    "entityId": "factorio-test",
    "kind": "server",
    "range": "1h",
    "step": 15,                 // seconds actually served
    "tier": "raw",              // "raw" | "rollup" — honesty about resolution
    "series": {
      "cpuPctCore": [ { "ts": "2026-06-24T12:00:00Z", "value": 42.1 }, ... ],
      "memBytes":   [ { "ts": "2026-06-24T12:00:00Z", "value": 123456 }, ... ]
    }
  }
  ```
  - **Rollup tier** points additionally carry `"min"`,`"max"`,`"n"` (the band + coverage);
    raw-tier points are just `{ ts, value }`. `tier` disambiguates.
  - **Gaps are absent points** — sparse series, no carry-forward; the client renders breaks.
  - Unknown id → `404 {error}`; history disabled / monitor never seen → empty `series` (200),
    never a fabricated curve.
- **Verify:** smoke seeds known rows → asserts range→tier selection at the 24 h boundary,
  the gap rendering (a deleted middle row → a hole), and the 404/empty-degrade branches;
  tests cover the query + serializer + the viewer gate (401 in the no-token sweep).
- **Done when:** both endpoints serve correct tier-selected, gap-honest series; the shape is
  frozen in `PLAN.md §6`; auth + degrade branches proven.

### Increment 4 — In-memory hot ring (Tier-0)  ·  ☐  *(optional optimization)*
- **Goal:** serve the live/recent window (`≤ HOT_WINDOW_MIN`) from RAM with zero DB reads.
- **Build:** a per-entity bounded ring of recent points; **warm it from Tier-1 on startup**
  so a restart doesn't blank the recent view. The read endpoint serves short ranges from the
  ring, falling through to SQLite. *Purely an optimization — Increment 3 already answers all
  ranges from SQLite, so this can be deferred or skipped.*
- **Done when:** short-range queries hit the ring (verifiable by a DB-read counter / timing),
  the ring rebuilds from Tier-1 on restart, results match the SQLite path exactly.

### Increment 5 — Frontend gate  ·  ☐
- **Goal:** restore the honest historical ranges in `kgsm-web` `PerformanceTab`.
- **Scope:** re-introduce the range selector (live / 1h / 24h / 7d / 30d) — "live" stays the
  current WS rolling window; the rest `fetch` the new endpoint. Render `min`/`max` as an
  optional band on the rollup tier. Surface gaps as breaks. **Do not** restore
  compare-to-last-week unless 30 d history makes a fair comparison honest (decide at the gate).
- **Done when:** the PerformanceTab shows real history across ranges from a real host, gaps
  honest, with no fabricated points.

---

## 7 · Recommendation in one line (the locked shape)

**The API owns a self-contained, RRD-style tiered metrics store — SQLite (`metrics.db`) as
the durable system of record across two retention tiers (24 h @ 15 s raw → 30 d @ 5 min
rolled-up), an optional in-memory hot ring on top, all fed off the existing `MetricsPump` —
not the monitor, not an external TSDB, not flat files.** Build it Increment 1 → 5; keep §0
current.
