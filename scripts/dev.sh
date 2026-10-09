#!/usr/bin/env bash
# dev.sh — run kgsm-api with the DEV profile: auth DISABLED, CORS open to the
# kgsm-web dev server (:5173), on :8090, with an isolated dev DB. Pairs with
# kgsm-web's .env.development (VITE_API_BASE=:8090) + its DEV-only seed
# auto-connect, so `./scripts/dev.sh` here + `npm run dev` there boot the whole
# stack with zero manual configuration.
#
#   Usage:  ./scripts/dev.sh
#   Every Api__* below is overridable from the environment (export then run).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UMBRELLA="$(dirname "$REPO")"
DEVDIR="$REPO/.dev"
mkdir -p "$DEVDIR"

# kgsm engine: prefer the sibling dev checkout in the umbrella, else the installed one.
KGSM="${Api__KgsmPath:-}"
[ -z "$KGSM" ] && [ -x "$UMBRELLA/kgsm/kgsm.sh" ] && KGSM="$UMBRELLA/kgsm/kgsm.sh"
[ -z "$KGSM" ] && KGSM="/usr/local/bin/kgsm"

export ASPNETCORE_ENVIRONMENT=Development
export Api__Urls="${Api__Urls:-http://127.0.0.1:8090}"
export Api__AuthDisabled=true
export Api__KgsmPath="$KGSM"
export Api__HostLabel="${Api__HostLabel:-dev}"
export Api__DbPath="${Api__DbPath:-$DEVDIR/kgsm-api.db}"

echo "── kgsm-api DEV ──────────────────────────────────────────"
echo "  bind        : $Api__Urls   (auth DISABLED → synthetic Owner, any origin)"
echo "  kgsm engine : $KGSM"
echo "  db          : $Api__DbPath"
echo "──────────────────────────────────────────────────────────"

exec dotnet run --project "$REPO/src/Api/Api.csproj"
