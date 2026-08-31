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
# Knobs: RID, HEALTH_URL, HEALTH_TRIES.
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

PROJECT_CSPROJ="$REPO_DIR/src/Api/Api.csproj"
RID="${RID:-linux-x64}"

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
# Every TheKrystalShip.* package restores from the org's GitHub Packages feed (nuget.config), so
# this repo builds with no sibling checkout present. The feed needs a token: a workstation keeps one
# in ~/.nuget/NuGet/NuGet.Config, CI uses the automatic GITHUB_TOKEN.

log "publishing framework-dependent single-file (${RID}) → ${PUBLISH_DIR}"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT_CSPROJ" -c Release -r "$RID" --no-self-contained -o "$PUBLISH_DIR"

# The Control Panel is not this repo's to publish, and does not have to live here at all: it is a
# static artifact its own repo builds and installs wherever it is served from, which need not be a
# node. So wwwroot is excluded from the sync below and this deploy neither fills it nor empties it.
#
# The exclusion is what makes both states survive a deploy. The publish tree is rebuilt from empty
# every time, so without it the prune would read "no wwwroot here" as "delete whatever is there" —
# and a host that does serve a panel would lose it to a deploy of the API. A host that serves none
# keeps an empty directory, which this API answers 404 from rather than pretending a panel is
# missing.
SPA_SYNC_EXCLUDES=(--exclude='/wwwroot/')

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
