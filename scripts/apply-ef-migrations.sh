#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

echo "Restoring local tools..."
dotnet tool restore

declare -a SERVICES=(
  "OrderService|OrderDbContext|src/Services/OrderService/OrderService.csproj"
  "InventoryService|InventoryDbContext|src/Services/InventoryService/InventoryService.csproj"
  "PaymentService|PaymentDbContext|src/Services/PaymentService/PaymentService.csproj"
  "NotificationService|NotificationDbContext|src/Services/NotificationService/NotificationService.csproj"
  "ReportingService|ReportingDbContext|src/Services/ReportingService/ReportingService.csproj"
)

for entry in "${SERVICES[@]}"; do
  IFS='|' read -r service context project <<< "$entry"
  echo
  echo "=== Applying migrations for ${service} (${context}) ==="
  dotnet ef database update \
    --project "$project" \
    --startup-project "$project" \
    --context "$context"
done

echo
echo "Applied all service migrations."
