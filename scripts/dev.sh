#!/usr/bin/env bash
# dev.sh — run kgsm-api with the DEV profile: auth DISABLED, CORS open to the
# kgsm-web dev server (:5173), assistant relay wired, on :8090, with an isolated
# dev DB. Pairs with kgsm-web's .env.development (VITE_API_BASE=:8090) + its
# DEV-only seed auto-connect, so `./scripts/dev.sh` here + `npm run dev` there
# boot the whole stack with zero manual configuration.
#
#   Usage:  ./scripts/dev.sh
#   Every Api__* below is overridable from the environment (export then run).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UMBRELLA="$(dirname "$REPO")"
DEVDIR="$REPO/.dev"
mkdir -p "$DEVDIR"

# Assistant relay secret — needed for the chat/assistant to work. NEVER committed:
# read from the host's existing config (api env first, then the assistant env).
SECRET="${Api__AssistantRelaySecret:-}"
[ -z "$SECRET" ] && SECRET="$(sed -n 's/^Api__AssistantRelaySecret=//p' /etc/kgsm-api/kgsm-api.env 2>/dev/null | head -n1)"
[ -z "$SECRET" ] && SECRET="$(sed -n 's/^Assistant__Relay__Secret=//p' /etc/kgsm-assistant/service.env 2>/dev/null | head -n1)"

# kgsm engine: prefer the sibling dev checkout in the umbrella, else the installed one.
KGSM="${Api__KgsmPath:-}"
[ -z "$KGSM" ] && [ -x "$UMBRELLA/kgsm/kgsm.sh" ] && KGSM="$UMBRELLA/kgsm/kgsm.sh"
[ -z "$KGSM" ] && KGSM="/usr/local/bin/kgsm"

export ASPNETCORE_ENVIRONMENT=Development
export Api__Urls="${Api__Urls:-http://127.0.0.1:8090}"
export Api__AuthDisabled=true
export Api__CorsOrigins="${Api__CorsOrigins:-http://localhost:5173,http://127.0.0.1:5173}"
export Api__AssistantBaseUrl="${Api__AssistantBaseUrl:-http://127.0.0.1:5180}"
export Api__AssistantRelaySecret="$SECRET"
export Api__KgsmPath="$KGSM"
export Api__HostLabel="${Api__HostLabel:-dev}"
export Api__DbPath="${Api__DbPath:-$DEVDIR/kgsm-api.db}"

asst="off (no relay secret found → chat disabled)"
[ -n "$SECRET" ] && asst="$Api__AssistantBaseUrl (secret loaded)"
echo "── kgsm-api DEV ──────────────────────────────────────────"
echo "  bind        : $Api__Urls   (auth DISABLED → synthetic admin)"
echo "  cors        : $Api__CorsOrigins"
echo "  kgsm engine : $KGSM"
echo "  assistant   : $asst"
echo "  db          : $Api__DbPath"
echo "──────────────────────────────────────────────────────────"

exec dotnet run --project "$REPO/src/Api/Api.csproj"
