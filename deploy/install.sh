#!/usr/bin/env bash
#
# Build, publish, and install the kgsm-api service. Run as root.
#
#   sudo ./deploy/install.sh            # publish + install binary + unit (does NOT enable)
#   sudo ./deploy/install.sh --enable   # the above, then `systemctl enable --now`
#
# Idempotent: re-running republishes and replaces the binary/unit in place. The env file at
# /etc/kgsm-api/kgsm-api.env is created from the template only if absent (never overwritten),
# so your secrets survive a reinstall. The DB (StateDirectory=/var/lib/kgsm-api) is untouched.
#
set -euo pipefail

PREFIX="/opt/kgsm-api"
UNIT_DST="/etc/systemd/system/kgsm-api.service"
ENV_DIR="/etc/kgsm-api"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_DIR/src/Api/Api.csproj"
UNIT_SRC="$REPO_DIR/deploy/kgsm-api.service"
ENV_EXAMPLE="$REPO_DIR/deploy/kgsm-api.env.example"
PUBLISH_DIR="$REPO_DIR/artifacts/publish"
RID="${RID:-linux-x64}"
SVC_USER="${KGSM_API_USER:-heisen}"

ENABLE=0
[[ "${1:-}" == "--enable" ]] && ENABLE=1

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
    echo "error: must run as root (writes ${PREFIX}, ${UNIT_DST}, ${ENV_DIR})" >&2
    exit 1
fi

echo ">> Publishing self-contained (${RID})..."
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained -p:PublishReadyToRun=true -o "$PUBLISH_DIR"

echo ">> Installing binary tree to ${PREFIX}..."
install -d "$PREFIX"
rm -rf "${PREFIX:?}"/*
cp -a "$PUBLISH_DIR/." "$PREFIX/"

echo ">> Installing systemd unit to ${UNIT_DST}..."
install -m 0644 "$UNIT_SRC" "$UNIT_DST"

echo ">> Ensuring env file ${ENV_DIR}/kgsm-api.env (template if absent)..."
install -d "$ENV_DIR"
if [[ ! -f "$ENV_DIR/kgsm-api.env" ]]; then
    install -m 0640 -g "$SVC_USER" "$ENV_EXAMPLE" "$ENV_DIR/kgsm-api.env"
    echo "   created from template — EDIT IT (set KGSM_API_AUTH_SIGNING_KEY + Discord creds)"
fi

systemctl daemon-reload

if [[ "$ENABLE" -eq 1 ]]; then
    echo ">> Enabling and starting kgsm-api..."
    systemctl enable --now kgsm-api
    systemctl --no-pager status kgsm-api || true
else
    cat <<EOF

Installed. The service is NOT running yet. Before starting, edit:

    ${ENV_DIR}/kgsm-api.env      # set KGSM_API_AUTH_SIGNING_KEY (openssl rand -base64 48) + Discord

Then:

    systemctl enable --now kgsm-api
    curl http://127.0.0.1:8080/health

EOF
fi
