#!/usr/bin/env bash
#
# Build + deploy kgsm-api in one go.
#
#   ./deploy/deploy.sh
#
# Builds (publishes) the API as YOU — the invoking, service-owning user — and uses sudo
# ONLY for the steps that touch systemd / root-owned paths (stop, unit refresh,
# daemon-reload, start). Run it as your normal user; it will prompt for your sudo password
# when it reaches those privileged steps.
#
# Builds as the invoking user (not root) so dotnet publish never pollutes src/ with
# root-owned obj/bin. Fit for both first-run and redeploy:
#   * the binary tree is synced in place into /opt/kgsm-api (stale files pruned),
#   * the systemd unit is refreshed only if it changed,
#   * the env file /etc/kgsm-api/kgsm-api.env is created from the template only if absent
#     (NEVER overwritten — your secrets survive a redeploy),
#   * the DB (/var/lib/kgsm-api) and the env file live outside /opt and are untouched.
#
# Deploy is verified by an actual HTTP 200 from /health — not just "the unit launched".
#
# Testing/automation hook (no password ever lives in this script): the privileged calls go
# through "$SUDO" (default: sudo). To drive it non-interactively, point sudo at an askpass:
#   printf '#!/bin/sh\necho YOUR_PASS\n' > /tmp/ap && chmod +x /tmp/ap
#   SUDO='sudo -A' SUDO_ASKPASS=/tmp/ap ./deploy/deploy.sh
#
set -euo pipefail

# ── Paths / config ────────────────────────────────────────────────────────────
PREFIX="/opt/kgsm-api"
UNIT_DST="/etc/systemd/system/kgsm-api.service"
ENV_DIR="/etc/kgsm-api"
ENV_FILE="$ENV_DIR/kgsm-api.env"
SERVICE="kgsm-api"

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_DIR/src/Api/Api.csproj"
UNIT_SRC="$REPO_DIR/deploy/kgsm-api.service"
ENV_EXAMPLE="$REPO_DIR/deploy/kgsm-api.env.example"
PUBLISH_DIR="$REPO_DIR/artifacts/publish"
RID="${RID:-linux-x64}"
SVC_USER="${KGSM_API_USER:-$(id -un)}"

# Privileged-call indirection: real users get a normal sudo prompt; an automated run can
# set SUDO='sudo -A' + SUDO_ASKPASS=... to inject the password. Never hard-code it here.
SUDO="${SUDO:-sudo}"

# Where to prove the service is actually alive. Override if the host rebinds the port.
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:8080/health}"
HEALTH_TRIES="${HEALTH_TRIES:-30}"

# ── Helpers ─────────────────────────────────────────────────────────────────
log() { printf '\033[1;34m>> %s\033[0m\n' "$*"; }
err() { printf '\033[1;31m!! %s\033[0m\n' "$*" >&2; }

STOPPED=0
on_err() {
    local line="$1"
    err "deploy failed (line ${line})."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap and may be down — attempting to bring it back up..."
        $SUDO systemctl start "$SERVICE" \
            && err "restarted ${SERVICE} (note: it is running the PREVIOUS build)." \
            || err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

# Poll /health until it returns 200, or give up. Used inside an `if`, so a failing curl
# does not trip the ERR trap.
wait_health() {
    local i
    for ((i = 1; i <= HEALTH_TRIES; i++)); do
        # Quiet per-attempt: a connection refused during the ~1s warmup is expected, not an
        # error to show. The give-up path below surfaces the journal instead.
        if curl -fsS -o /dev/null --max-time 2 "$HEALTH_URL" 2>/dev/null; then
            return 0
        fi
        sleep 1
    done
    return 1
}

# ── Pre-flight ────────────────────────────────────────────────────────────────
if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    err "do NOT run this as root — run it as the service user (${SERVICE} runs as 'heisen')."
    err "it builds as you and sudo's only the systemd steps."
    exit 1
fi
[[ -f "$PROJECT" ]] || { err "project not found: $PROJECT"; exit 1; }

# ── 1. Build (as the invoking user — no privilege, fail fast before any disruption) ──
log "publishing self-contained (${RID}) → ${PUBLISH_DIR}"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained -p:PublishReadyToRun=true -o "$PUBLISH_DIR"

# ── 2. Privileged prep (no service disruption yet) ────────────────────────────
# Ensure the install dir exists and is owned by the service user (so the swap below needs
# no sudo and ownership stays consistent).
if [[ ! -d "$PREFIX" ]]; then
    log "creating ${PREFIX} (owned by ${SVC_USER})"
    $SUDO install -d -m 0755 -o "$SVC_USER" -g "$SVC_USER" "$PREFIX"
elif [[ ! -w "$PREFIX" ]]; then
    err "${PREFIX} is not writable by $(id -un) (expected it owned by the service user)."
    err "fix once: sudo chown -R ${SVC_USER}:${SVC_USER} ${PREFIX}"
    exit 1
fi

# Env file: create from template only if absent — never clobber live secrets.
if [[ ! -f "$ENV_FILE" ]]; then
    log "creating ${ENV_FILE} from template — EDIT IT (KGSM_API_AUTH_SIGNING_KEY + Discord creds)"
    $SUDO install -d -m 0755 "$ENV_DIR"
    $SUDO install -m 0640 -g "$SVC_USER" "$ENV_EXAMPLE" "$ENV_FILE"
fi

# Unit: refresh only if it changed (cmp also fails-nonzero when UNIT_DST is absent → fresh
# install installs it too). daemon-reload only when we actually touched the unit.
UNIT_CHANGED=0
if ! cmp -s "$UNIT_SRC" "$UNIT_DST"; then
    log "systemd unit differs → installing ${UNIT_DST}"
    $SUDO install -m 0644 "$UNIT_SRC" "$UNIT_DST"
    UNIT_CHANGED=1
fi

# ── 3. The swap (minimal window: stop → sync the tree → start) ─────────────────
log "stopping ${SERVICE} (release the running binary)"
$SUDO systemctl stop "$SERVICE"
STOPPED=1

log "syncing publish tree → ${PREFIX}"
rsync -a --delete "$PUBLISH_DIR/" "$PREFIX/"

if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    $SUDO systemctl daemon-reload
fi

log "enabling + starting ${SERVICE}"
$SUDO systemctl enable --now "$SERVICE" >/dev/null 2>&1 || $SUDO systemctl start "$SERVICE"
STOPPED=0

# ── 4. Verify (the real pass/fail: an actual 200 from /health) ─────────────────
log "waiting for ${SERVICE} to report healthy at ${HEALTH_URL} ..."
if wait_health; then
    log "kgsm-api is up and healthy ✓"
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
else
    err "service started but ${HEALTH_URL} did not return 200 within ${HEALTH_TRIES}s."
    err "recent logs:"
    $SUDO journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi
