#!/usr/bin/env bash
# FR-26 demo: makes the demo shop send itself a bad webhook and prints how its SDK endpoint answered.
#   scripts/webhook-attack.sh forged    # made-up invoice.paid signed with a guessed secret        → 400
#   scripts/webhook-attack.sh tampered  # real webhook with the status changed, original signature → 400
#   scripts/webhook-attack.sh replay    # real webhook resent byte for byte → duplicate (200, not applied) within 5 min, 400 after
#   scripts/webhook-attack.sh stale     # real webhook captured 10 min ago, replayed now           → 400
# Or by hand, straight to the webhook endpoint (no valid signature → 400):
#   curl -i -X POST http://localhost:5080/webhooks/acceptcrypto -H 'AcceptCrypto-Timestamp: '"$(date +%s)" \
#        -H 'AcceptCrypto-Signature: v1=deadbeef' -H 'content-type: application/json' -d '{"id":"x"}'
# The result also appears in the shop's webhook log: http://localhost:5080/webhooks
set -euo pipefail
kind="${1:-forged}"
shop="${SHOP_URL:-http://localhost:5080}"
curl -sS -X POST "$shop/demo/attacks/$kind"
echo
