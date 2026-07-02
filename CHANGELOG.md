# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
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
