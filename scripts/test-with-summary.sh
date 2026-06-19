#!/usr/bin/env bash
set -u -o pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS_DIR="$ROOT_DIR/TestResults/summary-$(date +%Y%m%d-%H%M%S)"
LOG_FILE="$RESULTS_DIR/dotnet-test.log"
mkdir -p "$RESULTS_DIR"

echo "Running dotnet test with TRX output..."
echo "Results directory: $RESULTS_DIR"
echo "Console log: $LOG_FILE"
echo

dotnet test "$ROOT_DIR/event-driven-order-platform.sln" \
  --logger "trx" \
  --results-directory "$RESULTS_DIR" \
  "$@" \
  2>&1 | tee "$LOG_FILE"
DOTNET_EXIT_CODE=${PIPESTATUS[0]}

python3 "$ROOT_DIR/scripts/summarize-test-results.py" "$RESULTS_DIR" "$DOTNET_EXIT_CODE" "$LOG_FILE"
SUMMARY_EXIT_CODE=$?

exit "$SUMMARY_EXIT_CODE"
