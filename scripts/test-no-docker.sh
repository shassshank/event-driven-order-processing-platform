#!/usr/bin/env bash
set -o pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS_DIR="$ROOT_DIR/TestResults/no-docker-$(date +%Y%m%d-%H%M%S)"
LOG_FILE="$RESULTS_DIR/dotnet-test-no-docker.log"
mkdir -p "$RESULTS_DIR"

EXTRA_ARGS=("$@")
EXIT_CODE=0

run_project() {
  local project="$1"
  shift || true

  echo
  echo "========================================================================"
  echo "NO-DOCKER TEST PROJECT: $project"
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
No-Docker test lane
========================================================================
This lane does not require Docker, Testcontainers, RabbitMQ, PostgreSQL,
Redis, or the Docker Compose platform stack.

Results directory: $RESULTS_DIR
Console log:       $LOG_FILE
========================================================================
HEADER

run_project "tests/InventoryService.UnitTests/InventoryService.UnitTests.csproj"
run_project "tests/OrderService.UnitTests/OrderService.UnitTests.csproj"
run_project "tests/PaymentService.UnitTests/PaymentService.UnitTests.csproj"
run_project "tests/InventoryService.IntegrationTests/InventoryService.IntegrationTests.csproj"
run_project "tests/PaymentService.IntegrationTests/PaymentService.IntegrationTests.csproj"
run_project "tests/Platform.EndToEndTests/Platform.EndToEndTests.csproj"
run_project "tests/EventBus.RabbitMQ.Tests/EventBus.RabbitMQ.Tests.csproj" --filter "Category!=Docker"

python3 "$ROOT_DIR/scripts/summarize-test-results.py" "$RESULTS_DIR" "$EXIT_CODE" "$LOG_FILE"
exit $?
