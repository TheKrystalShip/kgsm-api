#!/usr/bin/env bash
#
# certbot deploy-hook for kgsm-api — make a renewed Let's Encrypt cert readable by the
# NON-root service, then restart it so Kestrel picks up the new cert.
#
# Why this is needed: kgsm-api terminates TLS in Kestrel itself (there is NO reverse proxy)
# and runs as a non-root user, but certbot writes /etc/letsencrypt/{live,archive} root-only —
# so the service cannot read privkey.pem directly. This hook copies fullchain.pem + privkey.pem
# into a service-readable dir (KGSM_API_TLS_DIR, default /etc/kgsm-api/tls, owned by the service
# user, 0640) that the env file points Kestrel at, then bounces the unit.
#
# Install it as a certbot DEPLOY hook (runs after every successful issuance/renewal):
#   sudo install -m 0755 deploy/certbot-deploy-hook.sh \
#        /etc/letsencrypt/renewal-hooks/deploy/kgsm-api.sh
# …or pass it once at issuance:
#   sudo certbot certonly --standalone -d kgsm.thekrystalship.com \
#        --deploy-hook /opt/kgsm-api-deploy/certbot-deploy-hook.sh
#
# Overridable via the environment (defaults target this host):
#   KGSM_API_TLS_DOMAIN  cert domain to act on            (default kgsm.thekrystalship.com)
#   KGSM_API_SERVICE     systemd unit to restart          (default kgsm-api)
#   KGSM_API_TLS_USER    owner of the copied cert+key     (default heisen)
#   KGSM_API_TLS_GROUP   group of the copied cert+key     (default = KGSM_API_TLS_USER)
#   KGSM_API_TLS_DIR     service-readable destination dir (default /etc/kgsm-api/tls)
set -euo pipefail

DOMAIN="${KGSM_API_TLS_DOMAIN:-kgsm.thekrystalship.com}"
SERVICE="${KGSM_API_SERVICE:-kgsm-api}"
SERVICE_USER="${KGSM_API_TLS_USER:-heisen}"
SERVICE_GROUP="${KGSM_API_TLS_GROUP:-$SERVICE_USER}"
TLS_DIR="${KGSM_API_TLS_DIR:-/etc/kgsm-api/tls}"

# certbot sets RENEWED_LINEAGE (= /etc/letsencrypt/live/<domain>) when it invokes a deploy hook;
# fall back to the well-known live path for a manual run.
LINEAGE="${RENEWED_LINEAGE:-/etc/letsencrypt/live/$DOMAIN}"

# As a /etc/letsencrypt/renewal-hooks/deploy/ hook this fires for EVERY renewed cert on the host,
# so only act on our own domain (a no-op exit for any other lineage).
if [[ "$(basename "$LINEAGE")" != "$DOMAIN" ]]; then
    exit 0
fi

if [[ ! -r "$LINEAGE/privkey.pem" ]]; then
    echo "certbot-deploy-hook: $LINEAGE/privkey.pem not found/readable — run as root." >&2
    exit 1
fi

install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$TLS_DIR"
install -m 0640 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$LINEAGE/fullchain.pem" "$TLS_DIR/fullchain.pem"
install -m 0640 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$LINEAGE/privkey.pem"   "$TLS_DIR/privkey.pem"

systemctl restart "$SERVICE"
echo "certbot-deploy-hook: installed $DOMAIN cert into $TLS_DIR and restarted $SERVICE."
