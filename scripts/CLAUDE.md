# scripts/

## `smoke.sh` — the "mock frontend"

It builds Release and asserts the whole HTTP contract surface, plus an **auth-ENABLED** no-token sweep.
The domain checks run under `Api__AuthDisabled=true` (the escape hatch — a synthetic Owner) so they
exercise the contracts unchanged; a dedicated auth-enabled instance then proves the no-token sweep
(every protected endpoint `401`s with the frozen envelope, `/health`+`/api/v1` stay open, and an
`/auth` path is a `404` because this node signs nobody in). The command-gate checks prove the
gate/rejection contract (`400`/`404`/`409`) **without mutation** — the gate rejects before a verb
runs. Real native lifecycle needs `kgsm-watchdog` up — without it, kgsm direct-spawns an orphan and
run-state tracking is unreliable.

It runs two phases: Phase A degrade (no monitor, live kgsm) and Phase B an **embedded stub monitor** (a
unix socket serving a canned `Snapshot`) that makes the host happy path + the servers-join
present-branch deterministic with no external monitor. The stream is covered by an embedded **SSE
reader** (a plain `text/event-stream` read against `?topics=`, no external dependency) that subscribes,
reads honest ticks, and — killing **then restarting** the stub monitor mid-stream — proves the
degrade→recover capability lifecycle (down flip + tick silence, then operational flip + ticks resume,
`provisioned:true` throughout).

Knobs: `SMOKE_PORT`, `SMOKE_SKIP_BUILD=1`, `SMOKE_DB`, `SMOKE_KGSM_PATH` (the engine on another host),
`SMOKE_MONITOR_SOCKET` (a live monitor in Phase A), `SMOKE_WATCHDOG_SOCKET` and `SMOKE_FIREWALL_SOCKET`
(both empty, and so absent, unless given: an unset leaf socket resolves to the well-known one wherever
that leaf is installed, which would hand the run the host's live leaf), `SMOKE_SERVER` (the server every
per-server check runs against; the roster's first when unset). The file checks save that server's config
back byte for byte, which changes nothing on disk but is recorded in the host's audit trail, so on a host
whose servers are in use name a disposable one. It `rm -f`s its own `SMOKE_DB`, because `EnsureCreated`
no-ops on an existing database.

The diagnostics endpoints it probes (`/api/v1/_throw`, `/api/v1/_dbcheck`) exist for it alone — restrict
them before any public exposure. `_dbcheck` is a **read** round-trip: the append-only audit table must
never be probe-written.

## `mint-dev-token.py`

Mints a session signed with the local anchor's key for an existing account, for testing against a live
auth-enabled API on a trusted dev host. It reads the key at runtime and writes neither the key nor a
token anywhere.
