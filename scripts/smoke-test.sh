#!/usr/bin/env bash
# End-to-end check of the running stack (S1 → S2 → webhook → FR-26):
#   docker compose up -d --build && scripts/smoke-test.sh
# Buys a product in the demo shop, pays the invoice with the scripted payer, waits until the payment page says
# "paid" and the shop got the webhook, then checks that a forged webhook is rejected. Exit code 0 = all good.
set -euo pipefail
cd "$(dirname "$0")/.."
shop="${SHOP_URL:-http://localhost:5080}"
service="${SERVICE_URL:-http://localhost:5052}"

wait_for() { # url, seconds
  for _ in $(seq 1 "$2"); do curl -fsS -o /dev/null "$1" 2>/dev/null && return 0; sleep 1; done
  echo "✗ $1 not ready after $2 s" >&2; return 1
}
json() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

echo "… waiting for the stack"
wait_for "$service/health/ready" 300
wait_for "$shop/health" 120

echo "… S1: buying a T-shirt in the demo shop"
pay_url=$(curl -fsS -o /dev/null -w '%{redirect_url}' -X POST -d 'product=tshirt' "$shop/checkout")
[[ "$pay_url" == "$service/pay/"* ]] || { echo "✗ checkout did not redirect to the payment page (got '$pay_url')" >&2; exit 1; }
token="${pay_url##*/}"
curl -fsS -o /dev/null "$pay_url" && echo "✓ invoice created, payment page opens: $pay_url"

echo "… S2: paying with the scripted payer"
scripts/pay.sh "$pay_url"

status=""
for _ in $(seq 1 90); do
  status=$(curl -fsS "$service/api/public/invoices/$token" | json 'd["status"]')
  [ "$status" = "paid" ] && break
  sleep 1
done
[ "$status" = "paid" ] || { echo "✗ payment page status is '$status', expected paid" >&2; exit 1; }
echo "✓ payment page shows paid"

order=""
for _ in $(seq 1 60); do
  order=$(curl -fsS "$shop/api/orders" | json "next((o['id']+' '+o['status'] for o in d if o.get('paymentPageUrl','').endswith('$token')), '')")
  [[ "$order" == *" paid" ]] && break
  sleep 1
done
[[ "$order" == *" paid" ]] || { echo "✗ shop order is '$order', expected paid (webhook not received?)" >&2; exit 1; }
echo "✓ shop received the webhook: order ${order% *} is paid"

code=$(curl -sS -X POST "$shop/demo/attacks/forged" | json 'd["statusCode"]')
[ "$code" = "400" ] || { echo "✗ forged webhook answered $code, expected 400" >&2; exit 1; }
echo "✓ forged webhook rejected (400)"
echo "All good."
