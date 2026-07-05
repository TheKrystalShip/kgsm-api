# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed (v0.13.0)
- Player roster identity is now **name-first**: the durable person key resolves as
  `id → name → addr → sessionKey` (was `id → addr → name → sessionKey`). For
  account-less direct-socket games (e.g. romestead) the character **name** now keys the
  roster instead of `ip:port` — a reconnect from a new ephemeral port, or after an ISP IP
  change, no longer mints a duplicate roster row. The name is present on both join and
  leave (the watchdog's `PlayerSessionMap` backfills it onto an `addr`-only leave line), so
  leave still correlates to the right person-row. Resolution is consolidated into a single
  `PlayerIdentityResolver` (was duplicated across four sites); the name is trimmed but not
  case-folded. The session-level key (`PlayerRosterService.ResolveKey`) is unchanged — a
  session is still keyed on the connection. (player-presence-contract.md §5.)
- On startup, `PlayerHistoryService` runs a one-time idempotent **re-key + merge** of the
  existing roster: every row is regrouped onto its recomputed person key, and rows that
  collapse together (old addr-first duplicates of the same character) are merged into one
  survivor — earliest `FirstSeen`, latest `LastSeen`, `banned` status/reason never lost,
  freshest non-blank name/addr/id carried forward.

### Added (v0.12.0)
- New server run-state `starting`, distinct from `running`: the window between
  `instance_started` (process spawned) and `instance_ready` (the watchdog's
  log-scrape confirms the game finished booting) — both events observe the process
  as "up," so the distinction is tracked out-of-band by a new `InstanceCache`
  "starting latch" (`MarkStarting`/`MarkReady`/`IsStarting`), not derivable from the
  boolean run-state reading alone.
- `KgsmAuditConsumer` registers a handler for the new `instance_ready` event
  (kgsm-lib 1.35.0, `InstanceReadyData`) — audit-silent by design (a run-state
  refinement of the already-recorded `server.start`, not a new fact); it only
  clears the starting latch.
- `ServerAggregator.BuildServer` folds the starting latch into `Server.status`
  (`ServerStatus.Starting = "starting"`); `DomainPump`'s existing status diff fans
  `starting`/`running` transitions out over the `servers` SSE topic with no pump
  change.
- `InstanceCache`'s background boolean reconcile can no longer promote
  `starting → running` on its own (the reconcile-hazard guard,
  `ReconcileStartingLatch`) — it only ever closes the latch on new evidence (the
  process measured down, the instance vanished from the roster, or a 5-minute
  safety timeout, which resolves honestly to `running` since the process is
  observed up in that same pass).
- `CommandGate`: `start` against a `starting` server is now inadmissible (409,
  same no-op class as start-when-running); `update` against `starting` is
  inadmissible (same "files in use" reason as running); `stop` against `starting`
  remains admissible (an operator can abort a server stuck mid-boot).

### Added (v0.11.0)
- `Job` DTO gains `phase` (install sub-phase: `"preparing"` | `"downloading"` | `"deploying"`)
  and `blueprint` fields; both are null for non-install jobs.
- `job.patch` SSE frames for install jobs now carry `blueprint` (stamped immediately in
  `StartInstall` before the background task runs) so any connected user can create a phantom card.
- `KgsmAuditConsumer` handles `instance_installation_started`, `instance_download_started`, and
  `instance_deploy_started` events and emits `job.patch` SSE frames with the corresponding
  `phase` value so clients show granular install progress.

### Added (v0.10.0)
- `GET /servers/{id}/settings`: `crashRestart` (bool) + `crashMaxRestarts` (int) from
  instance config (null when the kgsm config key is unset).
- `PATCH /servers/{id}/settings`: `crashRestart` (bool) + `crashMaxRestarts` (int, 1–10)
  → `crash_restart` / `crash_max_restarts` config keys. Validation: crashMaxRestarts
  must be 1–10.
- kgsm-lib → 1.35.0 (Instance.CrashRestart, CrashMaxRestarts).

### Added (v0.9.0)
- `GET /servers/{id}/settings`: `autoBackupOnRestart`, `backupRetention` from instance
  config; `lastBackupUtc`, `lastBackupOk` from scheduler status socket (null when
  scheduler leaf absent or no backup run yet).
- `PATCH /servers/{id}/settings`: `autoBackupOnRestart` (bool) + `backupRetention`
  (int, 1–100). Validation: retention must be 1–100; auto-backup=true requires a
  non-off scheduled cadence.
- kgsm-lib → 1.34.0 (Instance.AutoBackupOnRestart, BackupRetention, PruneBackups).

### Added
- **Settings Phase 3 — Scheduled restart.** `GET /servers/{id}/settings` now includes
  `scheduledRestart`, `restartTime`, `restartDay`, `timezone` (from kgsm instance config)
  and `nextFireUtc` (from the scheduler leaf status socket, null when scheduler absent).
  `PATCH /servers/{id}/settings` accepts all four schedule fields with validation.
  New `scheduler` leaf registered in `LeafCatalog` + `LeafHealthMonitor`; degrades
  gracefully when the scheduler daemon is absent (nextFireUtc null, scheduled-tasks
  card gated in the SPA). New `SchedulerClient` reads the NDJSON-over-unix-socket status
  snapshot at `KGSM_API_SCHEDULER_SOCKET` (opt-in — blank default). kgsm-lib upgraded to 1.33.0.
- **Settings Phase 2 — Resources.** `GET /servers/{id}/settings` now includes `cpuPriority: string|null`
  and `memoryCapMb: int|null`. `PATCH /servers/{id}/settings` accepts both fields: `cpuPriority`
  (low/normal/high — validated, live-applied via `IWatchdogClient.SetCpuPriorityAsync`, best-effort)
  and `memoryCapMb` (≥0, 0=uncapped — persisted to kgsm config, takes effect at next restart).
  kgsm-lib upgraded to 1.32.0.
- **`GET/PATCH /api/v1/servers/{id}/settings` (Phase 0 — Settings spine).** New settings aggregator
  endpoint, operator-gated write. Phase 0 surfaces the `autoUpdate` toggle (the existing `auto_update`
  kgsm config key). Later phases add autostart, resource caps, and scheduler config as those primitives
  land. Follows the `ServerConfigController` pattern: echo-path audit (kgsm's `instance_config_changed`
  event carries provenance), no double-write.

### Changed
- **Settings Phase 1 — Autostart.** `GET /servers/{id}/settings` now includes `autostart: bool|null`
  (null when the watchdog is absent/unreachable — honest unknown, never fabricated). `PATCH
  /servers/{id}/settings` accepts `autostart: bool` and fans out to `IWatchdogClient.Enable/Disable`
  (503 when the watchdog is not provisioned; 400 on a watchdog refusal). kgsm-lib upgraded to 1.31.0.
- **Uninstall pre-stop (Phase 0 — delete hardening).** `CommandRunner.RunUninstall` now issues a
  best-effort `Stop` before `Uninstall`, so we never orphan a running process. A non-zero stop result
  (instance already stopped) is logged at Debug and ignored.
- **Blueprint catalog cached in-memory (60s TTL, background refresh).** `GET /library` no longer
  spawns a `kgsm.sh` process on every request — a singleton `BlueprintCache` serves the blueprint
  dictionary from memory, refreshed by a background `PeriodicTimer` every 60s (configurable via
  `KGSM_API_BLUEPRINT_CACHE_TTL_SECONDS`). First request triggers an on-demand load; subsequent
  reads are instant. The `LibraryHydrationWorker` shares the same cache instead of making its own
  process spawn per sweep.

## [0.4.1] - 2026-07-02

### Fixed
- **SSE write loop no longer busy-loops (100% CPU) after a client disconnects.** On disconnect the
  connection token (`RequestAborted`) is cancelled; the wake branch of `StreamConnection.WriteLoopAsync`
  never checked it, so `await Task.WhenAny(canceledWait, canceledDelay)` completed synchronously every
  iteration and the loop drained an empty queue and `continue`d forever without yielding — one orphaned
  ThreadPool thread pegged at 100% per disconnected stream. The loop now guards on the token (loop
  condition + a post-`WhenAny` break) and cancels the losing task each iteration via a linked CTS (which
  also stops a 20s heartbeat timer from being abandoned on every wake). Regression-tested
  (`StreamConnectionTests`): a cancelled token stops `RunAsync` promptly, never spins.

## [0.4.0] - 2026-07-02

### Changed
- **Realtime transport migrated from WebSocket to Server-Sent Events (SSE).** `GET /api/v1/stream`
  is now a `text/event-stream` GET: topics are chosen via `?topics=` (resource-scoped topics
  contain `/`), the bearer arrives in the `Authorization` header, and an auth failure returns a
  plain, readable `401` (no more opaque WebSocket `1006` close). Server pushes `data:` frames with
  a `: connected` preamble and a 20s `: keepalive` heartbeat; `X-Accel-Buffering: no` +
  `DisableBuffering()` keep frames unbuffered through reverse proxies. The per-connection
  coalesce-to-latest queue and subscriber-gated publishers are unchanged — only the wire transport
  and framing changed. Removed the client→server command channel (`Subscribe`/`Unsubscribe`/`Ping`/
  `Pong`) since topics are now fixed at connect time by the query string.
- **HTTP protocol bumped to `Http1AndHttp2`** (prod TLS negotiates h2; dev plain-text stays h1.1).
  The prior h1.1 lock existed solely because WebSocket-over-h2 has no path in Kestrel; with WS gone
  that constraint is lifted. `UseWebSockets()` and the `?access_token=` query-string auth shim are
  removed from the pipeline.

## [0.3.1] - 2026-07-01

### Changed
- Player roster startup: replaced `MarkUnknownOnStartupAsync` (mark all online → unknown) with
  `ReconcileFromWatchdogAsync` — queries the watchdog's `GET /players` endpoint for the live
  session snapshot, marks matching players online and everyone else offline. No intermediate
  unknown state. Falls back to marking unknown when the watchdog is absent/down. Handles new
  players who joined while the API was down (inserted as online). Bumped kgsm-lib to 1.30.0.

## [0.3.0] - 2026-07-01

### Added
- Permanent player roster history: `PlayerHistory` entity + `PlayerHistoryService` (DB-backed
  authority for who has ever connected to a server). Each player has a `PlayerStatus` (online,
  offline, banned, unknown) that is deterministic — status resolves only on the next event,
  never probed. `MarkUnknownOnStartupAsync` marks all online players unknown on API restart.
  `players.ban` support with `banReason`. 14 new `PlayerHistoryServiceTests`.

## [0.2.0] - 2026-07-01

### Added
- Metrics-threshold alert source (increment 1) — built + unit-tested + live-validated (2026-07-01; not yet deployed): the
  `host-monitor`/`metrics` `AlertSource`s — previously reserved — are wired into the existing `AlertEngine`
  (new `TickMetrics` reconcile) so a sustained host or per-server metric breach (disk/mem/swap/load/temp on
  the host; opt-in pids/mem/cpu per server) raises/resolves through the same `/alerts` REST + `alerts` WS
  surface as the crash source. Two dwells (fire-dwell + clear hysteresis), honest-unknown on a down monitor,
  `resolution.actionId` always `null` (the audit bridge is crash-specific), metric `danger` never escalates.
  Policy is appsettings + env (`MetricsThresholds`), not yet DB-backed (increment 2). New `AlertSurface.Host`
  constant. 10 new `TickMetrics` tests; full suite 474/474. See `metrics-threshold-alerts-plan.md` and
  `src/Api/Services/Alerts/CLAUDE.md`.

### Fixed
- `MetricsThresholds:Rules` config binding no longer silently drops a custom policy back to the baked-in
  default when any rule is warn-only (`danger` absent/null): the binder cannot construct the positional
  `ThresholdRule` record without a `Danger` value, so binding now goes through a mutable `ThresholdRuleBinding`
  DTO. (Latent: the default policy's own warn-only `srv-*` rules made every operator override fall back to
  `Default`.)
- The crash `AlertEngine.Tick` resolve/retract loop now only reconciles `crash:` ids — the metric source
  shares the firing set, and without the guard a watchdog poll would wrongly retract/resolve a live
  `metric:` alert.

## [0.1.0] - 2026-06-30

### Added
- Initial versioned release.
