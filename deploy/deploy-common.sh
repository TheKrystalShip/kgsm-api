#!/usr/bin/env bash
#
# deploy-common.sh — the shared parameter block + helpers for kgsm-api's deploy scripts.
#
# Sourced by BOTH deploy/setup.sh (the one-shot privileged host provisioning) and
# deploy/deploy.sh (the headless code delivery). Every path, unit name and user lives here
# exactly once, so the two entry points can never disagree about what this project installs.
#
# The canonical source of this pattern is tks/scripts/deploy-template/ — see its README for the
# contract. This copy is vendored so a standalone kgsm-api clone deploys with no umbrella
# checkout present. Keep everything below the PROJECT BLOCK in step with the template.
#
# Not executable on its own.

# This file only DEFINES things; every variable below is consumed by the two scripts that
# source it, which shellcheck cannot see from here.
# shellcheck disable=SC2034

set -euo pipefail

# ── Identity (needed by the project block below) ──────────────────────────────
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# The user that owns the install and runs the service. Everything is provisioned FOR this
# user so that day-to-day deploys need no privilege at all.
DEPLOY_USER="${KGSM_API_USER:-${KGSM_DEPLOY_USER:-$(id -un)}}"
DEPLOY_GROUP="${KGSM_DEPLOY_GROUP:-$(id -gn)}"

# ── PROJECT BLOCK ─────────────────────────────────────────────────────────────
PROJECT="kgsm-api"

UNITS=("kgsm-api.service")
ENABLE_UNITS=("kgsm-api.service")

PREFIX="/opt/${PROJECT}"

ENV_DIR="/etc/${PROJECT}"
ENV_FILE="${ENV_DIR}/${PROJECT}.env"
ENV_EXAMPLE="${REPO_DIR}/deploy/${PROJECT}.env.example"

HEALTH_TRIES="${HEALTH_TRIES:-30}"

# This project's leaf config descriptor — the JSON declaring its full configurable surface, which
# kgsm-api reads to render the Control Panel's config page for this leaf. setup.sh creates the
# discovery directory; deploy.sh installs the file there unprivileged on every deploy, so the
# descriptor can never be older than the binary it describes. Format: tks/leaf-config-descriptor.md.
# Leave empty for a project that is not a leaf (nothing is installed and nothing is asserted).
LEAF_DESCRIPTOR="${REPO_DIR}/deploy/${PROJECT}.leaf.json"

# This project's own nginx server block, installed into /etc/nginx/conf.d/ by setup.sh when the
# host runs nginx. Each leaf ships its own vhost; the :80 ACME block and the certificate
# lifecycle are host-level and belong to no leaf.
NGINX_FRAGMENT="${REPO_DIR}/deploy/nginx/kgsm-api.conf"

# The leaf id kgsm-api knows this project by — the descriptor's "id", its filename stem in the
# discovery dir, and the {leaf} segment of the API's config route. Usually the project name minus
# the kgsm- prefix, but NOT always: kgsm-llm ships the leaf "assistant". State it, don't derive it.
LEAF_ID="${PROJECT#kgsm-}"

# The API's unit is host-neutral (User=/Group= are already the service user), so it installs
# verbatim. Substitution still runs so a host with a different service user works unchanged.
render_unit() {   # $1 = unit filename
    sed "s/^User=.*/User=${DEPLOY_USER}/; s/^Group=.*/Group=${DEPLOY_GROUP}/" \
        "${REPO_DIR}/deploy/$1"
}

# Where to prove the API actually serves. The bind comes from Api__Urls, which the operator
# env file overrides (this host serves HTTPS on :443 and plain HTTP on loopback:8097, while the
# unit's built-in default is :8080). Resolve it rather than hardcoding a port — a health check
# aimed at the wrong scheme or port fails a deploy that actually succeeded.
resolve_health_url() {
    local urls="" u hostport
    # systemd EnvironmentFile format (KEY=VALUE / # comments); a plain grep is the safe reader —
    # do NOT source it, it can hold values systemd parses but bash would mangle.
    if [[ -r "$ENV_FILE" ]]; then
        urls="$(grep -E '^[[:space:]]*Api__Urls=' "$ENV_FILE" 2>/dev/null \
                  | tail -n1 | cut -d= -f2- | tr -d '"'"'"' ')"
    fi
    [[ -n "$urls" ]] || urls="$(grep -E '^Environment=Api__Urls=' "${REPO_DIR}/deploy/${UNITS[0]}" \
                                  | tail -n1 | cut -d= -f3-)"
    # Prefer a plain-HTTP endpoint: the HTTPS one may carry a cert this host cannot verify locally.
    for u in ${urls//;/ }; do
        if [[ "$u" == http://* ]]; then
            hostport="${u#http://}"
            printf 'http://%s/health' "${hostport/0.0.0.0/127.0.0.1}"
            return 0
        fi
    done
    printf 'http://127.0.0.1:8080/health'
}

HEALTH_URL="${HEALTH_URL:-$(resolve_health_url)}"

health_probe() {
    curl -fsS -o /dev/null --max-time 2 "$HEALTH_URL" 2>/dev/null
}
# Runtime leaf-config wiring: the per-leaf systemd drop-ins + the scoped polkit rule that let the
# API APPLY per-leaf config at runtime (the Services panel). Privileged and one-shot, so it belongs
# in setup rather than in every deploy. setup-leaf-config.sh keeps its own drift guard: it refuses
# to install a drop-in for a unit its polkit template cannot restart, which is what keeps the leaf
# set in lockstep with LeafCatalog.cs.
setup_project_extras() {
    local script="$REPO_DIR/deploy/setup-leaf-config.sh"
    if [[ -x "$script" ]]; then
        log "wiring runtime leaf-config (drop-ins + scoped polkit restart grant) ..."
        SUDO="$SUDO" KGSM_API_USER="$DEPLOY_USER" "$script"
    else
        warn "deploy/setup-leaf-config.sh not found / not executable — skipping the leaf-config wiring."
        warn "runtime leaf configuration (the Services panel) stays inert; see deploy/leaf-config/README.md."
    fi
}
# ── END PROJECT BLOCK ─────────────────────────────────────────────────────────

# ── Derived paths (do not edit) ───────────────────────────────────────────────
# Where the REAL unit files live: a user-owned directory beside the project's config. systemd
# reaches them through a symlink at /etc/systemd/system/<unit> that setup.sh plants once. This
# is what lets deploy.sh update a unit with no sudo — it writes a file it owns, then asks
# systemd (via the polkit grant) to re-read it.
UNIT_DIR="${ENV_DIR}/systemd"
SYSTEMD_DIR="/etc/systemd/system"

# The polkit grant setup.sh installs: lets DEPLOY_USER drive systemctl for THIS project's units
# with no password and no interactive auth agent.
POLKIT_DST="/etc/polkit-1/rules.d/48-${PROJECT}-deploy.rules"

# The polkit rule's CONTENT is a committed file, not a heredoc, so what the host grants can be
# read and reviewed without running anything. Only the deploying user and the unit list cannot be
# known until install time, and those are the template's two placeholders.
POLKIT_TEMPLATE="${REPO_DIR}/deploy/polkit/48-${PROJECT}-deploy.rules.in"

render_polkit_rule() {
    [[ -f "$POLKIT_TEMPLATE" ]] || { err "missing polkit template: ${POLKIT_TEMPLATE}"; return 1; }

    local units_js="" u
    for u in "${UNITS[@]}"; do
        units_js+="        \"${u}\": true,"$'\n'
    done
    units_js="${units_js%$'\n'}"

    local rendered
    rendered="$(< "$POLKIT_TEMPLATE")"
    rendered="${rendered//@PROJECT@/${PROJECT}}"
    rendered="${rendered//@DEPLOY_USER@/${DEPLOY_USER}}"
    rendered="${rendered//@UNITS@/${units_js}}"
    printf '%s\n' "$rendered"
}

SERVICE="${UNITS[0]}"
PUBLISH_DIR="${REPO_DIR}/artifacts/publish"

# Where every leaf drops its config descriptor. Shared across projects and scanned by kgsm-api —
# the API holds no list of leaves, so a new leaf becomes configurable by landing a file here.
LEAF_DESCRIPTOR_DIR="${KGSM_LEAF_DESCRIPTOR_DIR:-/var/lib/kgsm/leaves}"
# Where this host declares who may do what — the Discord app, guild, role-lookup token and role map
# every KGSM surface authorizes against. One file, so a person cannot hold different authority on
# different surfaces. Each unit loads it before its own env file; setup.sh seeds it blank.
SHARED_AUTH_FILE="${KGSM_SHARED_AUTH_FILE:-/etc/kgsm/kgsm-auth.env}"

# The cluster secret every member on this host holds. One file rather than one copy per member: a
# machine can run more than one member of a cluster — a node and an auth anchor — and they must all
# hold the same secret or each concludes separately that it is not clustered. Each unit loads it
# before its own env file; setup.sh seeds it blank.
SHARED_CLUSTER_FILE="${KGSM_SHARED_CLUSTER_FILE:-/etc/kgsm/kgsm-cluster.env}"

# Where this host keeps its KGSM accounts — the store every surface on the box reads directly, so
# one person is one account whichever door they come through. A directory of its own rather than a
# file under /var/lib/kgsm: SQLite writes -wal/-shm BESIDE the database, so WAL needs write
# permission on the DIRECTORY, and /var/lib/kgsm itself is root-owned.
KGSM_AUTH_DIR="${KGSM_AUTH_DIR:-/var/lib/kgsm/auth}"

# Privileged-call indirection, used by setup.sh ONLY. deploy.sh never calls this. An automated
# run can set SUDO='sudo -A' + SUDO_ASKPASS=… to provision without an interactive prompt; no
# password is ever stored in the repo.
SUDO="${SUDO:-sudo}"

# ── Output helpers ────────────────────────────────────────────────────────────
log()  { printf '\033[1;34m>> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m** %s\033[0m\n' "$*" >&2; }
err()  { printf '\033[1;31m!! %s\033[0m\n' "$*" >&2; }

# ── Shared preflight ──────────────────────────────────────────────────────────

# Refuse to run as root. Both entry points build/publish as the invoking user so the source
# tree never gains root-owned obj/bin, and setup.sh templates the grants with a real user.
refuse_root() {
    if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
        err "do NOT run this as root — run it as the service-owning user."
        err "setup.sh sudo's the few steps that need it; deploy.sh needs no privilege at all."
        exit 1
    fi
}

# The contract deploy.sh enforces before it touches anything: this host has been provisioned.
# A missing piece means setup.sh has not run (or has been undone) — say so and stop, rather
# than half-deploying or blocking on a password prompt that will never be answered.
require_setup() {
    local u problem=0

    [[ -d "$PREFIX" && -w "$PREFIX" ]] || {
        err "install prefix ${PREFIX} is missing or not writable by $(id -un)."; problem=1; }
    [[ -d "$UNIT_DIR" && -w "$UNIT_DIR" ]] || {
        err "unit directory ${UNIT_DIR} is missing or not writable by $(id -un)."; problem=1; }

    for u in "${UNITS[@]}"; do
        if [[ ! -L "${SYSTEMD_DIR}/${u}" ]]; then
            err "${SYSTEMD_DIR}/${u} is not a symlink into ${UNIT_DIR}."; problem=1
        elif [[ "$(readlink -f "${SYSTEMD_DIR}/${u}")" != "${UNIT_DIR}/${u}" ]]; then
            err "${SYSTEMD_DIR}/${u} points at $(readlink "${SYSTEMD_DIR}/${u}"), not ${UNIT_DIR}/${u}."
            problem=1
        fi
    done

    if [[ "$problem" -ne 0 ]]; then
        err ""
        err "this host is not provisioned for headless deploys of ${PROJECT}."
        err "run ONCE (it will ask for your sudo password):   ${REPO_DIR}/deploy/setup.sh"
        exit 1
    fi
}

# systemctl, unprivileged, via the polkit grant setup.sh installed. A denial here means the
# grant is missing — surface that as the actionable thing it is instead of a raw polkit error.
sysctl_do() {   # $@ = systemctl arguments
    # --no-ask-password: this path must fail fast rather than block on a prompt nobody will answer.
    if ! systemctl --no-ask-password "$@"; then
        err "systemctl $* was refused."
        err "the polkit grant for ${DEPLOY_USER} is missing or does not cover this unit."
        err "re-run: ${REPO_DIR}/deploy/setup.sh"
        return 1
    fi
}

# Poll health_probe until it passes. Used inside an `if`, so a failing probe never trips ERR.
wait_health() {
    local i
    for ((i = 1; i <= HEALTH_TRIES; i++)); do
        health_probe && return 0
        sleep 1
    done
    return 1
}

# Write the rendered units into UNIT_DIR (which we own — no privilege). Sets UNIT_CHANGED=1
# when any unit's content actually changed, so the caller can daemon-reload only when needed.
UNIT_CHANGED=0
install_units_unprivileged() {
    local u tmp
    UNIT_CHANGED=0
    for u in "${UNITS[@]}"; do
        tmp="$(mktemp)"
        render_unit "$u" > "$tmp"
        if ! cmp -s "$tmp" "${UNIT_DIR}/${u}"; then
            log "unit changed → ${UNIT_DIR}/${u}"
            install -m 0644 "$tmp" "${UNIT_DIR}/${u}"
            UNIT_CHANGED=1
        fi
        rm -f "$tmp"
    done
}

# Install this project's leaf config descriptor into the shared discovery directory. Unprivileged:
# the directory is owned by DEPLOY_USER (setup.sh created it), so this is a plain file write.
#
# A project with no descriptor file is simply not a leaf — nothing is installed and nothing fails.
# When the file IS present the descriptor is validated before it lands, because kgsm-api skips a
# malformed one silently: catching it here is the difference between "the panel has no page for
# this leaf" and knowing why.
install_leaf_descriptor() {
    [[ -n "${LEAF_DESCRIPTOR:-}" && -f "$LEAF_DESCRIPTOR" ]] || return 0

    local dst="${LEAF_DESCRIPTOR_DIR}/${LEAF_ID}.json"

    # Validate what we can before it lands: it must parse, and its "id" must be the id this
    # project deploys under — a mismatch would install the file under a name kgsm-api then reads
    # back as a different leaf.
    if command -v python3 >/dev/null 2>&1; then
        if ! python3 - "$LEAF_DESCRIPTOR" "$LEAF_ID" <<'PY'
import json, sys
path, want = sys.argv[1], sys.argv[2]
try:
    d = json.load(open(path))
except Exception as e:
    sys.exit(f"{path} is not valid JSON: {e}")
if d.get("id") != want:
    sys.exit(f"{path} declares id={d.get('id')!r}, but this project deploys leaf id {want!r}.")
PY
        then
            err "refusing to install the leaf descriptor — kgsm-api would skip it and the"
            err "Control Panel would show no configuration for ${PROJECT}."
            return 1
        fi
    fi

    if [[ ! -d "$LEAF_DESCRIPTOR_DIR" ]]; then
        err "leaf descriptor directory ${LEAF_DESCRIPTOR_DIR} is missing."
        err "run ONCE (it will ask for your sudo password):   ${REPO_DIR}/deploy/setup.sh"
        return 1
    fi

    if ! cmp -s "$LEAF_DESCRIPTOR" "$dst"; then
        log "leaf descriptor changed → ${dst}"
        install -m 0644 "$LEAF_DESCRIPTOR" "$dst"
    fi
}
