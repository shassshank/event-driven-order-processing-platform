#!/usr/bin/env bash
set -euo pipefail

GATEWAY_URL="${GATEWAY_URL:-http://localhost:8090}"
API_KEY="${DEMO_API_KEY:-local-dev-key}"
POLL_SECONDS="${POLL_SECONDS:-45}"
POLL_INTERVAL_SECONDS="${POLL_INTERVAL_SECONDS:-2}"

if ! command -v jq >/dev/null 2>&1; then
  echo "ERROR: jq is required for the live demo script." >&2
  exit 2
fi

health_status=$(curl -sS -o /tmp/live-demo-gateway-health.json -w '%{http_code}' "$GATEWAY_URL/health" || true)
if [[ "$health_status" != "200" ]]; then
  echo "ERROR: ApiGateway health failed at $GATEWAY_URL/health with HTTP $health_status" >&2
  cat /tmp/live-demo-gateway-health.json >&2 || true
  exit 1
fi

client_request_id="live-demo-$(date +%Y%m%d%H%M%S)-$RANDOM"
request_body=$(cat <<JSON
{
  "customerId": "22222222-2222-2222-2222-222222222222",
  "clientRequestId": "$client_request_id",
  "currency": "USD",
  "items": [
    {
      "productId": "33333333-3333-3333-3333-333333333333",
      "quantity": 2,
      "unitPrice": 12.34
    }
  ]
}
JSON
)

create_response="/tmp/live-demo-create-order.json"
create_status=$(curl -sS -o "$create_response" -w '%{http_code}' \
  -X POST "$GATEWAY_URL/api/orders" \
  -H 'Content-Type: application/json' \
  -H "X-Demo-Api-Key: $API_KEY" \
  -H "X-Correlation-ID: 11111111-1111-1111-1111-111111111111" \
  -d "$request_body" || true)

if [[ "$create_status" != "201" ]]; then
  echo "ERROR: create order expected HTTP 201 but got $create_status" >&2
  cat "$create_response" >&2 || true
  exit 1
fi

order_id=$(jq -r '.id // .orderId // empty' "$create_response")
if [[ -z "$order_id" || "$order_id" == "null" ]]; then
  echo "ERROR: create-order response did not contain id/orderId" >&2
  cat "$create_response" >&2
  exit 1
fi

echo "========================================================================"
echo "LIVE DEMO RUN"
echo "========================================================================"
echo "Gateway:          $GATEWAY_URL"
echo "OrderId:          $order_id"
echo "ClientRequestId:  $client_request_id"
echo "========================================================================"
echo "Waiting for reporting projection to reach Completed..."

completed="false"
last_status=""
end_time=$((SECONDS + POLL_SECONDS))
while (( SECONDS < end_time )); do
  report=$(curl -sS "$GATEWAY_URL/api/reports/orders/$order_id" || true)
  status_value=$(printf '%s' "$report" | jq -r '.status // empty' 2>/dev/null || true)
  if [[ -n "$status_value" && "$status_value" != "$last_status" ]]; then
    echo "Reporting status: $status_value"
    last_status="$status_value"
  fi
  if [[ "$status_value" == "Completed" ]]; then
    completed="true"
    break
  fi
  sleep "$POLL_INTERVAL_SECONDS"
done

if [[ "$completed" != "true" ]]; then
  echo "ERROR: order did not reach Completed in reporting within ${POLL_SECONDS}s" >&2
  echo "Order response:"
  curl -sS "$GATEWAY_URL/api/orders/$order_id" | jq . >&2 || true
  echo "Reporting events:"
  curl -sS "$GATEWAY_URL/api/reports/orders/$order_id/events" | jq . >&2 || true
  exit 1
fi

notification_count=$(curl -sS "$GATEWAY_URL/api/notifications/orders/$order_id" | jq 'length')

echo
echo "Order completed. Projections are ready."
echo
echo "Demo commands:"
echo "curl -s $GATEWAY_URL/api/orders/$order_id | jq"
echo "curl -s $GATEWAY_URL/api/payments/orders/$order_id | jq"
echo "curl -s $GATEWAY_URL/api/notifications/orders/$order_id | jq"
echo "curl -s $GATEWAY_URL/api/reports/orders/$order_id | jq"
echo "curl -s $GATEWAY_URL/api/reports/orders/$order_id/events | jq"
echo "python3 scripts/dlq-inspect.py"
echo
echo "Browser URLs:"
echo "$GATEWAY_URL/health"
echo "$GATEWAY_URL/api/gateway/routes"
echo "http://localhost:15672  (guest / guest)"
echo
echo "========================================================================"
echo "LIVE DEMO READY"
echo "OrderId: $order_id"
echo "Notifications: $notification_count"
echo "========================================================================"
