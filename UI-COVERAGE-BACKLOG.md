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

## Upstream queue (cross-tier — needs an upstream build before the dependent UI is honest)

These were **not API work** — each needed a change in **kgsm / kgsm-lib / kgsm-watchdog / kgsm-monitor**
first, then a (usually small) API change to consume it. **BUILT + TESTED + COMMITTED on `main` across 5 repos
2026-06-22 (not pushed)** — orchestrated as parallel leaf builds (kgsm, watchdog, monitor) → one coordinated
kgsm-lib **1.22.0** bump → kgsm-api integration. Live host e2e where it needs a deployed daemon/instance is
owed (see "Owed-to-human" below).

| # | Blocks (UI) | Status — commits (all `main`, not pushed) | Result |
|---|---|---|---|
| **G1** | #6 `startedAt`/`uptime` | ✅ kgsm `a639908` (ISO-8601-`Z` emit, 3 call sites) + kgsm-lib `f9df267` (tolerant `DateTime?` converter — null-on-unparseable, never throws) | start_time now ISO-8601-UTC; the whole-roster-blanking parse throw is fixed. **No kgsm-api change** (already accepts `Kind==Utc`). ⚠ See the watchdog-overlay finding below — native `startedAt` is still null for watchdog-supervised instances; the fix lands `startedAt` for **containers**. |
| **G2** | #1 `config-set` audit row | ✅ kgsm `fc5f03d` (`instance_config_changed`, **key only**) + kgsm-lib `f9df267` (`InstanceConfigChangedData`) + kgsm-api `8c713e2` (`config.set` audit) | config-set writes a `config.set` audit row carrying the **key only** (never the value — secret hygiene, live-proven). Echo-path, no double-write. |
| **#8** | ConsolePanel / LogConsole (`console` WS) | ✅ kgsm-watchdog `ae36f49` (`/console/{name}` tail + `/follow`) + kgsm-lib `f9df267` (`GetConsoleTailAsync`/`FollowConsoleAsync`) + kgsm-api `37b81c8` (REST `?tail=N` + follow-only WS topic `servers/{id}/console`) | **Full REST + WS split** (matches the patch-only Realtime contract). Native-only; raw-text transport (no fabricated per-line structure); degrades to `{lines:[]}` when watchdog absent. |
| **#9** | Host diagnostics depth | ✅ kgsm-monitor `50c770e` (Contracts **1.1.0**) + kgsm-api `247936c` (Host DTO mapping) | **Slice 1 (incl. sensors):** cpu model/cores/threads/maxFreq, ram cached/buffers, iface mac/errors, sensors/temp, disk device-model. **Deferred** (honest reasons): iface **IP** (needs `getifaddrs` P/Invoke), **SMART** (no smartctl → would fabricate), **per-process list** (heavy + tick-cadence decision). |

**New upstream finding (surfaced during the build) — native `startedAt` has a *second* gap:** for a
**watchdog-supervised native** instance, `instances status --json` returns `start_time: null` because the
watchdog owns the cgroup process and doesn't write the management-script pid file, and `instances.sh` overlays
the watchdog's pid/active but **not** start_time. G1 is still correct (it fixes the format + the roster-blanking
and lands container `startedAt`); surfacing native `startedAt` would need the watchdog overlay to also carry
start_time — a **new candidate upstream item**, not yet queued.

**LIVE-VALIDATED end-to-end on `hotrod` 2026-06-22.** Deployed: **kgsm-watchdog** new build → `/opt/kgsm-watchdog/kgsm-watchdog` (old → `.pre-console.bak`, `systemctl restart`); **kgsm-monitor** new build (Contracts 1.1.0) on `/tmp/kgsm-monitor-slice6.sock`; **kgsm-api** new build on `:8097` (auth-disabled dev run, consuming kgsm-lib **1.22.0** + Monitor.Contracts **1.1.0** from local-nuget, watchdog/monitor/event sockets wired). kgsm runs from the dev checkout (no deploy). Proven live:
- **#9** — `GET /hosts/hotrod` returns real cpu-info (`AMD Ryzen 7 3800X` 8c/16t/4.56GHz), 8 sensors, mem cached/buffers GiB, iface mac+errors (enp4s0/wlp5s0/docker0), disk `/` → `Samsung SSD 990 EVO Plus 1TB`.
- **G1** — `GET /servers` roster reads cleanly (no blanking); native `startedAt` honest-`null`.
- **G2** — CLI `config-set` → `config.set` audit row (`meta.key` only); a distinctive sentinel **value appears 0 times** in `/audit` (key-only secret hygiene).
- **#8** — `GET /servers/factorio-test/console?tail=N` returns real factorio startup lines (`Hosting game at IP ADDR`), AND a WS subscriber on `servers/factorio-test/console` received a `console.line` frame (`seq:0`, follow-only, exact envelope).

Still owed: container `startedAt` *populated* path — both host instances are native, so it's untested (native stays null per the watchdog-overlay finding above); needs a container instance.

> **#7 (player roster) is NOT in this queue** — it's API-buildable now (a stateful aggregator on the
> `player.join`/`leave` events the API already audits). Its *data* depends on per-game presence-detection
> patterns being live (Increment 2 native detection is built; real Factorio/Minecraft patterns still owed),
> so PlayersTab reads honest-empty for any game without configured patterns even after #7 ships.
>
> Tier 3 (#10–#12) also need new stores/decisions but are **architectural choices**, not a queued upstream
> build — left in Tier 3.

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
> - ✅ **backups (list/create/restore) + `update`** — were engine-blocked at the read-check (the deployed
>   kgsm's `instances` command lacked these → `Unknown command` 503), now **FIXED in kgsm 2026-06-21**:
>   `instances backups`/`create-backup`/`restore-backup`/`update`/`check-update` added (CLI + per-instance
>   management scripts), emitting the events the API already audits. **Live-validated** — `GET
>   /servers/factorio-test/backups` now returns **200** with snapshots parsed (was 503); functional once the
>   deployed kgsm + management files are updated. No kgsm-lib/kgsm-api change. See Findings.
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
- [x] **2. `update` command verb** — add `update` to the command verb set (`POST /servers/{id}/commands {verb:"update"}`) — ✅ code committed; **engine gap now fixed** (kgsm gained `instances update`, 2026-06-21; functional once deployed — see Findings)
  - Coverage: kgsm-lib `IInstanceService.Update(name,actor,origin)` ✓ · kgsm per-instance `update` (scriptable) ✓.
  - Unblocks: the ServerHero **Update chip** (disabled in LIVE today).
  - Notes: long-running → a `job` like start/stop (mirror `CommandRunner`). kgsm refuses update on a running instance — surface honestly.
- [x] **3. Backups: list + create + restore** — `GET /servers/{id}/backups`, a `backup` create + `restore` action — ✅ code committed; **engine gap now fixed** (kgsm gained `instances backups`/`create-backup`/`restore-backup`, 2026-06-21; `GET /backups` live-validated 200 — see Findings)
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
- [x] **8. Console stream** — `console` WS topic + REST scrollback — ✅ **BUILT 2026-06-22** (watchdog `ae36f49` / kgsm-lib `f9df267` / kgsm-api `37b81c8`). Full REST `?tail=N` + follow-only WS split; native-only; raw-text; degrades to `{lines:[]}`. Live WS follow e2e owed (needs deployed watchdog + running native instance). See the Upstream queue.
  - Verified: the watchdog writes instance stdout to its `LogFile` but exposed **no tail/stream** endpoint; kgsm has no console query. Built: watchdog `/console/{name}` (tail) + `/follow`, kgsm-lib `IWatchdogClient.GetConsoleTailAsync`/`FollowConsoleAsync`, kgsm-api `ConsoleBridgeManager` + topic. Unblocks **ConsolePanel/LogConsole**.
- [x] **9. Host diagnostics depth (monitor slices)** — ✅ **SLICE 1 BUILT 2026-06-22** (monitor `50c770e` Contracts 1.1.0 / kgsm-api `247936c`): cpu model/threads/freq, ram cached/buffers, iface mac/errors, sensors/temp, disk device-model.
  - Verified: all were **absent from the monitor Snapshot**; each needed a new monitor collector → `Monitor.Contracts` bump → API mapping. **Deferred** (honest reasons): iface **IP** (`getifaddrs` P/Invoke), **SMART** (no smartctl → would fabricate), **per-process list** (heavy + tick-cadence decision). Live `/metrics` serve of the new fields needs the redeployed monitor.

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
- **✅ RESOLVED (kgsm, 2026-06-21) — the kgsm `instances` CLI now exposes the backup/update subcommands
  kgsm-lib calls.** Originally a 🚧 BLOCKER: kgsm-lib 1.21.0 execs `instances backups` / `instances
  create-backup` / `instances restore-backup` / `instances update` / `instances check-update`, but the
  deployed kgsm's `instances` command only had `remove / info / status / find / config-get / config-set /
  help` (verified live: `Unknown command: backups`). **Fix landed in kgsm** (working tree on `main`, see the
  kgsm changeset): the per-instance management scripts gained the dash-free commands `backups` /
  `create-backup` / `restore-backup` / `check-update` (templates `manage.{native,container}.d/12-commands` +
  `13-dispatch` + `02-help`), and the top-level `commands/instances.sh` gained matching subcommands that
  forward 1:1 to the management file (+ `update`, which every management file already had). The CLI emits the
  existing `instance_version_updated` / `instance_backup_created` / `instance_backup_restored` events on
  success, so the API's echo-path audit fires with **no double-write**. `backups` output is normalized
  one-per-line in `instances.sh` (the contract this API parses). An honest capability gate handles older
  un-regenerated management files (regenerate via `kgsm files management create <instance>`).
  **No kgsm-lib / kgsm-api code change** — the API already called these strings. **Live-validated 2026-06-21**:
  read paths (`backups`, `check-update`) + write path (real `create-backup`) work on `factorio-test`, and the
  original failing case `GET /servers/factorio-test/backups` now returns **HTTP 200** with both snapshots
  parsed (was 503). Tier-1 #2/#3 are now functional once the deployed kgsm + management files are updated.
  (Adjacent gap **also now wired in kgsm**: `instances version <i> --installed`/`--latest` — kgsm-lib's
  `GetInstalledVersion`/`GetLatestVersion` — added as a top-level read-only `version` subcommand forwarding to
  the management file (bare/`--installed` → installed, `--latest` → latest); live-verified on factorio-test
  (`2.0.76`). Not on the Tier-1 path — `updateAvailable` still comes from `instances status --json` — but the
  kgsm-lib↔CLI version contract is now complete.)
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
