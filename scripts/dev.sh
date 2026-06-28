#!/usr/bin/env bash
# dev.sh — run kgsm-api with the DEV profile: auth DISABLED, CORS open to the
# kgsm-web dev server (:5173), assistant relay wired, on :8090, with an isolated
# dev DB. Pairs with kgsm-web's .env.development (VITE_API_BASE=:8090) + its
# DEV-only seed auto-connect, so `./scripts/dev.sh` here + `npm run dev` there
# boot the whole stack with zero manual configuration.
#
#   Usage:  ./scripts/dev.sh
#   Every KGSM_API_* below is overridable from the environment (export then run).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UMBRELLA="$(dirname "$REPO")"
DEVDIR="$REPO/.dev"
mkdir -p "$DEVDIR"

# Assistant relay secret — needed for the chat/assistant to work. NEVER committed:
# read from the host's existing config (api env first, then the assistant env).
SECRET="${KGSM_API_ASSISTANT_RELAY_SECRET:-}"
[ -z "$SECRET" ] && SECRET="$(sed -n 's/^KGSM_API_ASSISTANT_RELAY_SECRET=//p' /etc/kgsm-api/kgsm-api.env 2>/dev/null | head -n1)"
[ -z "$SECRET" ] && SECRET="$(sed -n 's/^Assistant__Relay__Secret=//p' /etc/kgsm-assistant/service.env 2>/dev/null | head -n1)"

# kgsm engine: prefer the sibling dev checkout in the umbrella, else the installed one.
KGSM="${KGSM_API_KGSM_PATH:-}"
[ -z "$KGSM" ] && [ -x "$UMBRELLA/kgsm/kgsm.sh" ] && KGSM="$UMBRELLA/kgsm/kgsm.sh"
[ -z "$KGSM" ] && KGSM="/usr/local/bin/kgsm"

export ASPNETCORE_ENVIRONMENT=Development
export KGSM_API_URLS="${KGSM_API_URLS:-http://127.0.0.1:8090}"
export KGSM_API_AUTH_DISABLED=1
export KGSM_API_CORS_ORIGINS="${KGSM_API_CORS_ORIGINS:-http://localhost:5173,http://127.0.0.1:5173}"
export KGSM_API_ASSISTANT_URL="${KGSM_API_ASSISTANT_URL:-http://127.0.0.1:5180}"
export KGSM_API_ASSISTANT_RELAY_SECRET="$SECRET"
export KGSM_API_KGSM_PATH="$KGSM"
export KGSM_API_HOST_LABEL="${KGSM_API_HOST_LABEL:-dev}"
export KGSM_API_DB="${KGSM_API_DB:-$DEVDIR/kgsm-api.db}"

asst="off (no relay secret found → chat disabled)"
[ -n "$SECRET" ] && asst="$KGSM_API_ASSISTANT_URL (secret loaded)"
echo "── kgsm-api DEV ──────────────────────────────────────────"
echo "  bind        : $KGSM_API_URLS   (auth DISABLED → synthetic admin)"
echo "  cors        : $KGSM_API_CORS_ORIGINS"
echo "  kgsm engine : $KGSM"
echo "  assistant   : $asst"
echo "  db          : $KGSM_API_DB"
echo "──────────────────────────────────────────────────────────"

exec dotnet run --project "$REPO/src/Api/Api.csproj"
