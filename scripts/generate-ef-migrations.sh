#!/usr/bin/env bash
set -euo pipefail

MIGRATION_NAME="${1:-InitialCreate}"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

echo "Restoring local tools..."
dotnet tool restore

declare -a SERVICES=(
  "OrderService|OrderDbContext|src/Services/OrderService/OrderService.csproj|Persistence/Migrations"
  "InventoryService|InventoryDbContext|src/Services/InventoryService/InventoryService.csproj|Persistence/Migrations"
  "PaymentService|PaymentDbContext|src/Services/PaymentService/PaymentService.csproj|Persistence/Migrations"
  "NotificationService|NotificationDbContext|src/Services/NotificationService/NotificationService.csproj|Persistence/Migrations"
  "ReportingService|ReportingDbContext|src/Services/ReportingService/ReportingService.csproj|Persistence/Migrations"
)

for entry in "${SERVICES[@]}"; do
  IFS='|' read -r service context project output_dir <<< "$entry"
  echo
  echo "=== Generating migration for ${service} (${context}) ==="
  dotnet ef migrations add "$MIGRATION_NAME" \
    --project "$project" \
    --startup-project "$project" \
    --context "$context" \
    --output-dir "$output_dir"
done

echo
echo "Generated migrations named '${MIGRATION_NAME}' for all service DbContexts."
echo "Review generated files before committing."
