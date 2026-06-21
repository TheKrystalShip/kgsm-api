# kgsm-api ↔ Control Panel: UI coverage backlog

Backlog of functionality the **kgsm-web** SPA expects that **kgsm-api** does not provide
yet. Derived from `kgsm-web/WIRING.md` §5/§6/§9 (the field-level supply map) and
**verified against the real upstream source on 2026-06-21** — kgsm (engine),
**kgsm-lib 1.21.0** (the typed C#↔engine chokepoint — the API may only reach the engine
through this, never by shelling out), kgsm-monitor, and kgsm-watchdog.

**Dividing line:** an item is "pure API work" only when the capability exists in *both*
kgsm *and* kgsm-lib (the API just exposes it). If kgsm has it but kgsm-lib doesn't → an
extra kgsm-lib method is needed. If neither has it → real upstream work first.
**Never fabricate** a metric/status — measured, or explicitly unknown (`—`/null), never `0`.

**Prerequisite (resolved):** kgsm-api references `TheKrystalShip.KGSM.Lib` **1.21.0**
(`src/Api/Api.csproj`), which already exposes every Tier-1 method below — **no lib bump
needed**.

Legend: `[ ]` todo · `[~]` in progress · `[x]` done. Coverage = where the source lives.

---

## Tier 1 — pure API work (kgsm-lib 1.21.0 already exposes it; add endpoint/verb/field)

> **STATUS — IMPLEMENTED + VALIDATED + MERGED to `main` 2026-06-21.** Combined **0-warn Release build +
> 255 tests pass** (base 218 + 37 new). Two parallel subagent jobs on disjoint files: config + backups +
> `update` verb, and `updateAvailable` + `startedAt` + `panelVersion`. All reach the engine via kgsm-lib
> (no shelling out), mirror the command-path auth tiers + `origin` provenance, and never double-write audit
> (the kgsm echo owns it — handlers confirmed wired for `server.update`/`backup.create`/`backup.restore`).
>
> **⚠ LIVE READ-CHECK (against real kgsm on `hotrod`) split the six into working vs engine-blocked:**
> - ✅ **config GET** (200, real values), **panelVersion** (`"v1"`), **updateAvailable**/**startedAt**
>   (wired, honest `null`) — **work live.**
> - ❌ **backups (list/create/restore) + `update`** — kgsm-lib execs `instances backups`/`create-backup`/
>   `restore-backup`/`update`/`check-update`, but the deployed kgsm's `instances` command only has
>   `remove/info/status/find/config-get/config-set/help` → **`Unknown command`** (503). The API code is
>   correct and degrades honestly; it stays non-functional until kgsm exposes those subcommands (see the
>   **kgsm `instances` subcommand gap** under Tier 2 / Findings). Committed anyway per the chosen plan.
>
> **Verified caveats (honest, not bugs to fix in this slice):**
> - **config-set is un-audited** — kgsm emits no `config.*` event today, so the API writes no row
>   (refusing to fabricate a `config.*` action). Honest boundary; an upstream kgsm event would close it.
> - **`updateAvailable` is null in practice** — the roster status is read in `--fast` mode (a fleet-wide
>   networked update probe per poll is too expensive); wired for a future throttled update-check surface,
>   never a fabricated `false`.
> - **`startedAt` is null in practice + an upstream parse bug found** — kgsm emits `process.start_time`
>   as a non-ISO local string the referenced kgsm-lib's STJ deserializer can't parse (throws → swallowed
>   into an empty roster reading). Only watchdog-spawned natives (null start_time) parse. Surfaced honestly
>   as null; **the parse gap is pre-existing in the status-read path** (not introduced here) → see Findings.
> - **`panelVersion` = `"v1"`** — honest in-process const (no `<Version>` in the csproj); shared so the
>   `GET /api/v1` handshake and the host field can't drift.
> - **OWED — live engine round-trip:** request shaping + in-process behavior proven by the 255 tests
>   (WebApplicationFactory); actually running update/backup/config-set against real kgsm is owed-to-human
>   (same bar as slices 6/9a). The destructive write paths especially.

- [x] **1. Per-server config read/write** — `GET /servers/{id}/config`, `PATCH /servers/{id}/config` (✅ live-verified)
  - Coverage: kgsm-lib `IInstanceService.GetInstanceConfigValue(name,key)` + `SetInstanceConfigValue(name,key,value,actor,origin)` ✓ · kgsm `instances config-get/config-set` ✓ (protected/identity keys refused by the engine — surface that as a 4xx, don't bypass).
  - Unblocks: **ServerSettings** page (100% mock today) + the assistant's `set_config` verb.
  - Notes: write = Operator tier + `origin` provenance (mirror the command endpoint). No double-write audit (kgsm echo owns it). Read = Viewer.
- [~] **2. `update` command verb** — add `update` to the command verb set (`POST /servers/{id}/commands {verb:"update"}`) — ⚠ code committed, **engine-blocked** (kgsm has no `instances update`; see Findings)
  - Coverage: kgsm-lib `IInstanceService.Update(name,actor,origin)` ✓ · kgsm per-instance `update` (scriptable) ✓.
  - Unblocks: the ServerHero **Update chip** (disabled in LIVE today).
  - Notes: long-running → a `job` like start/stop (mirror `CommandRunner`). kgsm refuses update on a running instance — surface honestly.
- [~] **3. Backups: list + create + restore** — `GET /servers/{id}/backups`, a `backup` create + `restore` action — ⚠ code committed, **engine-blocked** (kgsm has no `instances backups`/`create-backup`/`restore-backup`; see Findings)
  - Coverage: kgsm-lib `GetBackups(name)` / `CreateBackup(name,actor,origin)` / `RestoreBackup(name,backupName,actor,origin)` ✓ · kgsm `backup list/create/restore` ✓.
  - Unblocks: **BackupsList** page (100% mock today).
  - Notes: list = Viewer; create/restore = Operator + provenance. Decide create/restore as command verbs vs dedicated routes (mirror whichever fits; restore takes a `backupName`).
- [x] **4. `update_available` on the Server DTO** (✅ wired; honest `null` in fast mode)
  - Coverage: kgsm-lib `VersionInfo.UpdatesAvailable` (bool?) on the status model (+ `CheckUpdate(name)`) ✓ · kgsm `version --compare` ✓.
  - Unblocks: the "update waiting" badge.
  - Notes: ⚠ this is a **networked** version check (kgsm skips it in `--fast` mode) — has a real cost; honest `null` when unchecked, never `false`.
- [x] **5. Host `panelVersion`** — surface the existing `ApiInfo.version` on the Host DTO (trivial). (✅ live-verified `"v1"`)
  - Coverage: already in-process (`ApiInfo`). No upstream dependency.
- [x] **6. Per-server `uptime`/`startedAt` on the Server DTO** (✅ wired; honest `null` — see the start_time parse finding)
  - Coverage: kgsm `instances status --json` emits `process.start_time` ✓; surface uptime from it (may need a one-field add to the kgsm-lib status model if it isn't already mapped — verify).

---

## Tier 2 — needs new upstream work or a new stateful API component (no honest source exposed)

- [ ] **7. Player roster + live count** — a `players` topic / `GET /servers/{id}/players` + Server `players{current,max}`
  - Verified: **no roster anywhere** — kgsm & watchdog *emit* `player.join`/`player.leave` only; the watchdog ingester is stateless; kgsm-lib has event *types* only. The API already **audits** join/leave, so the roster aggregator belongs in the API (stateful, like `AlertEngine`).
  - Caveat: lossy across restarts (roster-flush) → honest `unknown`, never a fabricated count. Unblocks **PlayersTab** + `players`.
- [ ] **8. Console stream** — a `console` WS topic
  - Verified: the watchdog writes instance stdout to its `LogFile` but exposes **no tail/stream** endpoint; kgsm has no console query. Needs a new watchdog `/logs` SSE + a kgsm-lib `IWatchdogClient` method (or a fragile native-only filesystem tail). Unblocks **ConsolePanel/LogConsole**.
- [ ] **9. Host diagnostics depth (monitor slices)** — process list, sensors/temp, cpu model/threads/freq, ram cached/buffers, disk device/SMART, iface ip/mac/errors
  - Verified: **all absent from the monitor Snapshot** (aggregates + throughput only; never `/proc/cpuinfo`, no per-process list, no sensors, no SMART, no iface ip/mac). Each needs a new monitor collector → `Monitor.Contracts` bump → API mapping.
  - Cheap: cpu-static, ram cached/buffers, iface ip/mac/errors. Bigger: per-process list, sensors, SMART.

---

## Tier 3 — needs an architectural decision

- [ ] **10. Metrics history** (PerformanceTab graphs)
  - Verified: the monitor serves **latest point-in-time only** (no ring/persistence). But per-server `metrics.tick` WS **already exists**. Cheapest: FE session-local ring off the live tick (no cold history). A real history store cuts against "domain live-scraped, never stored" → needs sign-off. **Recommend the FE ring.**
- [ ] **11. Global `/settings`** — no preference store anywhere (`/me` PATCH profile half also deferred). New API store.
- [ ] **12. File browser** — no file read/write API in kgsm or kgsm-lib. Security-sensitive; lowest priority.

---

## Minor / honest "—" (derive or leave unknown — not real features)
- `ip` (derive host addr + instance port), `notice` (no source), library curated names / `maxPlayers` (blueprint metadata curation).

## Not gaps (already built, or FE-side)
- Uninstall (`DELETE /servers/{id}` exists), open_ports + `network.patch` (M6·b), refresh-token endpoint,
  per-server `metrics.tick` + `capabilities.patch` WS topics (exist — FE just needs to subscribe),
  `installing/updating/crashed/error` status (FE synthesizes from jobs+alerts),
  multi-host registry (FE `localStorage`; the API is per-host **by design**).

---

## Findings (surfaced during Tier-1 implementation, 2026-06-21)
- **🚧 BLOCKER — the kgsm `instances` CLI is missing the backup/update subcommands kgsm-lib calls.**
  kgsm-lib 1.21.0 execs `instances backups` / `instances create-backup` / `instances restore-backup` /
  `instances update` / `instances check-update`, but the deployed kgsm's `instances` command only supports
  `remove / info / status / find / config-get / config-set / help` (verified live: `Unknown command: backups`).
  kgsm *has* the backup/update logic, but only on the per-instance **management script**
  (`<mgmt> backup list|create|restore`, `<mgmt> update`), not surfaced as top-level `kgsm.sh instances`
  subcommands. So kgsm-lib is ahead of the engine's CLI. **This blocks Tier-1 #2 (`update`) and #3 (backups),
  and a future real `updateAvailable` (needs `check-update`).** Fix = wire those `instances` subcommands in
  kgsm to the existing management-script functions (then no kgsm-lib/kgsm-api change needed — the API already
  calls them). Tracked as the next upstream task. The committed API endpoints degrade honestly (503 + the real
  engine error) until then.
- **kgsm `process.start_time` is non-ISO and the kgsm-lib status model can't parse it** — kgsm emits it as a
  local-time string (container: RFC3339 with the offset stripped, e.g. `2026-06-16 14:23:01`; native `ps lstart`:
  `Sun Jun 21 20:17:17 2026`). The referenced kgsm-lib (1.21.0) maps `start_time` to a `System.Text.Json DateTime?`
  which **throws** on those strings; the throw is swallowed into an empty roster reading. Only a null start_time
  (watchdog-spawned native, no local pid file) parses. **Upstream fix:** kgsm should emit ISO-8601-UTC `start_time`,
  and/or kgsm-lib should add a tolerant converter. Until then `startedAt` (Server DTO) is honestly null, and a
  running container / local-pid native may blank its whole status reading — a **pre-existing** status-path risk.
- **No `config.*` kgsm event** — `config-set` produces no audit trail (the API refuses to fabricate one). An
  upstream `instance_config_changed` event would let the audit consumer mirror it (one pure handler).

## Execution plan (Tier 1 delegation)

Tier 1 does **not** fan out into 6 independent parallel jobs — the items converge on a few
shared files (`ServersController`, `ServerDto`, `CommandDto`/`CommandRunner`, the
read/aggregation service). So it's grouped along a clean **write/read seam** into two
cohesive jobs (run in isolated git worktrees, integrated + validated after):

- **Job A — instance operations (write/action):** #1 config GET/PATCH · #3 backups list/create/restore · #2 `update` verb. Prefers *new* controllers (e.g. `ServerConfigController`, `ServerBackupsController`) to avoid touching `ServersController`; edits `CommandDto`/`CommandRunner` for the `update` verb.
- **Job B — read-model enrichment (read-only DTO fields):** #4 `updateAvailable` · #6 `uptime` (Server DTO) · #5 `panelVersion` (Host DTO) + mappers + the status-fetch path.

Both: go through kgsm-lib (never shell out) · mirror existing auth-tier + `origin` provenance ·
no double-write audit (kgsm echo owns it) · no DB migration (EnsureCreated) · don't bump
kgsm-lib (1.21.0 has everything) · build 0-warn + `dotnet test` (add WebApplicationFactory tests).
