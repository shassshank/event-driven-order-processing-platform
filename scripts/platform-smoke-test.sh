#!/usr/bin/env bash
set -euo pipefail

GATEWAY_URL="${GATEWAY_URL:-http://localhost:8090}"
ORDER_SERVICE_URL="${ORDER_SERVICE_URL:-http://localhost:8081}"
INVENTORY_SERVICE_URL="${INVENTORY_SERVICE_URL:-http://localhost:8082}"
PAYMENT_SERVICE_URL="${PAYMENT_SERVICE_URL:-http://localhost:8083}"
NOTIFICATION_SERVICE_URL="${NOTIFICATION_SERVICE_URL:-http://localhost:8084}"
REPORTING_SERVICE_URL="${REPORTING_SERVICE_URL:-http://localhost:8085}"
API_KEY="${DEMO_API_KEY:-local-dev-key}"
POLL_SECONDS="${POLL_SECONDS:-45}"
POLL_INTERVAL_SECONDS="${POLL_INTERVAL_SECONDS:-2}"

if ! command -v jq >/dev/null 2>&1; then
  echo "ERROR: jq is required for the smoke test." >&2
  exit 2
fi

check_health() {
  local name="$1"
  local url="$2"
  local status
  status=$(curl -sS -o /tmp/platform-smoke-health.json -w '%{http_code}' "$url/health" || true)
  if [[ "$status" != "200" ]]; then
    echo "ERROR: $name health failed at $url/health with HTTP $status" >&2
    cat /tmp/platform-smoke-health.json >&2 || true
    exit 1
  fi
  echo "OK: $name health is 200"
}

post_json() {
  local url="$1"
  local body="$2"
  local output_file="$3"
  local status
  status=$(curl -sS -o "$output_file" -w '%{http_code}' \
    -X POST "$url" \
    -H 'Content-Type: application/json' \
    -H "X-Demo-Api-Key: $API_KEY" \
    -H 'X-Correlation-ID: 11111111-1111-1111-1111-111111111111' \
    -d "$body" || true)
  echo "$status"
}

assert_http_status() {
  local actual="$1"
  local expected="$2"
  local context="$3"
  local body_file="$4"
  if [[ "$actual" != "$expected" ]]; then
    echo "ERROR: $context expected HTTP $expected but got $actual" >&2
    cat "$body_file" >&2 || true
    exit 1
  fi
}

echo "========================================================================"
echo "Platform smoke test"
echo "Gateway: $GATEWAY_URL"
echo "========================================================================"

check_health "ApiGateway" "$GATEWAY_URL"
check_health "OrderService" "$ORDER_SERVICE_URL"
check_health "InventoryService" "$INVENTORY_SERVICE_URL"
check_health "PaymentService" "$PAYMENT_SERVICE_URL"
check_health "NotificationService" "$NOTIFICATION_SERVICE_URL"
check_health "ReportingService" "$REPORTING_SERVICE_URL"

client_request_id="phase9-smoke-$(date +%Y%m%d%H%M%S)-$RANDOM"
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

create_response="/tmp/platform-smoke-create-order.json"
status=$(post_json "$GATEWAY_URL/api/orders" "$request_body" "$create_response")
assert_http_status "$status" "201" "create order through gateway" "$create_response"

order_id=$(jq -r '.id // .orderId // empty' "$create_response")
if [[ -z "$order_id" || "$order_id" == "null" ]]; then
  echo "ERROR: create-order response did not contain id/orderId" >&2
  cat "$create_response" >&2
  exit 1
fi

echo "OK: order created through gateway: $order_id"

completed="false"
end_time=$((SECONDS + POLL_SECONDS))
while (( SECONDS < end_time )); do
  report=$(curl -sS "$GATEWAY_URL/api/reports/orders/$order_id" || true)
  status_value=$(printf '%s' "$report" | jq -r '.status // empty' 2>/dev/null || true)
  if [[ "$status_value" == "Completed" ]]; then
    completed="true"
    break
  fi
  sleep "$POLL_INTERVAL_SECONDS"
done

if [[ "$completed" != "true" ]]; then
  echo "ERROR: order did not reach Completed in reporting within ${POLL_SECONDS}s" >&2
  curl -sS "$GATEWAY_URL/api/reports/orders/$order_id" | jq . >&2 || true
  curl -sS "$GATEWAY_URL/api/reports/orders/$order_id/events" | jq . >&2 || true
  exit 1
fi

echo "OK: reporting projection reached Completed"

notification_count=$(curl -sS "$GATEWAY_URL/api/notifications/orders/$order_id" | jq 'length')
if (( notification_count < 1 )); then
  echo "ERROR: expected at least one notification for order $order_id" >&2
  exit 1
fi

echo "OK: notifications exist for order ($notification_count rows)"

unauth_response="/tmp/platform-smoke-unauthorized.json"
unauth_status=$(curl -sS -o "$unauth_response" -w '%{http_code}' \
  -X POST "$GATEWAY_URL/api/orders" \
  -H 'Content-Type: application/json' \
  -d '{}' || true)
assert_http_status "$unauth_status" "401" "unauthenticated create order" "$unauth_response"

echo "OK: gateway rejects unauthenticated mutation"

echo "========================================================================"
echo "SMOKE TEST PASSED"
echo "OrderId: $order_id"
echo "ClientRequestId: $client_request_id"
echo "Notifications: $notification_count"
echo "========================================================================"
