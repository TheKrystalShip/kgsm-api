#!/usr/bin/env bash
#
# setup-leaf-config.sh — one-time privileged wiring for the kgsm-api "runtime leaf configuration"
# feature (see ../leaf-runtime-config-plan.md "Privilege model" + deploy/leaf-config/README.md).
#
#   ./deploy/setup-leaf-config.sh
#
# Idempotent and safe to re-run. deploy/deploy.sh invokes it on every deploy so the wiring is
# re-asserted from a fresh checkout, and a steady-state re-run is a no-op (every install is cmp-guarded,
# the daemon-reload is conditional, and the override dir is created only if missing).
#
# It does the THREE things — the ONLY privileged setup this feature needs:
#   1. ensure /var/lib/kgsm-api/leaf-overrides/ exists (0700, owned by the service user) — the API
#      writes each leaf's override env file here UNPRIVILEGED (it's inside the API's own state dir).
#   2. install a systemd drop-in per configurable leaf (monitor/watchdog/assistant/firewall) that
#      layers that leaf's override env file on LAST, the leaf staying unaware of the API.
#   3. install a scoped polkit rule letting the service user `systemctl restart` ONLY those four units.
#
# Run it as the SERVICE USER (not root); it sudo's ONLY the steps that touch /etc + systemd, exactly
# like deploy.sh. No password is ever stored here — privileged calls go through "$SUDO" (default: sudo).
# To drive it non-interactively (CI / Claude Code), point sudo at an askpass:
#   printf '#!/bin/sh\necho YOUR_PASS\n' > /tmp/ap && chmod +x /tmp/ap
#   SUDO='sudo -A' SUDO_ASKPASS=/tmp/ap ./deploy/setup-leaf-config.sh
#
set -euo pipefail

# ── Paths / config ────────────────────────────────────────────────────────────
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LEAF_CONFIG_DIR="$REPO_DIR/deploy/leaf-config"
DROPIN_TEMPLATE="$LEAF_CONFIG_DIR/dropins/50-kgsm-api-override.conf.in"
POLKIT_TEMPLATE="$LEAF_CONFIG_DIR/49-kgsm-api-leaf-restart.rules.in"

STATE_DIR="/var/lib/kgsm-api"                       # the API unit's StateDirectory (systemd, 0750 heisen)
OVERRIDE_DIR="$STATE_DIR/leaf-overrides"            # the API renders <leaf>.env here, unprivileged
DROPIN_NAME="50-kgsm-api-override.conf"             # the per-leaf drop-in filename
POLKIT_DST="/etc/polkit-1/rules.d/49-kgsm-api-leaf-restart.rules"

# The configurable leaves → their systemd unit. MUST match src/Api/Services/Leaves/LeafCatalog.cs and
# the unit names listed in the polkit template (asserted below). NB: assistant carries the '-service'
# segment — kgsm-assistant-service.service, not kgsm-assistant.service.
declare -A LEAF_UNITS=(
    [monitor]="kgsm-monitor.service"
    [watchdog]="kgsm-watchdog.service"
    [assistant]="kgsm-assistant-service.service"
    [firewall]="kgsm-firewall.service"
)

SVC_USER="${KGSM_API_USER:-$(id -un)}"

# Privileged-call indirection (same contract as deploy.sh): real users get a sudo prompt; an automated
# run sets SUDO='sudo -A' + SUDO_ASKPASS=… to inject the password. Never hard-code it here.
SUDO="${SUDO:-sudo}"

# ── Helpers ─────────────────────────────────────────────────────────────────
log() { printf '\033[1;34m>> %s\033[0m\n' "$*"; }
err() { printf '\033[1;31m!! %s\033[0m\n' "$*" >&2; }

trap 'err "setup-leaf-config failed (line ${LINENO})."; exit 1' ERR

# Render a template (substitute placeholders) to stdout.
render_dropin() { sed "s|@LEAF@|$1|g" "$DROPIN_TEMPLATE"; }            # $1 = leaf id
render_polkit() { sed "s|@SVC_USER@|$SVC_USER|g" "$POLKIT_TEMPLATE"; }

# Install a rendered file privileged ONLY if it differs from what's already there (cmp via $SUDO because
# /etc/polkit-1/rules.d is root-only-readable). Returns 0 if it changed/installed, 1 if already current.
install_if_changed() {  # $1=rendered-src  $2=dest  $3=mode
    local src="$1" dst="$2" mode="$3"
    if $SUDO cmp -s "$src" "$dst" 2>/dev/null; then
        return 1
    fi
    $SUDO install -D -m "$mode" "$src" "$dst"
    return 0
}

# ── Pre-flight ────────────────────────────────────────────────────────────────
if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    err "do NOT run this as root — run it as the kgsm-api service user (here: '${SVC_USER}')."
    err "it sudo's only the /etc + systemd steps and templates the polkit rule with that user."
    exit 1
fi
[[ -f "$DROPIN_TEMPLATE" ]] || { err "drop-in template missing: $DROPIN_TEMPLATE"; exit 1; }
[[ -f "$POLKIT_TEMPLATE" ]] || { err "polkit template missing: $POLKIT_TEMPLATE"; exit 1; }
command -v pkaction >/dev/null 2>&1 || \
    err "warning: polkit (pkaction) not found — the restart grant won't be honored until polkit is installed."

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"; err "setup-leaf-config failed (line ${LINENO})."; exit 1' ERR

# Render the polkit rule once and assert it names EVERY unit we install a drop-in for — this is the
# guard against the leaf→unit map here drifting from the granted units (a security-relevant mismatch).
POLKIT_RENDERED="$WORK/polkit.rules"
render_polkit > "$POLKIT_RENDERED"
for leaf in "${!LEAF_UNITS[@]}"; do
    unit="${LEAF_UNITS[$leaf]}"
    grep -q "\"$unit\"" "$POLKIT_RENDERED" || {
        err "drift: '$unit' (leaf '$leaf') is NOT granted by $POLKIT_TEMPLATE — refusing to install a"
        err "drop-in for a unit the polkit rule can't restart. Reconcile the template with LeafCatalog.cs."
        exit 1
    }
done

log "kgsm-api leaf-config setup — service user '${SVC_USER}', ${#LEAF_UNITS[@]} configurable leaves"

# ── 1. The override directory (the API writes <leaf>.env here, unprivileged) ───
if [[ -d "$OVERRIDE_DIR" ]]; then
    : # already present
elif [[ -w "$STATE_DIR" ]]; then
    # The API has run at least once → systemd made the StateDirectory and we own it → no sudo needed.
    log "creating ${OVERRIDE_DIR} (0700, unprivileged)"
    install -d -m 0700 "$OVERRIDE_DIR"
else
    # Fresh host: the API has never started, so systemd hasn't made its StateDirectory yet. Create the
    # tree privileged, owned by the service user, so the API writes overrides into it UNprivileged after.
    log "creating ${OVERRIDE_DIR} (0700, owned by ${SVC_USER})"
    [[ -d "$STATE_DIR" ]] || $SUDO install -d -m 0750 -o "$SVC_USER" -g "$SVC_USER" "$STATE_DIR"
    $SUDO install -d -m 0700 -o "$SVC_USER" -g "$SVC_USER" "$OVERRIDE_DIR"
fi

# ── 2. The per-leaf systemd drop-ins ──────────────────────────────────────────
DROPIN_CHANGED=0
for leaf in "${!LEAF_UNITS[@]}"; do
    unit="${LEAF_UNITS[$leaf]}"
    dst="/etc/systemd/system/${unit}.d/${DROPIN_NAME}"
    rendered="$WORK/${leaf}.conf"
    render_dropin "$leaf" > "$rendered"
    if install_if_changed "$rendered" "$dst" 0644; then
        log "installed drop-in → ${dst}"
        DROPIN_CHANGED=1
    fi
done

# ── 3. The scoped polkit rule (restart ONLY the four leaves) ───────────────────
if install_if_changed "$POLKIT_RENDERED" "$POLKIT_DST" 0644; then
    log "installed polkit rule → ${POLKIT_DST} (restart grant for '${SVC_USER}')"
    # polkitd watches rules.d and reloads on its own — no service reload needed for the rule itself.
fi

# ── 4. daemon-reload — ONLY if a drop-in changed (override CONTENT changes never need this) ─────
if [[ "$DROPIN_CHANGED" -eq 1 ]]; then
    log "a drop-in changed → systemctl daemon-reload (non-disruptive; leaves keep running)"
    $SUDO systemctl daemon-reload
fi

rm -rf "$WORK"
trap - ERR

# ── 5. Summary ────────────────────────────────────────────────────────────────
log "leaf-config wiring in place ✓"
printf '   granted: %s may  systemctl restart  (and try-restart / reload-or-restart) ONLY:\n' "$SVC_USER"
for leaf in monitor watchdog assistant firewall; do
    printf '            • %-9s → %s\n' "$leaf" "${LEAF_UNITS[$leaf]}"
done
printf '   overrides: %s/<leaf>.env  (API writes these unprivileged; applied on the next restart)\n' "$OVERRIDE_DIR"
printf '   verify (as %s, no password expected — firewall is the cheapest to bounce):\n' "$SVC_USER"
printf '            systemctl restart kgsm-firewall.service && echo OK\n'
