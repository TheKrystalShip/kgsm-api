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
#
# Two phases. Phase A runs deterministically with NO monitor (the degrade path: host metrics
# down + capacity null; every server metrics:null). Phase B starts an EMBEDDED stub monitor
# (a unix socket serving a canned Snapshot with a per-server row keyed to a real instance) and
# restarts the API at it — proving the happy path: host capacity present + the servers JOIN's
# present-branch (metrics carried through, keyed by id). The stub makes both deterministic with
# no external monitor; SMOKE_MONITOR_SOCKET still overrides Phase A's monitor if you want a live one.
# What this CANNOT prove is a real browser preflight (CORS is browser-enforced) or an actual SPA
# fetch — those wait on frontend access.
#
# Usage:  scripts/smoke.sh                 # build + run all checks (both phases)
#         SMOKE_PORT=9001 scripts/smoke.sh
#         SMOKE_SKIP_BUILD=1 scripts/smoke.sh   # reuse the existing Release build
#         SMOKE_KGSM_PATH=/path/to/kgsm.sh scripts/smoke.sh   # engine on another host
#         SMOKE_MONITOR_SOCKET=/run/kgsm-monitor.sock scripts/smoke.sh   # live monitor in Phase A
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${SMOKE_PORT:-8099}"
BASE="http://127.0.0.1:${PORT}"
DLL="${REPO_ROOT}/src/Api/bin/Release/net10.0/kgsm-api.dll"
DB="${SMOKE_DB:-/tmp/kgsm-api-smoke.db}"; rm -f "$DB"

# M1·a leaf wiring (deterministic defaults: no monitor, no watchdog, no assistant).
HOST_ID="${SMOKE_HOST_ID:-smoke-host}"
MON_SOCK="${SMOKE_MONITOR_SOCKET:-/tmp/kgsm-api-smoke-nomonitor.sock}"  # absent by default
WD_SOCK="${SMOKE_WATCHDOG_SOCKET:-}"                                    # empty -> watchdog absent

# M1·b engine wiring. kgsm-lib is base, not a leaf: point it at the canonical dev checkout so
# GET /servers reads a real roster. Override with SMOKE_KGSM_PATH on another host.
KGSM_PATH="${SMOKE_KGSM_PATH:-/home/heisen/tks/kgsm/kgsm.sh}"
# Unix socket for the embedded stub monitor (Phase B — proves the join's present-branch).
STUB_SOCK="/tmp/kgsm-api-smoke-stub-monitor.sock"; rm -f "$STUB_SOCK"
# Canned per-server metric values the stub serves; the join must carry these through verbatim.
STUB_CPU=142.5; STUB_MEM=3422552064; STUB_IO_READ=4096; STUB_PIDS=12  # ioWrite is null (nullable passthrough)

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
  rm -f "$STUB_SOCK" 2>/dev/null
}
trap cleanup EXIT

# start_api MONITOR_SOCKET — launch the API with the given monitor socket; wait for /healthz.
start_api() {
  KGSM_API_URLS="$BASE" KGSM_API_DB="$DB" \
  KGSM_API_HOST_ID="$HOST_ID" KGSM_API_MONITOR_SOCKET="$1" KGSM_API_WATCHDOG_SOCKET="$WD_SOCK" \
  KGSM_API_KGSM_PATH="$KGSM_PATH" \
    dotnet "$DLL" >/tmp/kgsm-api-smoke.log 2>&1 &
  SRV=$!; PIDS+=("$SRV")
  for _ in $(seq 1 80); do curl -fsS "${BASE}/healthz" >/dev/null 2>&1 && return 0; sleep 0.1; done
  return 1
}
stop_api() { kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; }

# req METHOD PATH [extra curl args...] -> sets $CODE and $BODY
req() {
  local method="$1" path="$2"; shift 2
  curl -s -X "$method" -o /tmp/kgsm-api-smoke.body -w '%{http_code}' "$@" "${BASE}${path}" > /tmp/kgsm-api-smoke.code
  CODE="$(cat /tmp/kgsm-api-smoke.code)"; BODY="$(cat /tmp/kgsm-api-smoke.body)"
}

# --- Phase A: no monitor (degrade path) ------------------------------------
echo "==> Phase A — Starting API on ${BASE} (no monitor; kgsm=${KGSM_PATH})"
start_api "$MON_SOCK" || { echo "API never became healthy; log:"; tail -20 /tmp/kgsm-api-smoke.log; exit 2; }

echo "==> Checks"

# 1. /healthz — 200, camelCase liveness, Z timestamp
req GET /healthz
[[ "$CODE" == 200 ]] && grep -q '"status":"ok"' <<<"$BODY" && grep -q '"service":"kgsm-api"' <<<"$BODY" \
  && ok "/healthz 200 + {status,service}" || bad "/healthz shape (code=$CODE body=$BODY)"

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

# 4. EF Core + SQLite round-trip (the data layer wiring, end-to-end)
req GET /api/v1/_dbcheck
[[ "$CODE" == 200 ]] && grep -qE '"probes":[1-9]' <<<"$BODY" \
  && ok "EF+SQLite round-trip ($(grep -oE '"probes":[0-9]+' <<<"$BODY"))" || bad "_dbcheck (code=$CODE body=$BODY)"

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

# 12. The honesty invariant: metrics status <-> capacity coupling. operational <=> capacity
#     present (real numbers); down/absent <=> capacity null. Never a fabricated default.
req GET "/api/v1/hosts/${HOST_ID}"
if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
m=d['capabilities']['metrics']['status']
present = d['cpuPct'] is not None and d['mem'] is not None and d['disks'] is not None
absent  = d['cpuPct'] is None and d['mem'] is None and d['disks'] is None
sys.exit(0 if ((m=='operational' and present) or (m!='operational' and absent)) else 1)
" 2>/dev/null; then
  STATUS="$(python3 -c "import json;print(json.load(open('/tmp/kgsm-api-smoke.body'))['capabilities']['metrics']['status'])" 2>/dev/null)"
  ok "metrics '${STATUS}' ↔ capacity coupling (honest unknown, never fabricated)"
else bad "capacity/metrics coupling violated (body=$BODY)"; fi

# --- M1·b: servers (kgsm-lib domain+run-state ⋈ monitor metrics) -----------
echo "==> M1·b server checks — Phase A degrade (no monitor; kgsm=${KGSM_PATH})"

# 13. GET /servers — honest DTO shape: stable keys, valid status/runtime enums, this host's id.
req GET /api/v1/servers
if [[ "$CODE" == 200 ]] && EXP="$HOST_ID" python3 -c "
import json,os,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
if not (isinstance(d,list) and len(d)>=1): sys.exit(2)   # empty roster -> can't prove a real read
keys={'id','name','blueprint','status','version','runtime','hostId','metrics'}
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
  "cpu": {"totalPct": 12.5, "perCore": [10.0, 15.0], "load": {"one": 0.4, "five": 0.5, "fifteen": 0.6}},
  "mem": {"totalKb": 32768000, "availableKb": 16384000, "usedKb": 16384000, "usedPct": 50.0, "swapTotalKb": 0, "swapUsedKb": 0},
  "disk": {"mounts": [{"mount": "/", "fs": "ext4", "totalBytes": 500000000000, "usedBytes": 250000000000, "usedPct": 50.0}], "io": {"readBps": 1000, "writeBps": 2000}},
  "net": {"ifaces": [{"name": "eth0", "rxBps": 100, "txBps": 200, "rxPps": 1, "txPps": 2}]},
  "servers": [{"id": sid, "name": sid, "kind": "native",
               "cpuPctCore": float(os.environ['SNAP_CPU']), "memBytes": int(os.environ['SNAP_MEM']),
               "ioReadBps": int(os.environ['SNAP_IOREAD']), "ioWriteBps": None, "pids": int(os.environ['SNAP_PIDS'])}],
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
  SNAP_ID="$FIRST_ID" SNAP_CPU="$STUB_CPU" SNAP_MEM="$STUB_MEM" SNAP_IOREAD="$STUB_IO_READ" SNAP_PIDS="$STUB_PIDS" \
    python3 "$STUB_PY" "$STUB_SOCK" >/tmp/kgsm-api-smoke-stub.log 2>&1 &
  PIDS+=("$!")
  for _ in $(seq 1 40); do [[ -S "$STUB_SOCK" ]] && break; sleep 0.1; done
  start_api "$STUB_SOCK" || { echo "API never healthy (Phase B); log:"; tail -20 /tmp/kgsm-api-smoke.log; exit 2; }

  # 16. Host happy path, now deterministic: metrics operational + capacity present (M1·a, via stub).
  req GET "/api/v1/hosts/${HOST_ID}"
  if [[ "$CODE" == 200 ]] && python3 -c "
import json,sys
d=json.load(open('/tmp/kgsm-api-smoke.body'))
sys.exit(0 if (d['capabilities']['metrics']['status']=='operational'
               and d['cpuPct'] is not None and d['mem'] is not None and d['disks'] is not None) else 1)
" 2>/dev/null; then
    ok "host metrics operational + capacity present (stub snapshot)"
  else bad "host happy-path capacity (code=$CODE body=$BODY)"; fi

  # 17. The JOIN present-branch (detail path): the monitor row is carried through VERBATIM, keyed by id.
  req GET "/api/v1/servers/${FIRST_ID}"
  if [[ "$CODE" == 200 ]] && \
     CPU="$STUB_CPU" MEM="$STUB_MEM" IOREAD="$STUB_IO_READ" PIDS_E="$STUB_PIDS" python3 -c "
import json,os,sys
m=json.load(open('/tmp/kgsm-api-smoke.body')).get('metrics')
if m is None: sys.exit(1)
sys.exit(0 if (abs(m['cpuPctCore']-float(os.environ['CPU']))<1e-6
               and m['memBytes']==int(os.environ['MEM'])
               and m['ioReadBps']==int(os.environ['IOREAD'])
               and m['ioWriteBps'] is None
               and m['pids']==int(os.environ['PIDS_E'])) else 2)
" 2>/dev/null; then
    ok "/servers/{id} JOIN present-branch (cpuPctCore>100 + null ioWrite carried through, keyed by id)"
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

  stop_api
fi

echo
echo "==> ${pass} passed, ${fail} failed"
exit $(( fail > 0 ? 1 : 0 ))
