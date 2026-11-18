#!/usr/bin/env bash
# Scripted payer (TEST ONLY, local chain). Runs in the compose stack's `payer` container if Docker is available,
# otherwise with `dotnet run` against localhost (stack started without Docker).
#   scripts/pay.sh http://localhost:5052/pay/<token> [--amount X] [--split N] [--then-reorg]
#   scripts/pay.sh reorg 3 | mine 5 | warp 3600 | balance | --to <address> --amount 1
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "${PAY_USE_DOTNET:-}" != "1" ] && command -v docker >/dev/null 2>&1 && docker compose ps --status running anvil 2>/dev/null | grep -q anvil; then
  exec docker compose run --rm payer "$@"
fi
exec dotnet run --project src/AcceptCrypto.Payer -- "$@"
