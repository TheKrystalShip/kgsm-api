#!/usr/bin/env bash
#
# Smoke test — the reproducible "mock frontend" stand-in until the real SPA can reach a
# host. Builds the API (JIT) and runs it, then asserts the cross-team contracts over HTTP.
#
#   M0 (architecture.html §6): /api/v1 base path, the error envelope on a real 500 and a
#   404, ISO-8601-UTC-'Z' timestamps, the EF Core + SQLite round-trip, CORS headers.
#   M1·a (§4·a/§4·b): GET /hosts + /hosts/{id} (this host's capacity + capability block),
#   the 404 envelope on an unknown host, and the honesty coupling — metrics 'operational'
#   iff capacity is present, 'down' iff capacity is null (never a fabricated number).
#   M1·b (§3): GET /servers + /servers/{id} (kgsm-lib domain+run-state ⋈ monitor metrics) —
#   the honest Server DTO, the 404 envelope, and the per-server metrics<->presence coupling.
#   M2 (§3·b/§3·j): GET /api/v1/stream WebSocket — the { topic, type, data } envelope, subscribe,
#   per-server + host metric ticks carried through honestly, the servers topic staying quiet under
#   the metric firehose, and the capability lifecycle: kill the monitor -> metric ticks fall silent +
#   a capabilities patch reports metrics 'down' (provisioned:true — never "lost"); restart it ->
#   metrics flips back 'operational' + ticks resume. Degrade AND recover gracefully, capability set fixed.
#   M4·a (§3·f): auth is ON by default. The M0–M3 checks above run under KGSM_API_AUTH_DISABLED=1 (the
#   dev escape hatch — synthetic admin), then a dedicated AUTH-ENABLED instance proves the no-token
#   sweep: every protected endpoint 401s with the frozen envelope, /health + /api/v1 stay open, and the
#   login endpoint 503s until Discord is configured (the M4·b live half). The full 401/403/tier matrix +
#   the callback/refresh/session flow are proven in-process by tests/Api.Tests (the Discord seam faked).
#   M6·b (§3·g): the ports surface degrade path (no firewall configured here) — open_ports is an admitted
#   verb (unknown server → 404, not a 400), the server DETAIL `network` block reports firewall:"absent" +
#   reachable:null (reserved) + every required open:null (never fabricated false), the list OMITS network
#   (detail≠list), and the host grid is omitted when the firewall is absent. The operational firewall path
#   (open verdicts + the open_ports apply/audit/verify) is a trusted-host live-validate.
#
# Two phases. Phase A runs deterministically with NO monitor (the degrade path: host metrics
# down + capacity null; every server metrics:null). Phase B starts an EMBEDDED stub monitor
# (a unix socket serving a canned Snapshot with a per-server row keyed to a real instance) and
# restarts the API at it — proving the happy path: host capacity present + the servers JOIN's
# present-branch (metrics carried through, keyed by id). It then opens a WebSocket and, mid-stream,
# KILLS then RESTARTS the stub to prove the monitor-down/up capability lifecycle. The stub makes all of this deterministic
# with no external monitor; SMOKE_MONITOR_SOCKET still overrides Phase A's monitor if you want a live one.
# What this CANNOT prove is a real browser preflight (CORS is browser-enforced) or an actual SPA
# fetch — those wait on frontend access. The WebSocket client is a ~70-line stdlib Python RFC6455
# client (no websocat/wscat/websockets dependency, none of which are guaranteed on a host).
#
# Usage:  scripts/smoke.sh                 # build + run all checks (both phases)
#         SMOKE_PORT=9001 scripts/smoke.sh
#         SMOKE_SKIP_BUILD=1 scripts/smoke.sh   # reuse the existing Release build
#         SMOKE_KGSM_PATH=/path/to/kgsm.sh scripts/smoke.sh   # engine on another host
#         SMOKE_MONITOR_SOCKET=/run/kgsm-monitor/metrics.sock scripts/smoke.sh   # live monitor in Phase A
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${SMOKE_PORT:-8099}"
BASE="http://127.0.0.1:${PORT}"
DLL="${REPO_ROOT}/src/Api/bin/Release/net10.0/kgsm-api.dll"
DB="${SMOKE_DB:-/tmp/kgsm-api-smoke.db}"; rm -f "$DB"
METRICS_DB="${SMOKE_METRICS_DB:-/tmp/kgsm-api-smoke-metrics.db}"; rm -f "$METRICS_DB"

# M1·a leaf wiring (deterministic defaults: no monitor, no watchdog, no assistant).
HOST_ID="${SMOKE_HOST_ID:-smoke-host}"
MON_SOCK="${SMOKE_MONITOR_SOCKET:-/tmp/kgsm-api-smoke-nomonitor.sock}"  # absent by default
WD_SOCK="${SMOKE_WATCHDOG_SOCKET:-}"                                    # empty -> watchdog absent

# M1·b engine wiring. kgsm-lib is base, not a leaf: point it at the canonical dev checkout so
# GET /servers reads a real roster. Override with SMOKE_KGSM_PATH on another host.
KGSM_PATH="${SMOKE_KGSM_PATH:-/home/heisen/tks/kgsm/kgsm.sh}"
# M5 event socket: a DEDICATED temp path the audit consumer binds — NEVER the shared default
# (/run/kgsm-api/kgsm-events.sock), since the listener deletes any file at its path before binding and
# could clobber another consumer's live socket. Smoke fires no kgsm events, so this just proves the
# consumer wires up without touching anything real.
KGSM_SOCK="${SMOKE_KGSM_SOCKET:-/tmp/kgsm-api-smoke-events.sock}"; rm -f "$KGSM_SOCK"

# M4·a: auth is ON by default. The M0–M3 checks below run under the dev escape hatch (synthetic admin)
# so they exercise the domain contracts unchanged; the auth boundary itself gets its own ENABLED
# instance + no-token sweep at the end (and the full tier matrix lives in tests/Api.Tests).
export KGSM_API_AUTH_DISABLED=1
# Steam covers are keyless + ON by default, so without this the worker would fetch real Steam capsules over
# the network and the offline "cover null (no RAWG key)" assertions below would flake. Disable it here so the
# smoke stays offline + deterministic; the Steam-primary/RAWG-fallback logic is unit-tested with fakes.
export KGSM_API_STEAM_COVERS_DISABLED=1
# Unix socket for the embedded stub monitor (Phase B — proves the join's present-branch).
STUB_SOCK="/tmp/kgsm-api-smoke-stub-monitor.sock"; rm -f "$STUB_SOCK"
# Canned per-server metric values the stub serves; the join must carry these through verbatim.
STUB_CPU=142.5; STUB_MEM=3422552064; STUB_IO_READ=4096; STUB_PIDS=12  # ioWrite is null (nullable passthrough)
STUB_DISK=293172125  # diskBytes (Contracts 1.2.0): per-server on-disk footprint, passed through verbatim
# M2 realtime: the stdlib WebSocket client + where it logs the frames it receives.
WS_PY="/tmp/kgsm-api-smoke-ws.py"
WS_LOG="/tmp/kgsm-api-smoke-ws.log"
# M7 assistant relay: a TCP HTTP stub serving /health + a canned §5·a /turn SSE that GATES on the relay
# secret and echoes the forwarded user — the deterministic stub analogue of the M2 stub monitor, proving
# the API forwards X-Relay-Secret + X-Relay-User and streams the body verbatim (the relay machinery the
# gate-only checks never reach).
ASSIST_PORT="$(( PORT + 1 ))"
ASSIST_URL="http://127.0.0.1:${ASSIST_PORT}"
REL_SECRET="smoke-relay-secret"
STUB_ASSIST_PY="/tmp/kgsm-api-smoke-stub-assistant.py"

pass=0; fail=0
ok()  { printf '  \033[32mPASS\033[0m %s\n' "$1"; pass=$((pass+1)); }
bad() { printf '  \033[31mFAIL\033[0m %s\n' "$1"; fail=$((fail+1)); }

# --- build -----------------------------------------------------------------
if [[ "${SMOKE_SKIP_BUILD:-0}" != "1" || ! -f "$DLL" ]]; then
  echo "==> Building (Release)…"
  dotnet build "${REPO_ROOT}/src/Api/Api.csproj" -c Release >/dev/null 2>&1 \
    || { echo "build FAILED"; exit 2; }
fi
[[ -f "$DLL" ]] || { echo "assembly missing: $DLL"; exit 2; }

# --- lifecycle helpers ------------------------------------------------------
PIDS=()
cleanup() {
  for p in "${PIDS[@]:-}"; do kill "$p" 2>/dev/null; done
  for p in "${PIDS[@]:-}"; do wait "$p" 2>/dev/null; done
  rm -f "$STUB_SOCK" "$STUB_ASSIST_PY" 2>/dev/null
}
trap cleanup EXIT

# start_api MONITOR_SOCKET — launch the API with the given monitor socket; wait for /health.
start_api() {
  KGSM_API_URLS="$BASE" KGSM_API_DB="$DB" KGSM_API_METRICS_HISTORY_DB="$METRICS_DB" \
  KGSM_API_METRICS_PERSIST_MS="${SMOKE_METRICS_PERSIST_MS:-5000}" \
  KGSM_API_HOST_ID="$HOST_ID" KGSM_API_MONITOR_SOCKET="$1" KGSM_API_WATCHDOG_SOCKET="$WD_SOCK" \
  KGSM_API_KGSM_PATH="$KGSM_PATH" KGSM_API_KGSM_SOCKET="$KGSM_SOCK" \
    dotnet "$DLL" >/tmp/kgsm-api-smoke.log 2>&1 &
  SRV=$!; PIDS+=("$SRV")
  for _ in $(seq 1 80); do curl -fsS "${BASE}/health" >/dev/null 2>&1 && return 0; sleep 0.1; done
  return 1
}
stop_api() { kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; }

# start_api_auth — launch with auth ENABLED (the escape hatch unset for this child only), Discord
# unconfigured (ephemeral signing key). Used by the M4·a no-token sweep.
start_api_auth() {
  env -u KGSM_API_AUTH_DISABLED \
    KGSM_API_URLS="$BASE" KGSM_API_DB="$DB" KGSM_API_METRICS_HISTORY_DB="$METRICS_DB" \
    KGSM_API_HOST_ID="$HOST_ID" \
    KGSM_API_MONITOR_SOCKET="$MON_SOCK" KGSM_API_WATCHDOG_SOCKET="$WD_SOCK" KGSM_API_KGSM_PATH="$KGSM_PATH" \
    KGSM_API_KGSM_SOCKET="$KGSM_SOCK" \
    dotnet "$DLL" >/tmp/kgsm-api-smoke-auth.log 2>&1 &
  SRV=$!; PIDS+=("$SRV")
  for _ in $(seq 1 80); do curl -fsS "${BASE}/health" >/dev/null 2>&1 && return 0; sleep 0.1; done
  return 1
}

# wait_caps_warm — block until the always-on LeafHealthMonitor has finished its first /health poll, so
# the §4·b capability statuses have left their cold 'unknown' (makes the capability assertions deterministic).
wait_caps_warm() {
  for _ in $(seq 1 50); do
    if curl -fsS "${BASE}/api/v1/hosts/${HOST_ID}" -o /tmp/kgsm-api-smoke.body 2>/dev/null && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if d['capabilities']['metrics']['status']!='unknown' else 1)
" 2>/dev/null; then return 0; fi
    sleep 0.1
  done
  return 1
}

# start_api_assistant URL SECRET — launch the API (auth still disabled) pointed at a stub assistant +
# the relay secret, so the M7 relay path can actually execute. Same monitor/watchdog/kgsm wiring as
# start_api (the absent defaults); only the assistant is added.
start_api_assistant() {
  KGSM_API_URLS="$BASE" KGSM_API_DB="$DB" KGSM_API_HOST_ID="$HOST_ID" \
  KGSM_API_MONITOR_SOCKET="$MON_SOCK" KGSM_API_WATCHDOG_SOCKET="$WD_SOCK" \
  KGSM_API_KGSM_PATH="$KGSM_PATH" KGSM_API_KGSM_SOCKET="$KGSM_SOCK" \
  KGSM_API_ASSISTANT_URL="$1" KGSM_API_ASSISTANT_RELAY_SECRET="$2" \
    dotnet "$DLL" >/tmp/kgsm-api-smoke-m7.log 2>&1 &
  SRV=$!; PIDS+=("$SRV")
  for _ in $(seq 1 80); do curl -fsS "${BASE}/health" >/dev/null 2>&1 && return 0; sleep 0.1; done
  return 1
}

# wait_assistant_operational — block until the LeafHealthMonitor has polled the stub assistant's
# /health and flipped the §4·b assistant capability to 'operational' (so the relay's capability gate
# admits the call instead of 503-ing on a cold/down read).
wait_assistant_operational() {
  for _ in $(seq 1 60); do
    if curl -fsS "${BASE}/api/v1/hosts/${HOST_ID}" -o /tmp/kgsm-api-smoke.body 2>/dev/null && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if d['capabilities']['assistant']['status']=='operational' else 1)
" 2>/dev/null; then return 0; fi
    sleep 0.2
  done
  return 1
}

# req METHOD PATH [extra curl args...] -> sets $CODE and $BODY
req() {
  local method="$1" path="$2"; shift 2
  curl -s -X "$method" -o /tmp/kgsm-api-smoke.body -w '%{http_code}' "$@" "${BASE}${path}" > /tmp/kgsm-api-smoke.code
  CODE="$(cat /tmp/kgsm-api-smoke.code)"; BODY="$(cat /tmp/kgsm-api-smoke.body)"
}

# --- Phase A: no monitor (degrade path) ------------------------------------
echo "==> Phase A — Starting API on ${BASE} (no monitor; kgsm=${KGSM_PATH})"
start_api "$MON_SOCK" || { echo "API never became healthy; log:"; tail -20 /tmp/kgsm-api-smoke.log; exit 2; }
wait_caps_warm || echo "  (warn: capability status still 'unknown' after warm-wait)"

echo "==> Checks"

# 1. /health — 200, camelCase liveness, Z timestamp (the API's own, unified ecosystem path)
req GET /health
[[ "$CODE" == 200 ]] && grep -q '"status":"ok"' <<<"$BODY" && grep -q '"service":"kgsm-api"' <<<"$BODY" \
  && ok "/health 200 + {status,service}" || bad "/health shape (code=$CODE body=$BODY)"

# 2. timestamp convention: ISO-8601 UTC ending in 'Z', NOT the +00:00 offset form
if grep -qE '"time":"[0-9T:.\-]+Z"' <<<"$BODY" && ! grep -q '+00:00' <<<"$BODY"; then
  ok "timestamp is ISO-8601 UTC 'Z'"
else
  bad "timestamp not 'Z' form (body=$BODY)"
fi

# 3. /api/v1 root handshake — 200 + version
req GET /api/v1
[[ "$CODE" == 200 ]] && grep -q '"name":"kgsm-api"' <<<"$BODY" && grep -q '"version":"v1"' <<<"$BODY" \
  && ok "/api/v1 200 + {name,version}" || bad "/api/v1 handshake (code=$CODE body=$BODY)"

# 3b. /api/v1/ping — the SPA's latency target: 200 + minimal {pong:true} body, no auth
req GET /api/v1/ping
[[ "$CODE" == 200 ]] && grep -q '"pong":true' <<<"$BODY" \
  && ok "/api/v1/ping 200 + {pong:true}" || bad "/api/v1/ping (code=$CODE body=$BODY)"

# 4. EF Core + SQLite round-trip (the data layer wiring, end-to-end). M5: a READ round-trip now (connect
#    -> EnsureCreated the audit schema -> count) — the audit table is append-only, so the probe never
#    writes a fake row. Fresh DB => auditRows:0; the point is the wiring builds + queries.
req GET /api/v1/_dbcheck
[[ "$CODE" == 200 ]] && grep -qE '"auditRows":[0-9]' <<<"$BODY" \
  && ok "EF+SQLite round-trip ($(grep -oE '"auditRows":[0-9]+' <<<"$BODY"))" || bad "_dbcheck (code=$CODE body=$BODY)"

# 5. error envelope on a REAL 500 (exception path, not assumed)
req GET /api/v1/_throw
[[ "$CODE" == 500 ]] && grep -q '"error":{' <<<"$BODY" && grep -q '"code":"internal_error"' <<<"$BODY" \
  && ok "500 returns {error:{code,message}}" || bad "500 envelope (code=$CODE body=$BODY)"

# 6. error envelope on a 404 (unmatched route → contract, not empty body)
req GET /api/v1/nope
[[ "$CODE" == 404 ]] && grep -q '"code":"not_found"' <<<"$BODY" \
  && ok "404 returns {error:{code:not_found}}" || bad "404 envelope (code=$CODE body=$BODY)"

# 7. CORS — simple GET with Origin reflects an Access-Control-Allow-Origin header
ACAO="$(curl -s -D - -o /dev/null -H 'Origin: http://example.test' "${BASE}/api/v1" \
        | grep -i '^access-control-allow-origin:' || true)"
[[ -n "$ACAO" ]] && ok "CORS header present (${ACAO%$'\r'})" || bad "no Access-Control-Allow-Origin header"

# 8. CORS preflight — OPTIONS returns allow-methods
ACAM="$(curl -s -D - -o /dev/null -X OPTIONS \
        -H 'Origin: http://example.test' -H 'Access-Control-Request-Method: GET' \
        "${BASE}/api/v1" | grep -i '^access-control-allow-methods:' || true)"
[[ -n "$ACAM" ]] && ok "CORS preflight allow-methods (${ACAM%$'\r'})" || bad "no preflight allow-methods"

# --- M1·a: hosts (kgsm-monitor scrape + §4·b capabilities) -----------------
echo "==> M1·a host checks (monitor=${MON_SOCK}; watchdog=${WD_SOCK:-<absent>})"

# 9. GET /hosts — per-host api returns exactly this one host, status online
req GET /api/v1/hosts
if [[ "$CODE" == 200 ]] && EXP="$HOST_ID" python3 -c "
import json,os,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if (isinstance(d,list) and len(d)==1 and d[0]['id']==os.environ['EXP'] and d[0]['status']=='online') else 1)
" 2>/dev/null; then
  ok "/hosts 200 + single host {id,status:online}"
else bad "/hosts shape (code=$CODE body=$BODY)"; fi

# 10. GET /hosts/{id} — capability block present; provisioning reflects config
req GET "/api/v1/hosts/${HOST_ID}"
if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
c=d.get('capabilities',{})
ok=(all(k in c for k in ('metrics','assistant','watchdog'))
    and all('provisioned' in c[k] and 'status' in c[k] for k in ('metrics','assistant','watchdog'))
    and c['metrics']['provisioned'] is True       # monitor socket configured
    and c['assistant']['provisioned'] is False)    # assistant unset -> absent
sys.exit(0 if ok else 1)
" 2>/dev/null; then
  ok "/hosts/{id} 200 + capabilities {metrics,assistant,watchdog}"
else bad "/hosts/{id} capability block (code=$CODE body=$BODY)"; fi

# 11. GET /hosts/{unknown} — 404 returns OUR error envelope, not framework ProblemDetails
req GET /api/v1/hosts/does-not-exist
[[ "$CODE" == 404 ]] && grep -q '"code":"not_found"' <<<"$BODY" && ! grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY" \
  && ok "/hosts/{unknown} 404 → {error:{code:not_found}}" || bad "/hosts unknown 404 envelope (code=$CODE body=$BODY)"

# 12. The honesty invariant, now that status comes from /health and capacity from /metrics (two
#     independent sources): capacity present REQUIRES operational; a non-operational metrics capability
#     REQUIRES null capacity. operational may have null capacity (monitor up but warming, no frame yet) —
#     never a fabricated number, and never run-state inferred from frame-presence (metric-presence ≠ status).
req GET "/api/v1/hosts/${HOST_ID}"
if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
m=d['capabilities']['metrics']['status']
# ANY honestly-measured field present (the M1·a capacity trio + the M-diag telemetry) ⇒ operational
# (equivalently: a non-operational metrics capability REQUIRES every one of them null — no fabricated leak).
present = any(d.get(k) is not None for k in
              ('cpuPct','mem','disks','perCore','load','diskIo','interfaces','hostname','uptimeSec','sampleTs'))
sys.exit(0 if (m=='operational' or not present) else 1)
" 2>/dev/null; then
  STATUS="$(python3 -c "import json;print(json.load(open('/tmp/kgsm-api-smoke.body'))['capabilities']['metrics']['status'])" 2>/dev/null)"
  ok "metrics '${STATUS}' ↔ capacity coupling (present⇒operational; honest, never fabricated)"
else bad "capacity/metrics coupling violated (body=$BODY)"; fi

# --- M1·b: servers (kgsm-lib domain+run-state ⋈ monitor metrics) -----------
echo "==> M1·b server checks — Phase A degrade (no monitor; kgsm=${KGSM_PATH})"

# 13. GET /servers — honest DTO shape: stable keys, valid status/runtime enums, this host's id.
req GET /api/v1/servers
if [[ "$CODE" == 200 ]] && EXP="$HOST_ID" python3 -c "
import json,os,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
if not (isinstance(d,list) and len(d)>=1): sys.exit(2)   # empty roster -> can't prove a real read
keys={'id','name','blueprint','status','version','runtime','hostId','steamAppId','clientSteamAppId','isSteamAccountRequired','metrics','updateAvailable','startedAt'}
for s in d:
    if set(s)!=keys: sys.exit(3)
    if s['status'] not in ('running','stopped','unknown'): sys.exit(4)
    if s['runtime'] not in ('native','container'): sys.exit(5)
    if s['hostId']!=os.environ['EXP']: sys.exit(6)
sys.exit(0)
" 2>/dev/null; then
  N="$(python3 -c "import json;print(len(json.load(open('/tmp/kgsm-api-smoke.body'))))" 2>/dev/null)"
  ok "/servers 200 + honest DTO shape (n=${N})"
else bad "/servers shape (code=$CODE body=$BODY) [empty roster? set SMOKE_KGSM_PATH]"; fi

# The join in Phase B keys on a real instance id; capture the first one from the roster.
FIRST_ID="$(python3 -c "import json;d=json.load(open('/tmp/kgsm-api-smoke.body'));print(d[0]['id'] if d else '')" 2>/dev/null)"

# 14. Degrade honesty: with no monitor, EVERY server's metrics is null (not a fabricated zero).
req GET /api/v1/servers
if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if all(s['metrics'] is None for s in d) else 1)
" 2>/dev/null; then
  ok "/servers metrics:null with no monitor (honest unknown, never fabricated)"
else bad "/servers metrics not null under degrade (body=$BODY)"; fi

# 15. GET /servers/{unknown} — OUR 404 envelope, not framework ProblemDetails.
req GET /api/v1/servers/does-not-exist
[[ "$CODE" == 404 ]] && grep -q '"code":"not_found"' <<<"$BODY" && ! grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY" \
  && ok "/servers/{unknown} 404 → {error:{code:not_found}}" || bad "/servers unknown 404 envelope (code=$CODE body=$BODY)"

# --- M8·a: GET /library (the installable-game catalog — blueprint scrape ⋈ RAWG cover/metadata) -----
# Phase A reads the REAL dev kgsm catalog (no monitor needed — blueprints are engine-only). Proves the
# honest DTO end-to-end: the frozen key set, structured ports emitted directly by kgsm (no C# parse),
# steam-id honesty (null for a non-Steam blueprint, never "0"). Cover hydration is OFF in this run — no RAWG
# key AND KGSM_API_STEAM_COVERS_DISABLED=1 (set above so the keyless Steam source can't reach the network) —
# so cover/hero stay null and genres/tags []; rawgSlug is still POPULATED from the curated blueprints
# (Phase 1 wrote 29 slugs) — so we assert string-or-null and that at least one is non-null.
echo "==> M8·a library checks — Phase A (live blueprint catalog; kgsm=${KGSM_PATH})"

# 15b. GET /library — honest shape + structured ports + steam honesty + RAWG fields (offline: cover/hero
#      null, genres/tags [], rawgSlug populated from the blueprints).
req GET /api/v1/library
if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
if not (isinstance(d,list) and len(d)>=1): sys.exit(2)   # empty catalog -> can't prove a real read
keys={'id','name','type','steamAppId','clientSteamAppId','isSteamAccountRequired','ports','specs',
      'cover','hero','description','genres','tags','rawgSlug'}
speckeys={'maxPlayers','minRamMb','recommendedRamMb','baseDiskMb'}
saw_range=saw_steam=saw_null_steam=saw_slug=False
for e in d:
    if set(e)!=keys: sys.exit(3)
    if e['type'] not in ('native','container'): sys.exit(4)
    if e['cover'] is not None: sys.exit(5)               # opt-in (no key) -> cover null offline
    if e['hero'] is not None: sys.exit(6)                # opt-in (no key) -> hero null offline
    if e['description'] is not None and not isinstance(e['description'],str): sys.exit(7)
    if e['genres']!=[]: sys.exit(8)                      # opt-in -> genres [] offline (honest empty, not null)
    if e['tags']!=[]: sys.exit(9)                        # opt-in -> tags [] offline
    if e['rawgSlug'] is not None:
        if not isinstance(e['rawgSlug'],str): sys.exit(10)
        saw_slug=True
    if set(e['specs'])!=speckeys: sys.exit(11)           # keys present (values null today, uncurated)
    if not isinstance(e['ports'],list): sys.exit(12)
    for p in e['ports']:
        if set(p)!={'start','end','proto'}: sys.exit(13)
        if not (isinstance(p['start'],int) and isinstance(p['end'],int) and p['start']<=p['end']): sys.exit(14)
        if p['proto'] not in ('tcp','udp'): sys.exit(15)
        if p['start']<p['end']: saw_range=True
    if e['steamAppId'] is not None: saw_steam=True
    else: saw_null_steam=True
# the live catalog must exercise: a real multi-port range (kgsm range-preserving) and steam honesty both ways
if not (saw_range and saw_steam and saw_null_steam): sys.exit(16)
# at least one blueprint carries a curated rawg_slug -> proves Phase 2 (lib 1.23.0) -> Phase 3 wiring
if not saw_slug: sys.exit(17)
if 'factorio' not in {e['id'] for e in d}: sys.exit(18)  # a known blueprint -> a real engine read
sys.exit(0)
" 2>/dev/null; then
  N="$(python3 -c "import json;print(len(json.load(open('/tmp/kgsm-api-smoke.body'))))" 2>/dev/null)"
  ok "/library 200 + honest shape (n=${N}): structured ports (from kgsm, no C# parse), steam null-honesty, rawgSlug populated, cover/hero null + genres/tags [] (RAWG opt-in, no key)"
else bad "/library shape (code=$CODE body=$BODY) [empty catalog? set SMOKE_KGSM_PATH]"; fi

# 15c. ?q= narrows by id/name (case-insensitive); a no-match returns [] (never a fabricated row).
req GET '/api/v1/library?q=factorio'
q_ok=false
if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if (isinstance(d,list) and len(d)>=1 and all('factorio' in (e['id']+e['name']).lower() for e in d)) else 1)
" 2>/dev/null; then
  req GET '/api/v1/library?q=zzzznotagame'
  [[ "$CODE" == 200 ]] && [[ "$(tr -d ' \n' < /tmp/kgsm-api-smoke.body)" == "[]" ]] && q_ok=true
fi
$q_ok && ok "/library?q= filters by id/name (factorio→matches, no-match→[])" || bad "/library q filter (code=$CODE body=$BODY)"

# 15d. POST /library/refresh — the admin on-demand re-fetch trigger. Under the dev escape hatch (synthetic
#      admin) it returns 202 (the sweep runs off the request thread; with no RAWG key + Steam disabled here it
#      finds nothing to fetch and no-ops). A second immediate call may 202 (prior finished) or 409 (still in
#      flight) — both are honest, so accept either.
req POST /api/v1/library/refresh
[[ "$CODE" == 202 ]] \
  && ok "POST /library/refresh → 202 (admin on-demand refresh accepted, runs off the request thread)" \
  || bad "/library/refresh (code=$CODE body=$BODY)"

stop_api

# --- Phase B: embedded stub monitor → host happy path + the servers JOIN present-branch ----
echo "==> M1·b server checks — Phase B join (stub monitor at ${STUB_SOCK})"

if [[ -z "$FIRST_ID" ]]; then
  echo "  (skipping Phase B: empty roster — no instance id to key the join on; set SMOKE_KGSM_PATH)"
else
  # A tiny unix-socket HTTP stub that serves a canned monitor Snapshot (camelCase, matching
  # TheKrystalShip.KGSM.Monitor.Contracts) with ONE per-server row keyed to $FIRST_ID. cpuPctCore
  # is >100 (% of one core, can exceed 100) and ioWriteBps is null — both exercise honest passthrough.
  STUB_PY="/tmp/kgsm-api-smoke-stub.py"
  cat > "$STUB_PY" <<'PYEOF'
import socket, os, json, sys
sock_path = sys.argv[1]
sid = os.environ.get('SNAP_ID', 'x')
snap = {
  "ts": 1718400000000, "intervalMs": 1000, "hostname": "smoke-stub", "uptimeSec": 12345,
  # cpu.info is the Monitor.Contracts 1.1.0 STATIC identity (rides the Host view only, not the metrics tick).
  "cpu": {"totalPct": 12.5, "perCore": [10.0, 15.0], "load": {"one": 0.4, "five": 0.5, "fifteen": 0.6},
          "info": {"model": "AMD Ryzen 7 3800X 8-Core Processor", "cores": 8, "threads": 16, "maxFreqGhz": 3.9}},
  # cachedKb/buffersKb are the 1.1.0 mem-depth fields (KiB → GiB on the wire, like the other mem figures).
  "mem": {"totalKb": 32768000, "availableKb": 16384000, "usedKb": 16384000, "usedPct": 50.0,
          "swapTotalKb": 0, "swapUsedKb": 0, "cachedKb": 4194304, "buffersKb": 1048576},
  # device is the 1.1.0 backing-disk MODEL string (rides the shared DiskCapacity shape → both Host + tick).
  "disk": {"mounts": [{"mount": "/", "fs": "ext4", "totalBytes": 500000000000, "usedBytes": 250000000000,
                       "usedPct": 50.0, "device": "Samsung SSD 990 EVO Plus 1TB"}], "io": {"readBps": 1000, "writeBps": 2000}},
  # mac/errors are the 1.1.0 iface-depth fields; errors:0 is a GENUINE zero (never conflated with unknown null).
  "net": {"ifaces": [{"name": "eth0", "rxBps": 100, "txBps": 200, "rxPps": 1, "txPps": 2,
                      "mac": "aa:bb:cc:dd:ee:ff", "errors": 0}]},
  # sensors is the 1.1.0 hwmon list — a non-nullable array in the contract; the stub serves one real row so the
  # Host/tick mapping is exercised (an empty array is the honest no-hwmon case, never an invented row).
  "sensors": [{"chip": "k10temp", "label": "Tctl", "valueC": 42.5}],
  # diskBytes is the 1.2.0 per-server on-disk footprint (slow-cadence working-dir walk); carried through 1:1.
  "servers": [{"id": sid, "name": sid, "kind": "native",
               "cpuPctCore": float(os.environ['SNAP_CPU']), "memBytes": int(os.environ['SNAP_MEM']),
               "ioReadBps": int(os.environ['SNAP_IOREAD']), "ioWriteBps": None, "pids": int(os.environ['SNAP_PIDS']),
               "diskBytes": int(os.environ['SNAP_DISK'])}],
}
body = json.dumps(snap).encode()
resp = b"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + str(len(body)).encode() + b"\r\nConnection: close\r\n\r\n" + body
try: os.unlink(sock_path)
except FileNotFoundError: pass
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
s.bind(sock_path); s.listen(16)
while True:
    try: conn, _ = s.accept()
    except OSError: break
    try:
        conn.recv(65536); conn.sendall(resp)
    except OSError: pass
    finally: conn.close()
PYEOF

  echo "  starting stub monitor (join keyed to '${FIRST_ID}')"
  SNAP_ID="$FIRST_ID" SNAP_CPU="$STUB_CPU" SNAP_MEM="$STUB_MEM" SNAP_IOREAD="$STUB_IO_READ" SNAP_PIDS="$STUB_PIDS" SNAP_DISK="$STUB_DISK" \
    python3 "$STUB_PY" "$STUB_SOCK" >/tmp/kgsm-api-smoke-stub.log 2>&1 &
  STUB_PID=$!; PIDS+=("$STUB_PID")
  for _ in $(seq 1 40); do [[ -S "$STUB_SOCK" ]] && break; sleep 0.1; done
  start_api "$STUB_SOCK" || { echo "API never healthy (Phase B); log:"; tail -20 /tmp/kgsm-api-smoke.log; exit 2; }
  wait_caps_warm || echo "  (warn: capability status still 'unknown' after warm-wait)"

  # 16. Host happy path, now deterministic: metrics operational + capacity present (M1·a, via stub).
  req GET "/api/v1/hosts/${HOST_ID}"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
ok = (d['capabilities']['metrics']['status']=='operational'
      and d['cpuPct'] is not None and d['mem'] is not None and d['disks'] is not None
      # M-diag enriched telemetry carried through verbatim from the stub snapshot:
      and d.get('perCore')==[10.0,15.0]
      and d.get('load')=={'one':0.4,'five':0.5,'fifteen':0.6}
      and d['mem'].get('available') is not None and d['mem'].get('swapTotal') is not None
      and d['disks'][0].get('fs')=='ext4'
      and d.get('diskIo')=={'readBps':1000,'writeBps':2000}
      and isinstance(d.get('interfaces'),list) and d['interfaces'][0]['name']=='eth0'
      and d.get('hostname')=='smoke-stub' and d.get('uptimeSec')==12345
      and d.get('sampleTs')==1718400000000
      # M-diag depth (Monitor.Contracts 1.1.0): cached/buffers KiB→GiB, cpu-info passthrough (maxFreq already
      # GHz), iface mac + GENUINE errors:0, disk device model, and the hwmon sensor row — all honest passthrough.
      and d['mem'].get('cached')==4.0 and d['mem'].get('buffers')==1.0          # 4194304 KiB=4 GiB, 1048576=1 GiB
      and d.get('cpu')=={'model':'AMD Ryzen 7 3800X 8-Core Processor','cores':8,'threads':16,'maxFreqGhz':3.9}
      and d['interfaces'][0].get('mac')=='aa:bb:cc:dd:ee:ff' and d['interfaces'][0].get('errors')==0
      and d['disks'][0].get('device')=='Samsung SSD 990 EVO Plus 1TB'
      and d.get('sensors')==[{'chip':'k10temp','label':'Tctl','valueC':42.5}])
sys.exit(0 if ok else 1)
" 2>/dev/null; then
    ok "host metrics operational + capacity + M-diag telemetry + 1.1.0 depth (cpu-info/sensors/mem-cached/mac/device) present (stub snapshot)"
  else bad "host happy-path capacity (code=$CODE body=$BODY)"; fi

  # 17. The JOIN present-branch (detail path): the monitor row is carried through VERBATIM, keyed by id.
  req GET "/api/v1/servers/${FIRST_ID}"
  if [[ "$CODE" == 200 ]] && \
     CPU="$STUB_CPU" MEM="$STUB_MEM" IOREAD="$STUB_IO_READ" PIDS_E="$STUB_PIDS" DISK="$STUB_DISK" python3 -c "
import json,os,sys
m=json.load(open('/tmp/kgsm-api-smoke.body')).get('metrics')
if m is None: sys.exit(1)
sys.exit(0 if (abs(m['cpuPctCore']-float(os.environ['CPU']))<1e-6
               and m['memBytes']==int(os.environ['MEM'])
               and m['ioReadBps']==int(os.environ['IOREAD'])
               and m['ioWriteBps'] is None
               and m['pids']==int(os.environ['PIDS_E'])
               and m['diskBytes']==int(os.environ['DISK'])) else 2)
" 2>/dev/null; then
    ok "/servers/{id} JOIN present-branch (cpuPctCore>100 + null ioWrite + diskBytes carried through, keyed by id)"
  else bad "/servers/{id} join present-branch (code=$CODE body=$BODY)"; fi

  # 18. Server-side metrics<->presence coupling on the LIST path: the joined server has metrics.
  req GET /api/v1/servers
  if [[ "$CODE" == 200 ]] && ID="$FIRST_ID" python3 -c "
import json,os,sys
s=next((x for x in json.load(open('/tmp/kgsm-api-smoke.body')) if x['id']==os.environ['ID']), None)
sys.exit(0 if (s is not None and s['metrics'] is not None) else 1)
" 2>/dev/null; then
    ok "/servers metrics present ↔ monitor operational (server-side coupling)"
  else bad "/servers list join coupling (body=$BODY)"; fi

  # --- M2 realtime: the /api/v1/stream WebSocket -----------------------------
  echo "==> M2 realtime checks — WebSocket /api/v1/stream (stub up, then killed mid-stream)"

  # 19. A plain (non-upgrade) GET on the stream endpoint is a client error -> OUR 400 envelope.
  req GET /api/v1/stream
  [[ "$CODE" == 400 ]] && grep -q '"code":"bad_request"' <<<"$BODY" && ! grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY" \
    && ok "/stream non-WS GET 400 → {error:{code:bad_request}}" || bad "/stream non-WS 400 envelope (code=$CODE body=$BODY)"

  # A stdlib RFC6455 client: handshake, send one subscribe, log every received text frame as a
  # {"t":<rel-sec>,"msg":{topic,type,data}} line. Masks client frames; ignores pings; runs DURATION secs.
  cat > "$WS_PY" <<'PYEOF'
import socket, os, sys, json, base64, struct, time
host, port, dur = "127.0.0.1", int(sys.argv[1]), float(sys.argv[2])
topics = json.loads(sys.argv[3])
key = base64.b64encode(os.urandom(16)).decode()
req = (f"GET /api/v1/stream HTTP/1.1\r\nHost: {host}:{port}\r\n"
       "Upgrade: websocket\r\nConnection: Upgrade\r\n"
       f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")
s = socket.create_connection((host, port), timeout=5)
s.sendall(req.encode())
resp = b""
while b"\r\n\r\n" not in resp:
    chunk = s.recv(4096)
    if not chunk:
        print(json.dumps({"err": "handshake-eof"})); sys.exit(3)
    resp += chunk
head, _, rest = resp.partition(b"\r\n\r\n")
if b" 101 " not in head.split(b"\r\n", 1)[0]:
    print(json.dumps({"err": "no-101", "head": head[:80].decode("latin1")})); sys.exit(3)
buf = bytearray(rest)

def send_text(txt):
    data = txt.encode(); mask = os.urandom(4); n = len(data); hdr = bytearray([0x81])
    if n < 126: hdr.append(0x80 | n)
    elif n < 65536: hdr.append(0x80 | 126); hdr += struct.pack(">H", n)
    else: hdr.append(0x80 | 127); hdr += struct.pack(">Q", n)
    hdr += mask
    s.sendall(bytes(hdr) + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))

send_text(json.dumps({"type": "subscribe", "topics": topics}))
s.settimeout(0.5)
deadline = time.monotonic() + dur

def read_exact(n):
    while len(buf) < n:
        try: chunk = s.recv(4096)
        except socket.timeout:
            if time.monotonic() > deadline: return None
            continue
        if not chunk: return None
        buf.extend(chunk)
    out = bytes(buf[:n]); del buf[:n]; return out

while time.monotonic() < deadline:
    h = read_exact(2)
    if h is None: break
    op = h[0] & 0x0f; ln = h[1] & 0x7f
    if ln == 126:
        e = read_exact(2)
        if e is None: break
        ln = struct.unpack(">H", e)[0]
    elif ln == 127:
        e = read_exact(8)
        if e is None: break
        ln = struct.unpack(">Q", e)[0]
    payload = read_exact(ln) if ln else b""
    if payload is None: break
    if op == 0x8: break        # close
    if op == 0x9: continue     # ping (no pong needed in this short window)
    if op == 0x1:
        try: msg = json.loads(payload.decode())
        except Exception: continue
        print(json.dumps({"t": round(time.monotonic(), 3), "msg": msg}), flush=True)
try: s.close()
except OSError: pass
PYEOF

  WS_TOPICS="$(I="$FIRST_ID" H="$HOST_ID" python3 -c "import json,os;print(json.dumps(['servers/'+os.environ['I']+'/metrics','hosts/'+os.environ['H']+'/metrics','hosts/'+os.environ['H']+'/capabilities','servers']))")"
  : > "$WS_LOG"
  echo "  opening WebSocket (subscribing: servers, servers/${FIRST_ID}/metrics, hosts/${HOST_ID}/metrics, hosts/${HOST_ID}/capabilities)"
  python3 "$WS_PY" "$PORT" 20 "$WS_TOPICS" >"$WS_LOG" 2>/tmp/kgsm-api-smoke-ws.err &
  WS_CLIENT_PID=$!; PIDS+=("$WS_CLIENT_PID")
  sleep 5                                              # metric ticks flow + capability state is operational
  echo "  killing stub monitor mid-stream (degrade: down flip + tick silence)"
  kill "$STUB_PID" 2>/dev/null; wait "$STUB_PID" 2>/dev/null
  sleep 7                                              # let the down flip emit + the outage silence settle
  echo "  restarting stub monitor (recover: operational flip + ticks resume)"
  SNAP_ID="$FIRST_ID" SNAP_CPU="$STUB_CPU" SNAP_MEM="$STUB_MEM" SNAP_IOREAD="$STUB_IO_READ" SNAP_PIDS="$STUB_PIDS" SNAP_DISK="$STUB_DISK" \
    python3 "$STUB_PY" "$STUB_SOCK" >>/tmp/kgsm-api-smoke-stub.log 2>&1 &
  STUB_PID=$!; PIDS+=("$STUB_PID")
  wait "$WS_CLIENT_PID" 2>/dev/null                    # client runs out its window through the recovery

  # 20. Per-server metric ticks arrive on servers/{id}/metrics carrying the stub values VERBATIM.
  if I="$FIRST_ID" CPU="$STUB_CPU" MEM="$STUB_MEM" LOG="$WS_LOG" python3 -c "
import json,os,sys
topic='servers/'+os.environ['I']+'/metrics'
ticks=[r['msg'] for r in (json.loads(l) for l in open(os.environ['LOG']) if l.strip()) if 'msg' in r
       and r['msg'].get('topic')==topic and r['msg'].get('type')=='metrics.tick']
if len(ticks)<2: sys.exit(1)
for m in ticks:
    d=m.get('data') or {}
    if not (abs(d.get('cpuPctCore',-1)-float(os.environ['CPU']))<1e-6
            and d.get('memBytes')==int(os.environ['MEM']) and d.get('ioWriteBps') is None): sys.exit(2)
sys.exit(0)
" 2>/dev/null; then
    ok "WS metrics.tick on servers/{id}/metrics (cpuPctCore>100 + null ioWrite carried verbatim, ≥2 ticks)"
  else bad "WS per-server metric ticks (see $WS_LOG)"; fi

  # 21. Host capacity ticks arrive on hosts/{id}/metrics with real (non-null) capacity.
  if H="$HOST_ID" LOG="$WS_LOG" python3 -c "
import json,os,sys
topic='hosts/'+os.environ['H']+'/metrics'
ticks=[r['msg'] for r in (json.loads(l) for l in open(os.environ['LOG']) if l.strip()) if 'msg' in r
       and r['msg'].get('topic')==topic and r['msg'].get('type')=='host.metrics']
if not ticks: sys.exit(1)
d=ticks[0].get('data') or {}
# The enriched HostMetricsDto rides the WS tick too (the shared MetricsMapping → byte-identical to REST):
sys.exit(0 if (d.get('cpuPct') is not None and d.get('mem') and d.get('disks')
               and d.get('perCore')==[10.0,15.0] and d.get('hostname')=='smoke-stub'
               and d.get('sampleTs')==1718400000000 and isinstance(d.get('interfaces'),list)) else 2)
" 2>/dev/null; then
    ok "WS host.metrics on hosts/{id}/metrics (capacity + M-diag telemetry present)"
  else bad "WS host metric ticks (see $WS_LOG)"; fi

  # 22. The servers topic carries STATUS/ROSTER only — NOT the 1s metric firehose. With status stable,
  #     ZERO server.patch must arrive even though many metric ticks did (the frozen §6 topic split).
  if LOG="$WS_LOG" python3 -c "
import json,os,sys
rows=[json.loads(l) for l in open(os.environ['LOG']) if l.strip()]
patches=[r for r in rows if 'msg' in r and r['msg'].get('topic')=='servers'
         and r['msg'].get('type') in ('server.patch','server.removed')]
ticks=[r for r in rows if 'msg' in r and r['msg'].get('type')=='metrics.tick']
sys.exit(0 if (len(ticks)>=2 and len(patches)==0) else 1)
" 2>/dev/null; then
    ok "WS servers topic quiet under the metric firehose (status/roster only, no metric double-stream)"
  else bad "WS servers topic emitted unexpectedly (see $WS_LOG)"; fi

  # 23. DEGRADE flip: after the monitor dies, a capabilities.patch reports metrics 'down' — and keeps
  #     provisioned:true. The capability is "temporarily unavailable, still there", never "lost".
  if H="$HOST_ID" LOG="$WS_LOG" python3 -c "
import json,os,sys
topic='hosts/'+os.environ['H']+'/capabilities'
downs=[((r['msg'].get('data') or {}).get('metrics') or {}) for r in
       (json.loads(l) for l in open(os.environ['LOG']) if l.strip())
       if 'msg' in r and r['msg'].get('topic')==topic and r['msg'].get('type')=='capabilities.patch'
       and ((r['msg'].get('data') or {}).get('metrics') or {}).get('status')=='down']
sys.exit(0 if (downs and all(m.get('provisioned') is True for m in downs)) else 1)
" 2>/dev/null; then
    ok "WS capabilities.patch metrics 'down' + provisioned:true (degrade — capability never 'lost')"
  else bad "WS degrade flip not observed / provisioned flipped (see $WS_LOG)"; fi

  # 24. ...and the metric ticks CEASE during the outage — silence, never a replayed stale frame. The
  #     window starts past the monitor's ~1s cache grace and ends before the restart, so it is fully
  #     within the dead period.
  if H="$HOST_ID" LOG="$WS_LOG" python3 -c "
import json,os,sys
rows=[json.loads(l) for l in open(os.environ['LOG']) if l.strip()]
topic='hosts/'+os.environ['H']+'/capabilities'
down_t=[r['t'] for r in rows if 'msg' in r and r['msg'].get('topic')==topic
        and ((r['msg'].get('data') or {}).get('metrics') or {}).get('status')=='down']
tick_t=[r['t'] for r in rows if 'msg' in r and r['msg'].get('type')=='metrics.tick']
if not down_t: sys.exit(1)
d=min(down_t); lo,hi=d+1.5,d+3.5
sys.exit(0 if not any(lo<=t<=hi for t in tick_t) else 2)
" 2>/dev/null; then
    ok "WS metric ticks fall silent during the outage (no stale replay)"
  else bad "WS metric ticks continued during outage (see $WS_LOG)"; fi

  # 25. RECOVER flip: after the monitor returns, a capabilities.patch flips metrics back to operational
  #     (provisioned:true on EVERY patch throughout), and the metric ticks resume past the recovery.
  if I="$FIRST_ID" H="$HOST_ID" LOG="$WS_LOG" python3 -c "
import json,os,sys
rows=[json.loads(l) for l in open(os.environ['LOG']) if l.strip()]
ctopic='hosts/'+os.environ['H']+'/capabilities'
caps=[(r['t'], (r['msg'].get('data') or {}).get('metrics') or {}) for r in rows if 'msg' in r
      and r['msg'].get('topic')==ctopic and r['msg'].get('type')=='capabilities.patch']
if any(m.get('provisioned') is not True for _,m in caps): sys.exit(3)   # never broadcast a lost capability
downs=[t for t,m in caps if m.get('status')=='down']
ups  =[t for t,m in caps if m.get('status')=='operational']
if not downs or not ups: sys.exit(1)
u=min(t for t in ups if t>min(downs)) if any(t>min(downs) for t in ups) else None
if u is None: sys.exit(2)                                              # operational must follow a down
mt='servers/'+os.environ['I']+'/metrics'
tick_t=[r['t'] for r in rows if 'msg' in r and r['msg'].get('topic')==mt and r['msg'].get('type')=='metrics.tick']
sys.exit(0 if any(t>u for t in tick_t) else 4)                         # ticks resume past the recovery
" 2>/dev/null; then
    ok "WS recovery: metrics flips back operational (provisioned:true throughout) + ticks resume"
  else bad "WS recovery flip / resume not observed (see $WS_LOG)"; fi

  # --- M3 commands: the write path (gate → 202 + job → jobs WS → verify) ------
  echo "==> M3 command checks — POST /servers/{id}/commands (the gate/rejection contract, no mutation)"

  # 26. Unknown server id -> OUR 404 envelope (the write path resolves the server like the read path).
  req POST /api/v1/servers/does-not-exist/commands -H 'Content-Type: application/json' -d '{"verb":"start"}'
  [[ "$CODE" == 404 ]] && grep -q '"code":"not_found"' <<<"$BODY" && ! grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY" \
    && ok "POST commands unknown server → 404 {error:{code:not_found}}" || bad "M3 unknown-server 404 (code=$CODE body=$BODY)"

  # 27. Unknown verb -> 400 bad_request. The verb set is closed and server-defined; the client (or model)
  #     cannot invent one. No job is created, nothing runs.
  req POST "/api/v1/servers/${FIRST_ID}/commands" -H 'Content-Type: application/json' -d '{"verb":"frobnicate"}'
  [[ "$CODE" == 400 ]] && grep -q '"code":"bad_request"' <<<"$BODY" && ! grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY" \
    && ok "POST commands unknown verb → 400 {error:{code:bad_request}}" || bad "M3 unknown-verb 400 (code=$CODE body=$BODY)"

  # 28. The admissibility gate: an obvious no-op against the REAL observed status -> 409 conflict, with
  #     NO mutation (the gate rejects before the verb ever runs). The no-op verb is chosen from the live
  #     status; 'unknown' status never blocks (we can't honestly call a transition a no-op), so skip it.
  req GET "/api/v1/servers/${FIRST_ID}"
  ST="$(python3 -c "import json;print(json.load(open('/tmp/kgsm-api-smoke.body')).get('status',''))" 2>/dev/null)"
  case "$ST" in
    running) NOOP_VERB=start ;;
    stopped) NOOP_VERB=stop  ;;
    *)       NOOP_VERB=""     ;;
  esac
  if [[ -n "$NOOP_VERB" ]]; then
    req POST "/api/v1/servers/${FIRST_ID}/commands" -H 'Content-Type: application/json' -d "{\"verb\":\"${NOOP_VERB}\"}"
    [[ "$CODE" == 409 ]] && grep -q '"code":"conflict"' <<<"$BODY" \
      && ok "gate: no-op '${NOOP_VERB}' on ${ST} server → 409 conflict (rejected pre-execution, no mutation)" \
      || bad "M3 no-op gate 409 (status=$ST verb=$NOOP_VERB code=$CODE body=$BODY)"
  else
    echo "  (skipping no-op gate check: server status '${ST:-unknown}' — the gate correctly does not block unknown)"
  fi

  # Coverage note (honest, mirroring M2's server.patch boundary): the 202 + job + WS job.patch + verify
  # server.patch + the in-flight 409 guard are exercised by code review + a LIVE mutation on a trusted
  # host — the verb must actually run, which mutates real state and is not deterministic in this
  # stub-driven smoke. The gate and all three error envelopes above ARE proven here, without mutation.
  echo "  (note: 202/job lifecycle + WS job.patch + verify are code-path-only in smoke — live-validated on a trusted host)"

  # --- M5 audit: the read surface (the append path is xUnit + a trusted-host live-validate) ---
  echo "==> M5 audit checks — GET /audit (the keyset read contract; empty here — no events fire in smoke)"

  # 29. GET /audit -> 200 + the { data, nextCursor } page shape. Empty + nextCursor:null on a fresh DB
  #     (no kgsm lifecycle events fire in smoke, auth is disabled so no login) — proves the endpoint, the
  #     page envelope, and that EnsureCreated landed the audit table (a missing table would 500 here).
  req GET /api/v1/audit
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if (isinstance(d.get('data'),list) and len(d['data'])==0 and d.get('nextCursor') is None) else 1)
" 2>/dev/null; then
    ok "/audit 200 + { data:[], nextCursor:null } (empty page; table created via EnsureCreated)"
  else bad "M5 /audit empty page (code=$CODE body=$BODY)"; fi

  # 30. The filters + limit are accepted (still empty here) — proves the keyset query parameters bind and
  #     the page shape holds with filters applied (1:1 to the indexed columns).
  req GET "/api/v1/audit?limit=5&severity=info&serverId=mc&actor=haru"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if ('data' in d and 'nextCursor' in d) else 1)
" 2>/dev/null; then
    ok "/audit accepts cursor/limit/severity/serverId/actor filters (page shape holds)"
  else bad "M5 /audit filters (code=$CODE body=$BODY)"; fi

  echo "  (note: the event-sourced append + the audit.append WS topic are proven in tests/Api.Tests; the"
  echo "   live kgsm-event → audit row path is a trusted-host live-validate, like M3's mutation happy path)"

  # --- M6·b ports: the network surface (firewall ABSENT here → the honest degrade path) -------
  echo "==> M6·b ports checks — network block degrade (no firewall configured in smoke)"

  # 31. open_ports is an ADMITTED verb now (the closed set grew): an unknown server resolves first → 404,
  #     NOT a 400 unknown-verb. That distinction is the proof the verb is in the closed set.
  req POST /api/v1/servers/does-not-exist/commands -H 'Content-Type: application/json' -d '{"verb":"open_ports"}'
  [[ "$CODE" == 404 ]] && grep -q '"code":"not_found"' <<<"$BODY" && ! grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY" \
    && ok "open_ports admitted (unknown server → 404, not a 400 unknown-verb)" \
    || bad "M6·b open_ports verb (code=$CODE body=$BODY)"

  # 32. The server DETAIL view carries the `network` block; with no firewall configured it degrades
  #     honestly — firewall:"absent", reachable:null (RESERVED — no upstream prober), and EVERY required
  #     row open:null (never a fabricated false). `required` is domain truth (Instance.Ports), present regardless.
  req GET "/api/v1/servers/${FIRST_ID}"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
n=json.load(open('/tmp/kgsm-api-smoke.body')).get('network')
sys.exit(0 if (n is not None and n['firewall']=='absent' and n['reachable'] is None
               and isinstance(n['required'],list) and all(r['open'] is None for r in n['required'])) else 1)
" 2>/dev/null; then
    ok "/servers/{id} network: firewall:absent + reachable:null + every open:null (honest, never fabricated)"
  else bad "M6·b server network degrade (code=$CODE body=$BODY)"; fi

  # 33. detail ≠ list: the `network` block is detail-ONLY — the /servers list element omits it, so the list
  #     and the `servers` stream stay byte-identical to the frozen M1·b shape (no per-poll firewall probe).
  req GET /api/v1/servers
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if (d and 'network' not in d[0]) else 1)
" 2>/dev/null; then
    ok "/servers list OMITS network (detail-only; list/stream keep the M1·b shape)"
  else bad "M6·b list omits network (code=$CODE body=$BODY)"; fi

  # 34. The host DETAIL grid is null (the key is omitted) when the firewall is absent — honest "not
  #     measurable", never [] nor a fabricated grid. (An Ok-but-empty firewall yields openPorts:[] —
  #     the Unknown≠empty distinction is covered in tests/Api.Tests.)
  req GET "/api/v1/hosts/${HOST_ID}"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
sys.exit(0 if 'network' not in json.load(open('/tmp/kgsm-api-smoke.body')) else 1)
" 2>/dev/null; then
    ok "/hosts/{id} omits the openPorts grid when firewall absent (honest null, omitted)"
  else bad "M6·b host network omitted (code=$CODE body=$BODY)"; fi

  echo "  (note: the OPERATIONAL firewall path — open/closed verdicts, the open_ports apply + the direct"
  echo "   network.ports.open audit + the servers/{id}/network verify patch — is a trusted-host live-validate,"
  echo "   needing the kgsm-firewall daemon + kgsm-group socket access, like M3's mutation happy path)"

  # --- M9 metrics history: the durable tiered store + read endpoint --------
  echo "==> M9 metrics history checks — GET /{servers,hosts}/{id}/metrics/history + durability"

  # Server history unknown id → 404
  req GET /api/v1/servers/nonexistent/metrics/history
  if [[ "$CODE" == 404 ]]; then ok "server history unknown id → 404"
  else bad "M9 server history 404 (code=$CODE)"; fi

  # Host history unknown id → 404
  req GET /api/v1/hosts/nonexistent/metrics/history
  if [[ "$CODE" == 404 ]]; then ok "host history unknown id → 404"
  else bad "M9 host history 404 (code=$CODE)"; fi

  # Durability: the sampler is running with the stub monitor (KGSM_API_METRICS_PERSIST_MS=5000).
  # Wait > one persist interval, then assert rows landed. The stub monitor serves a host snapshot
  # + a per-server row keyed to 'factorio' → both host AND server history should be non-empty.
  echo "  waiting ~8s for ≥1 metrics persist interval (sampler → metrics.db)…"
  sleep 8

  # Host history: the sampler should have persisted host metrics.
  req GET "/api/v1/hosts/$HOST_ID/metrics/history?range=1h"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
series=d.get('series',{})
has_data=any(len(v)>0 for v in series.values())
sys.exit(0 if d.get('kind')=='host' and d.get('tier') in ('raw','rollup') and has_data else 1)
" 2>/dev/null; then
    ok "host history 200 + NON-EMPTY series (sampler → metrics.db durable write proven)"
  else bad "M9 host history durability (code=$CODE body=$(cat /tmp/kgsm-api-smoke.body 2>/dev/null | head -c 300))"; fi

  # Server history: the sampler should have persisted per-server metrics for 'factorio'
  req GET "/api/v1/servers/factorio/metrics/history?range=1h"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
series=d.get('series',{})
has_data=any(len(v)>0 for v in series.values())
sys.exit(0 if d.get('kind')=='server' and d.get('tier') in ('raw','rollup') and has_data else 1)
" 2>/dev/null; then
    ok "server history 200 + NON-EMPTY series (per-server sampler → metrics.db durable write proven)"
  else bad "M9 server history durability (code=$CODE body=$(cat /tmp/kgsm-api-smoke.body 2>/dev/null | head -c 300))"; fi

  # Durability across restart: stop the API, start it again (same metrics.db), assert data persists
  stop_api
  start_api "$STUB_SOCK" || { echo "API restart failed (Phase B M9 restart); log:"; tail -20 /tmp/kgsm-api-smoke.log; exit 2; }
  wait_caps_warm

  req GET "/api/v1/hosts/$HOST_ID/metrics/history?range=1h"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
series=d.get('series',{})
has_data=any(len(v)>0 for v in series.values())
sys.exit(0 if has_data else 1)
" 2>/dev/null; then
    ok "host history survives API restart (D1 durability — metrics.db is the system of record)"
  else bad "M9 durability after restart (code=$CODE body=$(cat /tmp/kgsm-api-smoke.body 2>/dev/null | head -c 300))"; fi

  # --- M6·a alerts: the condition-mirror read surface (watchdog ABSENT here → empty feed) ------
  echo "==> M6·a alerts checks — GET /alerts (the condition-mirror read; empty here — no watchdog in smoke)"

  # 35. GET /alerts (default firing) -> 200 + { data:[] }. No watchdog is provisioned in smoke, so the
  #     engine serves an EMPTY feed (degrade gracefully — never a 500). Proves the endpoint + the envelope.
  req GET /api/v1/alerts
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if (isinstance(d.get('data'),list) and len(d['data'])==0) else 1)
" 2>/dev/null; then
    ok "/alerts 200 + { data:[] } (empty feed — no crash source provisioned; never a 500)"
  else bad "M6·a /alerts empty feed (code=$CODE body=$BODY)"; fi

  # 36. The resolved rear-view + the since window bind (still empty here) — proves status=resolved & since
  #     parse and the page shape holds (the firing/resolved split + the 24h rear-view).
  req GET "/api/v1/alerts?status=resolved&since=24h"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if (isinstance(d.get('data'),list) and len(d['data'])==0) else 1)
" 2>/dev/null; then
    ok "/alerts?status=resolved&since=24h 200 + { data:[] } (rear-view + since window bind)"
  else bad "M6·a /alerts resolved window (code=$CODE body=$BODY)"; fi

  echo "  (note: the crash raise/escalate/probation-resolve/retract lifecycle + the alert↔audit bridge are"
  echo "   proven in tests/Api.Tests/AlertEngineTests; the live watchdog-crash → alert path is a trusted-host"
  echo "   live-validate, needing kgsm-watchdog up + a forced crash, like M3's mutation happy path)"

  # --- #8 console scrollback: GET /servers/{id}/console?tail=N (the REST hydrate half) --------
  echo "==> #8 console checks — GET /servers/{id}/console?tail=N (scrollback REST; watchdog ABSENT here)"

  # 36·c. No watchdog is provisioned in smoke (KGSM_API_WATCHDOG_SOCKET unset) → no console source → the
  #       endpoint degrades to { lines: [] } (degrade gracefully, NEVER a 500). Proves the frozen shape +
  #       the absent-watchdog degrade. ?tail= is accepted (clamped 0..5000); the value is inert with no source.
  req GET "/api/v1/servers/${FIRST_ID}/console?tail=50"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if (isinstance(d.get('lines'),list) and len(d['lines'])==0) else 1)
" 2>/dev/null; then
    ok "/servers/{id}/console?tail=50 200 + { lines: [] } (watchdog absent → honest empty; never a 500)"
  else bad "#8 console scrollback degrade (code=$CODE body=$BODY)"; fi

  # 36·d. The default (no ?tail=) is the same honest empty here — proves the param is optional + the shape holds.
  req GET "/api/v1/servers/${FIRST_ID}/console"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if (isinstance(d.get('lines'),list) and len(d['lines'])==0) else 1)
" 2>/dev/null; then
    ok "/servers/{id}/console (no ?tail=, default 200) 200 + { lines: [] }"
  else bad "#8 console scrollback default (code=$CODE body=$BODY)"; fi

  echo "  (note: the live WS follow on servers/{id}/console — opening a shared watchdog tail-bridge per native"
  echo "   instance and streaming console.line frames with monotonic seq — is OWED-TO-HUMAN: it needs a real"
  echo "   running kgsm-watchdog + a native instance producing stdout. The bridge open/close/unique-seq/"
  echo "   not-capable-no-retry logic is proven in tests/Api.Tests/ConsoleBridgeTests; the controller happy/"
  echo "   absent/down paths in tests/Api.Tests/ConsoleControllerTests.)"

  # --- File browser (Tier 3 #12): GET/PUT /servers/{id}/files… (real working dir) ---------------
  echo "==> file browser checks — GET/PUT /servers/{id}/files (list/read/jail/save; real working dir)"

  # 1. LIST the instance root → 200 + entries[], dirs sorted before files (deterministic truncation order).
  req GET "/api/v1/servers/${FIRST_ID}/files"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
es=d.get('entries'); assert isinstance(es,list) and d.get('path')==''
seen_file=False
for e in es:
    if e['kind']!='dir': seen_file=True
    elif seen_file: sys.exit(1)   # a dir AFTER a file ⇒ ordering broken
sys.exit(0)
" 2>/dev/null; then
    ok "/servers/{id}/files 200 + entries[] (dirs-first ordering)"
  else bad "file list (code=$CODE body=$BODY)"; fi

  # 2. READ the instance config (kgsm layout: <id>.config.ini) → 200 + raw UTF-8 + sha256 etag. Stash the
  #    content+etag so the save round-trip can rewrite IDENTICAL bytes (non-destructive).
  CFG="${FIRST_ID}.config.ini"
  req GET "/api/v1/servers/${FIRST_ID}/files/content?path=${CFG}"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
ok = isinstance(d.get('content'),str) and str(d.get('etag','')).startswith('sha256:') and d.get('encoding')=='utf-8'
if ok: open('/tmp/kgsm-api-smoke-fb.json','w').write(json.dumps({'content':d['content'],'etag':d['etag'],'origin':'ui'}))
sys.exit(0 if ok else 1)
" 2>/dev/null; then
    ok "/servers/{id}/files/content 200 + raw text + sha256 etag"
  else bad "file read (code=$CODE body=$BODY)"; fi

  # 3. Jail: a traversal escape is refused (404 — never leaks the host fs).
  req GET "/api/v1/servers/${FIRST_ID}/files/content?path=../../../../etc/passwd"
  [[ "$CODE" == 404 ]] && ok "file read traversal escape → 404 (jail holds)" || bad "file traversal (code=$CODE)"

  # 4. SAVE-BACK identical bytes with the etag → 200 + sha256 etag (the full PUT path, NON-DESTRUCTIVE —
  #    same content ⇒ same sha256). Proves the atomic write + audit (file.write) end-to-end.
  if [[ -f /tmp/kgsm-api-smoke-fb.json ]]; then
    req PUT "/api/v1/servers/${FIRST_ID}/files/content?path=${CFG}" -H 'Content-Type: application/json' -d @/tmp/kgsm-api-smoke-fb.json
    if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if str(d.get('etag','')).startswith('sha256:') else 1)
" 2>/dev/null; then
      ok "/servers/{id}/files/content PUT identical bytes → 200 + sha256 etag (save path, non-destructive)"
    else bad "file save round-trip (code=$CODE body=$BODY)"; fi
  else echo "  (skip save round-trip — config read did not yield a body to echo back)"; fi

  # 5. Stale-etag PUT → 412 (optimistic concurrency).
  req PUT "/api/v1/servers/${FIRST_ID}/files/content?path=${CFG}" -H 'Content-Type: application/json' -d '{"content":"x","etag":"sha256:deadbeef"}'
  [[ "$CODE" == 412 ]] && ok "file save stale etag → 412 (optimistic concurrency)" || bad "file save 412 (code=$CODE body=$BODY)"

  # 6. Save to a non-existent file → 404 (v1 = save-existing only, no create).
  req PUT "/api/v1/servers/${FIRST_ID}/files/content?path=temp/does-not-exist-smoke.txt" -H 'Content-Type: application/json' -d '{"content":"x"}'
  [[ "$CODE" == 404 ]] && ok "file save non-existent → 404 (save-existing only, no create)" || bad "file save 404 (code=$CODE body=$BODY)"

  # 7. The file.write audit row landed with path/size/sha256 — and NEVER the content (secret hygiene).
  req GET "/api/v1/audit?serverId=${FIRST_ID}"
  if python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
rows=[r for r in d.get('data',[]) if r.get('action')=='file.write']
assert rows, 'no file.write row'
m=rows[0].get('meta') or {}
sys.exit(0 if ('path' in m and 'sizeBytes' in m and str(m.get('sha256','')).startswith('sha256:') and 'content' not in m) else 1)
" 2>/dev/null; then
    ok "audit: file.write row carries path/size/sha256 — NEVER the content (secret hygiene)"
  else bad "file.write audit row (code=$CODE body=$BODY)"; fi

  # --- M7 assistant turn relay: the gates that run before any upstream call ------
  echo "==> M7 assistant relay checks — POST /api/v1/assistant/turn (auth + capability gates, no upstream)"

  # The smoke instance configures NO assistant (KGSM_API_ASSISTANT_URL unset) -> capability absent, so the
  # relay degrades to an honest 404 BEFORE any upstream call (degrade-gracefully, never a 500).
  req POST /api/v1/assistant/turn -H 'Content-Type: application/json' -d '{"prompt":"hi"}'
  [[ "$CODE" == 404 ]] && grep -q '"code":"not_found"' <<<"$BODY" && ! grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY" \
    && ok "POST assistant/turn, assistant absent → 404 {error:{code:not_found}} (capability gate)" \
    || bad "M7 assistant-absent 404 (code=$CODE body=$BODY)"

  # Prompt validation precedes the capability gate -> a blank prompt is a 400 envelope.
  req POST /api/v1/assistant/turn -H 'Content-Type: application/json' -d '{"prompt":"   "}'
  [[ "$CODE" == 400 ]] && grep -q '"code":"bad_request"' <<<"$BODY" \
    && ok "POST assistant/turn blank prompt → 400 {error:{code:bad_request}}" \
    || bad "M7 blank-prompt 400 (code=$CODE body=$BODY)"

  # The reverse-path read endpoints share the same capability gate: assistant absent → honest 404 envelope.
  req GET /api/v1/assistant/conversations
  [[ "$CODE" == 404 ]] && grep -q '"code":"not_found"' <<<"$BODY" \
    && ok "GET assistant/conversations, assistant absent → 404 {error:{code:not_found}} (capability gate)" \
    || bad "reverse-path conversations-absent 404 (code=$CODE body=$BODY)"
  req GET /api/v1/assistant/conversations/chatA
  [[ "$CODE" == 404 ]] && grep -q '"code":"not_found"' <<<"$BODY" \
    && ok "GET assistant/conversations/{id}, assistant absent → 404 {error:{code:not_found}} (capability gate)" \
    || bad "reverse-path transcript-absent 404 (code=$CODE body=$BODY)"

  echo "  (note: the FULL relay path — identity + secret forwarding + byte-faithful streaming — is proven by the"
  echo "   dedicated stub-assistant phase below; only a real-model (Ollama) end-to-end remains a live nicety)"

  # --- M8·b install / uninstall: the create/delete write path (gate only — NO mutation) ------
  echo "==> M8·b install/uninstall checks — POST /servers + DELETE /servers/{id} (gate/rejection, no mutation)"

  # 36. A missing blueprint is rejected before anything runs -> 400 bad_request. (A VALID blueprint would
  #     really install under AUTH_DISABLED, so smoke only ever sends the rejection cases — exactly like M3.)
  req POST /api/v1/servers -H 'Content-Type: application/json' -d '{}'
  [[ "$CODE" == 400 ]] && grep -q '"code":"bad_request"' <<<"$BODY" && ! grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY" \
    && ok "POST /servers no blueprint → 400 {error:{code:bad_request}}" \
    || bad "M8·b install no-blueprint 400 (code=$CODE body=$BODY)"

  # 37. An unknown blueprint: the backend assigns the id via kgsm generate-id, which validates the blueprint
  #     and fails -> 400 (nothing can be installed). The name is bogus, so kgsm creates NOTHING.
  req POST /api/v1/servers -H 'Content-Type: application/json' -d '{"blueprint":"zzzz-not-a-real-blueprint"}'
  [[ "$CODE" == 400 ]] && grep -q '"code":"bad_request"' <<<"$BODY" && ! grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY" \
    && ok "POST /servers unknown blueprint → 400 (generate-id rejects, nothing created)" \
    || bad "M8·b install unknown-blueprint 400 (code=$CODE body=$BODY)"

  # 38. Uninstall an unknown server -> OUR 404 envelope (the roster is the authority, like the command path).
  req DELETE /api/v1/servers/does-not-exist
  [[ "$CODE" == 404 ]] && grep -q '"code":"not_found"' <<<"$BODY" && ! grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY" \
    && ok "DELETE /servers/{unknown} → 404 {error:{code:not_found}}" \
    || bad "M8·b uninstall unknown-server 404 (code=$CODE body=$BODY)"

  echo "  (note: the 202 + install/uninstall job lifecycle + verify server.patch/server.removed + the"
  echo "   server.install/uninstall audit echo stay out of smoke — a real install mutates the host, so the"
  echo "   mutation happy path was live-validated separately on the trusted host (2026-06-19), like M3's)"

  # --- M8 /me: the identity surface (projects the bearer claims; here the AUTH_DISABLED synthetic admin) ---
  echo "==> M8 /me checks — GET /api/v1/me (identity + tier + scopes projected from the bearer)"

  # 39. Under AUTH_DISABLED the synthetic admin IS the caller -> 200 with its identity + tier:admin + scopes.
  #     (The tier matrix + the none-tier/no-token cases are proven in tests/Api.Tests; the no-bearer 401 is
  #     in the auth-enabled sweep below. Here we prove the wire shape: camelCase {user,tier,scopes}.)
  req GET /api/v1/me
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
u=d.get('user',{})
sys.exit(0 if (d.get('tier')=='admin' and u.get('id')=='discord:dev'
               and u.get('username')=='dev' and isinstance(d.get('scopes'),list)
               and 'identify' in d['scopes']) else 1)
" 2>/dev/null; then
    ok "/me 200 + {user:{id:discord:dev,...}, tier:admin, scopes:[…]} (projects the bearer claims)"
  else bad "/me shape (code=$CODE body=$BODY)"; fi

  # --- M8·c integrations: outbound-notification config (admin; here the AUTH_DISABLED synthetic admin) ---
  echo "==> M8·c integrations checks — /integrations (discord + slack; config + masked secret; NO real post)"

  # 40. GET /integrations lists BOTH providers (the Increment C abstraction is wired — discord + slack),
  #     each unconfigured initially.
  req GET /api/v1/integrations
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
by={x.get('provider'): x for x in d}
sys.exit(0 if ('discord' in by and 'slack' in by
               and all(by[p].get('configured') is False and by[p].get('enabled') is False for p in ('discord','slack'))) else 1)
" 2>/dev/null; then
    ok "/integrations 200 + discord AND slack present, unconfigured (the abstraction is wired)"
  else bad "/integrations list shape (code=$CODE body=$BODY)"; fi

  # 41. GET /integrations/discord -> the §3·e record: webhook unconfigured, bot:null (one-way only),
  #     catalog lists only deliverable events (online/crash present; resource/join honestly omitted).
  req GET /api/v1/integrations/discord
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
ids={e['id'] for e in d.get('events',[])}
sys.exit(0 if (d.get('webhook',{}).get('configured') is False and d.get('bot') is None
               and 'online' in ids and 'crash' in ids
               and 'resource' not in ids and 'join' not in ids) else 1)
" 2>/dev/null; then
    ok "/integrations/discord 200 + bot:null + honest catalog (resource/join omitted)"
  else bad "/integrations/discord shape (code=$CODE body=$BODY)"; fi

  # 42. POST /test with nothing configured -> 409 not_configured (honest; no real Discord call, no faked ok).
  req POST /api/v1/integrations/discord/test
  [[ "$CODE" == 409 ]] && grep -q '"code":"not_configured"' <<<"$BODY" \
    && ok "POST /integrations/discord/test unconfigured → 409 not_configured (honest, no faked send)" \
    || bad "M8·c test unconfigured 409 (code=$CODE body=$BODY)"

  # 43. PATCH a (fake) webhook + label + a sparse event change -> 200; the raw secret is NEVER echoed
  #     and the masked hint is returned. (A fake webhook URL: we never call /test after this, so no post.)
  req PATCH /api/v1/integrations/discord -H 'Content-Type: application/json' \
    -d '{"webhook":"https://discord.com/api/webhooks/111222333/SMOKEfaketoken","channelLabel":"#smoke-ops","events":[{"id":"backup","enabled":false}]}'
  PATCH_CODE=$CODE; PATCH_BODY=$BODY
  req GET /api/v1/integrations/discord
  if [[ "$PATCH_CODE" == 200 ]] && [[ "$CODE" == 200 ]] \
     && ! grep -q 'SMOKEfaketoken' <<<"$PATCH_BODY" && ! grep -q 'SMOKEfaketoken' <<<"$BODY" \
     && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
wh=d.get('webhook',{})
backup=[e for e in d.get('events',[]) if e['id']=='backup']
sys.exit(0 if (wh.get('configured') is True and wh.get('hint','').startswith('…/webhooks/111222333/')
               and d.get('channelLabel')=='#smoke-ops'
               and len(backup)==1 and backup[0].get('enabled') is False) else 1)
" 2>/dev/null; then
    ok "PATCH /integrations/discord persists + masks the secret (hint only, raw never echoed)"
  else bad "M8·c PATCH round-trip (patch=$PATCH_CODE get=$CODE body=$BODY)"; fi

  # 44. GET /integrations/slack (Increment C, the second provider) -> the webhook-only record: NO `bot`
  #     block (Slack has no Discord-style control bot — honest), same masked-webhook + honest catalog shape.
  req GET /api/v1/integrations/slack
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
ids={e['id'] for e in d.get('events',[])}
sys.exit(0 if (d.get('provider')=='slack' and d.get('webhook',{}).get('configured') is False and 'bot' not in d
               and 'online' in ids and 'crash' in ids and 'resource' not in ids and 'join' not in ids) else 1)
" 2>/dev/null; then
    ok "/integrations/slack 200 + no bot field + honest catalog (webhook-family abstraction validated)"
  else bad "/integrations/slack shape (code=$CODE body=$BODY)"; fi

  # 45. PATCH a (fake) Slack webhook -> 200; the raw secret is NEVER echoed, the masked hint is returned.
  req PATCH /api/v1/integrations/slack -H 'Content-Type: application/json' \
    -d '{"webhook":"https://hooks.slack.com/services/T0SMOKE/B0SMOKE/SMOKEfakeslacktoken","channelLabel":"#smoke-ops"}'
  PATCH_CODE=$CODE; PATCH_BODY=$BODY
  req GET /api/v1/integrations/slack
  if [[ "$PATCH_CODE" == 200 ]] && [[ "$CODE" == 200 ]] \
     && ! grep -q 'SMOKEfakeslacktoken' <<<"$PATCH_BODY" && ! grep -q 'SMOKEfakeslacktoken' <<<"$BODY" \
     && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
wh=d.get('webhook',{})
sys.exit(0 if (wh.get('configured') is True and wh.get('hint','').startswith('…/services/T0SMOKE/B0SMOKE/')
               and d.get('channelLabel')=='#smoke-ops') else 1)
" 2>/dev/null; then
    ok "PATCH /integrations/slack persists + masks the secret (hint only, raw never echoed)"
  else bad "M8·c slack PATCH round-trip (patch=$PATCH_CODE get=$CODE body=$BODY)"; fi

  stop_api
fi

# --- M7 stub assistant relay: the FULL relay path (a fresh API pointed at a stub assistant) ---------
# The gate-only checks above never reach the relay machinery (assistant absent). Here a stub assistant
# GATES on the relay secret (wrong/absent secret -> 401, which the API maps to 502), so the API reaching
# 200 PROVES it forwarded the correct X-Relay-Secret; the stub echoes X-Relay-User back in the stream,
# proving identity forwarding; and the canned §5·a frames coming through verbatim prove the byte relay.
echo "==> M7 assistant relay — stub assistant (forwards X-Relay-Secret + X-Relay-User; streams §5·a frames verbatim)"
cat > "$STUB_ASSIST_PY" <<'PYEOF'
import os, sys
from http.server import BaseHTTPRequestHandler, HTTPServer

SECRET = os.environ.get("REL_SECRET", "")

class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"  # connection-close delimits the finite SSE body for the client
    def log_message(self, *a): pass

    def do_GET(self):
        if self.path == "/health":
            self.send_response(200); self.send_header("Content-Type", "application/json"); self.end_headers()
            self.wfile.write(b'{"status":"ok"}')
            return
        # The reverse-path read endpoints (conversation history). Gate on the relay secret exactly like
        # /turn — a wrong/absent secret is 401 — and echo the forwarded user so the smoke can assert the
        # API forwards X-Relay-Secret + X-Relay-User on a READ, and relays the JSON body verbatim.
        if self.path == "/conversations" or self.path.startswith("/conversations/"):
            if self.headers.get("X-Relay-Secret") != SECRET:
                self.send_response(401); self.end_headers(); return
            user = self.headers.get("X-Relay-User", "")
            self.send_response(200); self.send_header("Content-Type", "application/json"); self.end_headers()
            if self.path == "/conversations":
                # A canned summary list, tagged with the forwarded user so the smoke sees the scope.
                self.wfile.write(('[{"id":"chatA","title":"%s asked about factorio","createdAt":"2026-06-26T10:00:00Z","lastActivityAt":"2026-06-26T10:05:00Z","turnCount":2}]' % user).encode())
            else:
                cid = self.path[len("/conversations/"):]
                # A canned transcript echoing the requested chat id + forwarded user, §5·a-shaped turn.
                self.wfile.write(('{"id":"%s","entries":[{"kind":"turn","createdAt":"2026-06-26T10:00:00Z","turn":{"prompt":"hi from %s","final":"hello","think":false,"thinking":null,"tools":[],"usage":null,"outcome":"ok"}}]}' % (cid, user)).encode())
            return
        self.send_response(404); self.end_headers()

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0) or 0)
        if n: self.rfile.read(n)
        if self.path != "/turn":
            self.send_response(404); self.end_headers(); return
        # Gate exactly like the real assistant's relay path: a wrong/absent secret is 401.
        if self.headers.get("X-Relay-Secret") != SECRET:
            self.send_response(401); self.end_headers(); return
        user = self.headers.get("X-Relay-User", "")
        # Echo BOTH authority decisions so the smoke can assert the split: canAct (PROPOSE, operator+
        # tier, toggle-INDEPENDENT) vs autoAct (AUTO-RUN, admin tier ∧ the per-turn actions toggle).
        canact = self.headers.get("X-Relay-Can-Act", "")
        autoact = self.headers.get("X-Relay-Auto-Act", "")
        # Echo the per-chat conversation id so the smoke can assert the API forwards it (the fresh-
        # context-window plumbing: body.conversationId -> sanitise -> X-Relay-Conversation-Id).
        conv = self.headers.get("X-Relay-Conversation-Id", "")
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        self.wfile.write(b'event: text.delta\ndata: {"type":"text.delta","text":"relay ok"}\n\n')
        # A Phase-2 card-bearing tool.result: the `result` card is an "unknown field" to the relay,
        # so it proves the byte-copy relay (CopyToAsync) passes a structured card through untouched.
        self.wfile.write(b'event: tool.result\ndata: {"type":"tool.result","id":"tc_0","tool":"run_health_check","summary":"factorio: passed with warnings.","result":{"tool":"run_health_check","confidence":"confirmed","subject":{"resource":"server","id":"factorio"},"data":{"overall":"warn","checks":[{"name":"updates","state":"warn","severity":"update","detail":"Update available."}],"passed":1,"total":2,"skipped":0}}}\n\n')
        self.wfile.write(('event: done\ndata: {"type":"done","relayUser":"%s","canAct":"%s","autoAct":"%s","conv":"%s"}\n\n' % (user, canact, autoact, conv)).encode())

HTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
PYEOF
REL_SECRET="$REL_SECRET" python3 "$STUB_ASSIST_PY" "$ASSIST_PORT" >/tmp/kgsm-api-smoke-stub-assistant.log 2>&1 &
ASSIST_PID=$!; PIDS+=("$ASSIST_PID")
for _ in $(seq 1 40); do curl -fsS "${ASSIST_URL}/health" >/dev/null 2>&1 && break; sleep 0.1; done

if start_api_assistant "$ASSIST_URL" "$REL_SECRET"; then
  if wait_assistant_operational; then
    req POST /api/v1/assistant/turn -H 'Content-Type: application/json' -d '{"prompt":"hi"}'
    if [[ "$CODE" == 200 ]] \
       && grep -q 'event: text.delta' <<<"$BODY" \
       && grep -q 'event: done' <<<"$BODY" \
       && grep -q '"relayUser":"dev"' <<<"$BODY" \
       && grep -q 'event: tool.result' <<<"$BODY" \
       && grep -q '"result":{' <<<"$BODY" \
       && grep -q '"overall":"warn"' <<<"$BODY" \
       && grep -q '"confidence":"confirmed"' <<<"$BODY"; then
      ok "relay 200 SSE: stub gated on the secret (200 ⇒ correct X-Relay-Secret forwarded), X-Relay-User=dev echoed, §5·a frames verbatim INCL. a Phase-2 tool.result card (result/overall/confidence survive the byte relay)"
    else
      bad "M7 stub relay (code=$CODE body=$BODY; stub log: $(cat /tmp/kgsm-api-smoke-stub-assistant.log 2>/dev/null))"
    fi

    # Action authority is TWO axes (folded server-side from the caller's verified tier ∧ the toggle):
    #   canAct  = may PROPOSE — operator+ tier, toggle-INDEPENDENT (proposing is a tier capability).
    #   autoAct = may AUTO-RUN without confirmation — admin tier ∧ the per-turn `actions` toggle.
    # No actions flag: an admin (auth-disabled synthetic admin) can still PROPOSE (canAct=true), but
    # auto-run is OFF (autoAct=false) — the toggle gates auto-run, not proposing.
    req POST /api/v1/assistant/turn -H 'Content-Type: application/json' -d '{"prompt":"hi"}'
    { grep -q '"canAct":"true"' <<<"$BODY" && grep -q '"autoAct":"false"' <<<"$BODY"; } \
      && ok "relay action authority: actions omitted → canAct=true (operator+ may propose), autoAct=false (toggle gates auto-run)" \
      || bad "M7 authority/no-toggle (expected canAct=true+autoAct=false; body=$BODY)"
    # actions:true + admin tier ⇒ both: canAct=true AND autoAct=true (auto-run unlocked).
    req POST /api/v1/assistant/turn -H 'Content-Type: application/json' -d '{"prompt":"hi","actions":true}'
    { grep -q '"canAct":"true"' <<<"$BODY" && grep -q '"autoAct":"true"' <<<"$BODY"; } \
      && ok "relay action authority: actions:true + admin tier → canAct=true AND autoAct=true (toggle ∧ tier folded server-side)" \
      || bad "M7 authority/toggle-on (expected canAct=true+autoAct=true; body=$BODY)"
    # Per-chat conversation id: body.conversationId → sanitised → X-Relay-Conversation-Id, so the
    # assistant scopes memory web:<userId>:<chatId> (each "new chat" = a fresh context window). The "."
    # is stripped by the [A-Za-z0-9_-] sanitiser, proving the bound is applied (chat.7 → chat7).
    req POST /api/v1/assistant/turn -H 'Content-Type: application/json' -d '{"prompt":"hi","conversationId":"chat.7"}'
    grep -q '"conv":"chat7"' <<<"$BODY" \
      && ok "relay conversation scope: body.conversationId → X-Relay-Conversation-Id forwarded + sanitised (chat.7→chat7; per-chat fresh context)" \
      || bad "M7 conversationId forward (expected conv=chat7; body=$BODY)"
    # Omitted conversationId ⇒ no header ⇒ the assistant keeps the bare per-user key (back-compat).
    req POST /api/v1/assistant/turn -H 'Content-Type: application/json' -d '{"prompt":"hi"}'
    grep -q '"conv":""' <<<"$BODY" \
      && ok "relay conversation scope: omitted conversationId → no X-Relay-Conversation-Id (bare per-user key, back-compat)" \
      || bad "M7 conversationId omitted (expected empty conv; body=$BODY)"

    # Reverse path: list the caller's own past chats. The API forwards X-Relay-Secret + X-Relay-User on a
    # GET and relays the assistant's summary JSON verbatim (the stub echoes the forwarded user into the
    # title → proves the identity reached the leaf).
    req GET /api/v1/assistant/conversations
    { [[ "$CODE" == 200 ]] && grep -q '"id":"chatA"' <<<"$BODY" && grep -q '"dev asked about factorio"' <<<"$BODY"; } \
      && ok "reverse path: GET /assistant/conversations → 200, summary list relayed verbatim (X-Relay-User=dev forwarded on the read)" \
      || bad "conversations list (code=$CODE body=$BODY)"

    # Reverse path: load one chat's transcript. The {id} is forwarded in the path; the stub echoes it +
    # the user back, proving the per-chat fetch reaches the leaf scoped to the verified caller.
    req GET /api/v1/assistant/conversations/chatA
    { [[ "$CODE" == 200 ]] && grep -q '"id":"chatA"' <<<"$BODY" && grep -q '"prompt":"hi from dev"' <<<"$BODY" && grep -q '"kind":"turn"' <<<"$BODY"; } \
      && ok "reverse path: GET /assistant/conversations/{id} → 200, transcript relayed verbatim (per-chat fetch scoped to verified caller)" \
      || bad "conversation transcript (code=$CODE body=$BODY)"
  else
    bad "M7 stub relay: assistant capability never went operational (api log: $(tail -5 /tmp/kgsm-api-smoke-m7.log 2>/dev/null))"
  fi
  stop_api
else
  bad "M7 stub relay: API never healthy; log: $(tail -20 /tmp/kgsm-api-smoke-m7.log 2>/dev/null)"
fi
kill "$ASSIST_PID" 2>/dev/null; wait "$ASSIST_PID" 2>/dev/null

# --- M4·a auth: the no-token sweep (auth ENABLED) --------------------------
echo "==> M4·a auth checks — AUTH ENABLED instance (no-token sweep; full tier matrix in tests/Api.Tests)"
start_api_auth || { echo "API never healthy (auth-enabled); log:"; tail -20 /tmp/kgsm-api-smoke-auth.log; exit 2; }

# 31. Protected endpoints with NO bearer -> 401 + the frozen {error} envelope (never ProblemDetails).
#     Includes the diagnostics probes (_dbcheck touches the DB, _throw forces a 500): the secure-by-default
#     fallback + the admin gate close them, so "protect all prior endpoints" holds with no open back door.
#     /audit (M5) + /alerts (M6·a) + /library (M8·a) are viewer reads -> also 401 with no bearer.
#     /me (M8) is [Authorize] (any authenticated caller) -> still 401 with no bearer.
#     /integrations (M8·c) is admin-gated -> 401 with no bearer (the tier/admin gate is in tests).
auth_401=true
for p in /api/v1/hosts /api/v1/servers /api/v1/stream /api/v1/audit /api/v1/alerts /api/v1/library /api/v1/me /api/v1/servers/x/console /api/v1/servers/x/files "/api/v1/servers/x/files/content?path=y" /api/v1/integrations /api/v1/integrations/discord /api/v1/integrations/slack /api/v1/_dbcheck /api/v1/_throw /api/v1/servers/x/metrics/history /api/v1/hosts/x/metrics/history; do
  req GET "$p"
  if [[ "$CODE" != 401 ]] || ! grep -q '"code":"unauthorized"' <<<"$BODY" || grep -q 'ProblemDetails\|tools.ietf.org' <<<"$BODY"; then
    auth_401=false; echo "    ($p -> $CODE $BODY)"
  fi
done
req POST /api/v1/servers/x/commands -H 'Content-Type: application/json' -d '{"verb":"start"}'
[[ "$CODE" == 401 ]] && grep -q '"code":"unauthorized"' <<<"$BODY" || { auth_401=false; echo "    (POST commands -> $CODE $BODY)"; }
req POST /api/v1/servers -H 'Content-Type: application/json' -d '{"blueprint":"factorio"}'
[[ "$CODE" == 401 ]] && grep -q '"code":"unauthorized"' <<<"$BODY" || { auth_401=false; echo "    (POST /servers install -> $CODE $BODY)"; }
req DELETE /api/v1/servers/x
[[ "$CODE" == 401 ]] && grep -q '"code":"unauthorized"' <<<"$BODY" || { auth_401=false; echo "    (DELETE /servers/{id} -> $CODE $BODY)"; }
req PUT "/api/v1/servers/x/files/content?path=y" -H 'Content-Type: application/json' -d '{"content":"x"}'
[[ "$CODE" == 401 ]] && grep -q '"code":"unauthorized"' <<<"$BODY" || { auth_401=false; echo "    (PUT files/content -> $CODE $BODY)"; }
req POST /api/v1/assistant/turn -H 'Content-Type: application/json' -d '{"prompt":"hi"}'
[[ "$CODE" == 401 ]] && grep -q '"code":"unauthorized"' <<<"$BODY" || { auth_401=false; echo "    (POST assistant/turn -> $CODE $BODY)"; }
req POST /api/v1/library/refresh
[[ "$CODE" == 401 ]] && grep -q '"code":"unauthorized"' <<<"$BODY" || { auth_401=false; echo "    (POST library/refresh -> $CODE $BODY)"; }
$auth_401 && ok "no-bearer -> 401 envelope on /hosts,/servers,/stream,/audit,/alerts,/library,/me,/servers/{id}/console,/servers/{id}/files(+content),/integrations,/_dbcheck,/_throw,POST commands,POST+DELETE /servers,PUT files/content,POST assistant/turn,POST library/refresh,metrics/history (no open back door)" \
  || bad "no-bearer 401 sweep (see above)"

# 31b. The library cover/hero image endpoints are [AllowAnonymous] (a CSS background:url / <img> never sends
#      the bearer, and game art isn't sensitive). So with NO bearer they must 404 (no cached image), NEVER
#      401 — the 404-not-401 is the proof the anonymous override beat the class [Authorize] + the global
#      FallbackPolicy. (They are deliberately ABSENT from the 401 sweep above.)
anon_ok=true
for p in /api/v1/library/nope/cover /api/v1/library/nope/hero; do
  req GET "$p"
  [[ "$CODE" == 404 ]] || { anon_ok=false; echo "    ($p -> $CODE $BODY [expected 404, not 401])"; }
done
$anon_ok && ok "library cover/hero [AllowAnonymous]: no-bearer -> 404 (not 401) — game art renders without a token" \
  || bad "library cover/hero anonymous override (see above)"

# 32. The reachability/latency probes stay OPEN under auth (the SPA checks 'backend reachable' and
#     measures ping before login, and pings with a bare GET / no bearer to avoid a CORS preflight).
req GET /health;       H=$CODE
req GET /api/v1;       V=$CODE
req GET /api/v1/ping;  P=$CODE
[[ "$H" == 200 && "$V" == 200 && "$P" == 200 ]] && ok "/health + /api/v1 + /api/v1/ping stay open under auth (200/200/200)" \
  || bad "open endpoints under auth (health=$H meta=$V ping=$P)"

# 33. The login endpoint 503s until Discord is configured (the M4·b live half) — honest "unconfigured",
#     not a 404/500. JWT validation + tier gating (above) are enforced regardless.
req GET /auth/discord/start
[[ "$CODE" == 503 ]] && grep -q '"code":"auth_unconfigured"' <<<"$BODY" \
  && ok "/auth/discord/start -> 503 auth_unconfigured (login needs Discord cfg; M4·b)" \
  || bad "/auth/discord/start 503 (code=$CODE body=$BODY)"

# Coverage note: the 401/403/tier matrix (viewer/operator/admin), the callback verdict (ok/denied/
# invalid/upstream-error), refresh rotation, the session snapshot and the WS ?access_token= path are
# proven deterministically in tests/Api.Tests with the Discord seam faked. The real discord.com code
# exchange + bot-token role lookup are the M4·b LIVE half (validated once on the trusted host when the
# Discord app / bot token / guild / role-map are supplied).
echo "  (note: tier matrix + callback/refresh/session are in tests/Api.Tests; live OAuth is M4·b)"

stop_api

echo
echo "==> ${pass} passed, ${fail} failed"
exit $(( fail > 0 ? 1 : 0 ))
