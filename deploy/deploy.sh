#!/usr/bin/env bash
#
# deploy.sh — build + deploy kgsm-api. Fully headless: no sudo, no prompts, ever.
#
#   ./deploy/deploy.sh
#
# Assumes deploy/setup.sh has already provisioned this host (prefix owned by you, the unit
# symlinked out of a directory you own, polkit grant in place). If it has not, this script says
# so and stops before building — it never half-deploys and never blocks on a password.
#
# It builds as YOU, so dotnet publish never pollutes src/ with root-owned obj/bin:
#   * the binary tree is synced in place into /opt/kgsm-api (stale files pruned),
#   * the systemd unit is refreshed only if it changed (a write to a file you own + daemon-reload),
#   * the env file /etc/kgsm-api/kgsm-api.env is setup.sh's business and is never touched here,
#   * the DB (/var/lib/kgsm-api) lives outside /opt and is untouched.
#
# Deploy is verified by an actual HTTP 200 from /health — not just "the unit launched". The
# health URL is resolved from the configured bind (see deploy-common.sh), not hardcoded.
#
# Knobs: RID, HEALTH_URL, HEALTH_TRIES, SKIP_SPA=1 (API-only), KGSM_WEB_DIR, LOCAL_NUGET.
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

PROJECT_CSPROJ="$REPO_DIR/src/Api/Api.csproj"
RID="${RID:-linux-x64}"

# The umbrella tks/ checkout — the sibling repos (kgsm-lib, kgsm-monitor) live here. This repo
# consumes them as PACKAGES from a local folder feed (src/Api/nuget.config → $LOCAL_NUGET), so a
# fresh clone needs those packages packed before restore can succeed (bootstrapped below).
WORKSPACE="$(cd "$REPO_DIR/.." && pwd)"
LOCAL_NUGET="${LOCAL_NUGET:-/home/heisen/local-nuget}"

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap and may be down — bringing it back up ..."
        if systemctl start "$SERVICE"; then
            err "restarted ${SERVICE} (note: it is running the PREVIOUS build)."
        else
            err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
        fi
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

# ── Preflight ─────────────────────────────────────────────────────────────────
refuse_root
require_setup
[[ -f "$PROJECT_CSPROJ" ]] || { err "project not found: $PROJECT_CSPROJ"; exit 1; }

# Framework-dependent single-file: the host must have the .NET 10 + ASP.NET Core SHARED runtime
# (we deliberately do NOT bundle it — that keeps the artifact ~9 MB instead of ~90 MB). Verify it
# up front so a missing runtime fails here, not after we've stopped the live service.
if ! dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App 10\.'; then
    err "the .NET 10 ASP.NET Core shared runtime is not installed (need 'Microsoft.AspNetCore.App 10.x')."
    err "install the .NET 10 runtime (or SDK), then re-run. Check: dotnet --list-runtimes"
    exit 1
fi

# ── 1. Build (as the invoking user — no privilege, fail fast before any disruption) ──
# Bootstrap the local NuGet feed. This repo consumes the sibling kgsm-lib + monitor-contracts as
# PACKAGES, not project refs, so on a fresh umbrella checkout those packages aren't packed yet.
# Pack them from the siblings if absent — pack-if-MISSING only, so an already-published version is
# never repacked (avoids the same-version stale-dll NuGet-cache trap).
pkg_ver() { grep -oP "Include=\"$1\"\s+Version=\"\K[^\"]+" "$PROJECT_CSPROJ"; }
ensure_pkg() {  # csproj  package-id  version
    local csproj="$1" id="$2" ver="$3"
    [[ -n "$ver" ]] || { err "could not read $id version from $PROJECT_CSPROJ"; exit 1; }
    [[ -f "$LOCAL_NUGET/${id}.${ver}.nupkg" ]] && return 0
    [[ -f "$csproj" ]] || { err "need $id $ver, but $LOCAL_NUGET lacks it and the sibling source is missing:"; err "  $csproj"; err "clone the full tks workspace (umbrella checkout) so kgsm-lib + kgsm-monitor are present."; exit 1; }
    log "packing $id $ver → $LOCAL_NUGET (from $(basename "$(dirname "$csproj")"))"
    dotnet pack "$csproj" -c Release -o "$LOCAL_NUGET"
}
mkdir -p "$LOCAL_NUGET"
ensure_pkg "$WORKSPACE/kgsm-lib/kgsm-lib/kgsm-lib.csproj"                            TheKrystalShip.KGSM.Lib               "$(pkg_ver TheKrystalShip.KGSM.Lib)"
ensure_pkg "$WORKSPACE/kgsm-monitor/src/Monitor.Contracts/Monitor.Contracts.csproj" TheKrystalShip.KGSM.Monitor.Contracts "$(pkg_ver TheKrystalShip.KGSM.Monitor.Contracts)"

log "publishing framework-dependent single-file (${RID}) → ${PUBLISH_DIR}"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT_CSPROJ" -c Release -r "$RID" --no-self-contained -o "$PUBLISH_DIR"

# ── 1b. Bundle the Control Panel SPA (Kestrel serves it same-origin) ───────────
# kgsm-api serves the SPA at / on the SAME origin as the API (one domain, no CORS). Build the
# sibling kgsm-web checkout and drop its dist/ into the publish wwwroot, where UseStaticFiles +
# the SPA fallback serve it. VITE_API_BASE=self makes the bundle talk to whatever origin served
# it (no baked domain). Skip with SKIP_SPA=1 for an API-only deploy (the API runs fine with no
# SPA — Startup's serveSpa gate no-ops when wwwroot has no index.html).
#
# A run that does not build the SPA leaves the deployed one alone. The publish tree is rebuilt from
# empty every deploy, so without this the prune below reads "no wwwroot here" as "delete the one
# over there" — an API-only deploy would take the Control Panel down with it. Not bundling a page
# and removing the page already being served are different intentions.
SPA_DIR="${KGSM_WEB_DIR:-$WORKSPACE/kgsm-web}"
SPA_SYNC_EXCLUDES=(--exclude='/wwwroot/')
if [[ "${SKIP_SPA:-0}" == "1" ]]; then
    log "SKIP_SPA=1 → not bundling the SPA (API-only deploy; the deployed one is left as it is)"
elif [[ -f "$SPA_DIR/package.json" ]]; then
    command -v npm >/dev/null 2>&1 || { err "npm not found, but the SPA build needs it (set SKIP_SPA=1 to skip)"; exit 1; }
    log "building the SPA (${SPA_DIR}) → bundling into wwwroot"
    (
        cd "$SPA_DIR" || exit 1
        [[ -d node_modules ]] || npm ci
        VITE_API_BASE=self npm run build
    )
    rm -rf "$PUBLISH_DIR/wwwroot"
    mkdir -p "$PUBLISH_DIR/wwwroot"
    cp -a "$SPA_DIR/dist/." "$PUBLISH_DIR/wwwroot/"
    # This run owns wwwroot, so the prune manages it: a file the new bundle dropped must go.
    SPA_SYNC_EXCLUDES=()
else
    warn "SPA checkout not found at ${SPA_DIR} — deploying API-only (the deployed SPA is left as it is)."
    warn "set KGSM_WEB_DIR=/path/to/kgsm-web, or SKIP_SPA=1 to silence this."
fi

# ── 2. Refresh the unit if it changed (we own the file; systemd reads it via the symlink) ──
install_units_unprivileged
if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    sysctl_do daemon-reload
fi

# ── 2b. Publish this API's own leaf config descriptor ──────────────────────────
# The API is a config target like any other leaf, read-only: the panel shows what this service is
# configured with, and says why it cannot change it from there.
install_leaf_descriptor

# ── 3. The swap (minimal window: stop → sync the tree → start) ─────────────────
log "stopping ${SERVICE} (release the running binary)"
sysctl_do stop "$SERVICE"
STOPPED=1

log "syncing publish tree → ${PREFIX}"
rsync -a --delete "${SPA_SYNC_EXCLUDES[@]}" "$PUBLISH_DIR/" "$PREFIX/"

log "starting ${SERVICE}"
sysctl_do start "$SERVICE"
STOPPED=0

# ── 4. Verify (the real pass/fail: an actual 200 from /health) ─────────────────
log "waiting for ${SERVICE} to report healthy at ${HEALTH_URL} ..."
if wait_health; then
    log "kgsm-api is up and healthy ✓"
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true

    # ── 5. The first administrator ────────────────────────────────────────────
    # A host with no KGSM accounts has nobody who can sign in to create one, and with no identity
    # provider configured there is no other door either. So the very first successful deploy makes
    # one and prints its generated password once.
    #
    # Safe to run every time: `user bootstrap` is a no-op the moment any account exists, so this
    # fires exactly once per host and never touches accounts afterwards. It writes the same store the
    # running service reads — two processes on one SQLite file is what the store is built for — so
    # nothing needs restarting. A failure here is reported and does not fail the deploy: the service
    # is up and healthy, which is what this script promised.
    "${PREFIX}/${PROJECT}" user bootstrap \
        || warn "could not create the first administrator; run '${PREFIX}/${PROJECT} user bootstrap' by hand."
else
    err "service started but ${HEALTH_URL} did not return 200 within ${HEALTH_TRIES}s."
    err "recent logs:"
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi
