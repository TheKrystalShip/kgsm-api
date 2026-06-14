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
#
# The M1·a checks run deterministically with NO monitor (the degrade path: metrics down,
# capacity null, watchdog/assistant absent). Point them at live leaves to prove the happy
# path: SMOKE_MONITOR_SOCKET=<sock> (metrics operational + real capacity), SMOKE_WATCHDOG_SOCKET=<sock>.
# What this CANNOT prove is a real browser preflight (CORS is browser-enforced) or an
# actual SPA fetch — those wait on frontend access.
#
# Usage:  scripts/smoke.sh                 # build + run all checks
#         SMOKE_PORT=9001 scripts/smoke.sh
#         SMOKE_SKIP_BUILD=1 scripts/smoke.sh   # reuse the existing Release build
#         SMOKE_MONITOR_SOCKET=/run/kgsm-monitor.sock scripts/smoke.sh   # prove happy path
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

# --- run -------------------------------------------------------------------
echo "==> Starting API on ${BASE}"
KGSM_API_URLS="$BASE" KGSM_API_DB="$DB" \
KGSM_API_HOST_ID="$HOST_ID" KGSM_API_MONITOR_SOCKET="$MON_SOCK" KGSM_API_WATCHDOG_SOCKET="$WD_SOCK" \
  dotnet "$DLL" >/tmp/kgsm-api-smoke.log 2>&1 &
SRV=$!
cleanup() { kill "$SRV" 2>/dev/null; wait "$SRV" 2>/dev/null; }
trap cleanup EXIT

for _ in $(seq 1 80); do
  curl -fsS "${BASE}/healthz" >/dev/null 2>&1 && break
  sleep 0.1
done

# req METHOD PATH [extra curl args...] -> sets $CODE and $BODY
req() {
  local method="$1" path="$2"; shift 2
  curl -s -X "$method" -o /tmp/kgsm-api-smoke.body -w '%{http_code}' "$@" "${BASE}${path}" > /tmp/kgsm-api-smoke.code
  CODE="$(cat /tmp/kgsm-api-smoke.code)"; BODY="$(cat /tmp/kgsm-api-smoke.body)"
}

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

echo
echo "==> ${pass} passed, ${fail} failed"
exit $(( fail > 0 ? 1 : 0 ))
