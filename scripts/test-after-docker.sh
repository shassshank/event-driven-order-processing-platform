#!/usr/bin/env bash
set -o pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS_DIR="$ROOT_DIR/TestResults/docker-$(date +%Y%m%d-%H%M%S)"
LOG_FILE="$RESULTS_DIR/dotnet-test-docker.log"
mkdir -p "$RESULTS_DIR"

RUN_LIVE_STACK=1
INCLUDE_LIVE_DEMO=0
EXTRA_ARGS=()

for arg in "$@"; do
  case "$arg" in
    --dotnet-only|--skip-live-stack)
      RUN_LIVE_STACK=0
      ;;
    --include-live-demo)
      INCLUDE_LIVE_DEMO=1
      ;;
    *)
      EXTRA_ARGS+=("$arg")
      ;;
  esac
done

EXIT_CODE=0

run_project() {
  local project="$1"
  shift || true

  echo
  echo "========================================================================"
  echo "DOCKER TEST PROJECT: $project"
  echo "========================================================================"

  local cmd=(dotnet test "$ROOT_DIR/$project" --logger "trx" --results-directory "$RESULTS_DIR")

  if [[ $# -gt 0 ]]; then
    cmd+=("$@")
  fi

  if [[ ${#EXTRA_ARGS[@]} -gt 0 ]]; then
    cmd+=("${EXTRA_ARGS[@]}")
  fi

  "${cmd[@]}" 2>&1 | tee -a "$LOG_FILE"

  local code=${PIPESTATUS[0]}
  if [[ $code -ne 0 ]]; then
    EXIT_CODE=$code
  fi
}

cat <<HEADER
========================================================================
Docker-required test lane
========================================================================
This lane requires Docker. It runs Testcontainers-backed tests and,
by default, live Docker Compose platform checks.

Results directory: $RESULTS_DIR
Console log:       $LOG_FILE
========================================================================
HEADER

if ! docker info >/dev/null 2>&1; then
  echo "ERROR: Docker daemon is not reachable. Start Docker Desktop or a Docker engine first." >&2
  exit 2
fi

run_project "tests/EventBus.RabbitMQ.Tests/EventBus.RabbitMQ.Tests.csproj" --filter "Category=Docker"
run_project "tests/OrderService.IntegrationTests/OrderService.IntegrationTests.csproj" --filter "Category=Docker"

python3 "$ROOT_DIR/scripts/summarize-test-results.py" "$RESULTS_DIR" "$EXIT_CODE" "$LOG_FILE"
SUMMARY_EXIT_CODE=$?
if [[ $SUMMARY_EXIT_CODE -ne 0 ]]; then
  exit "$SUMMARY_EXIT_CODE"
fi

if [[ $RUN_LIVE_STACK -eq 0 ]]; then
  echo
  echo "Docker/Testcontainers tests passed. Live stack checks were skipped."
  exit 0
fi

GATEWAY_URL="${GATEWAY_URL:-http://localhost:8090}"
if ! curl -fsS "$GATEWAY_URL/health" >/dev/null 2>&1; then
  cat >&2 <<MESSAGE
ERROR: Live platform stack is not reachable at $GATEWAY_URL/health.

Start it first:
  docker compose -f deploy/docker-compose.yml up -d --build \\
    postgres redis rabbitmq \\
    order-service inventory-service payment-service \\
    notification-service reporting-service api-gateway

Then run:
  bash scripts/test-after-docker.sh

To run only the Testcontainers-backed dotnet tests, use:
  bash scripts/test-after-docker.sh --dotnet-only
MESSAGE
  exit 3
fi

echo
echo "========================================================================"
echo "LIVE DOCKER COMPOSE CHECKS"
echo "========================================================================"

bash "$ROOT_DIR/scripts/platform-smoke-test.sh"
python3 "$ROOT_DIR/scripts/dlq-inspect.py"

if [[ $INCLUDE_LIVE_DEMO -eq 1 ]]; then
  bash "$ROOT_DIR/scripts/live-demo.sh"
fi

echo
if [[ $INCLUDE_LIVE_DEMO -eq 1 ]]; then
  echo "DOCKER TEST LANE PASSED: Testcontainers + smoke + DLQ + live demo"
else
  echo "DOCKER TEST LANE PASSED: Testcontainers + smoke + DLQ"
fi
