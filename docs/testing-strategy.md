# Testing Strategy

The repository has two explicit validation lanes so local development does not accidentally depend on infrastructure.

## Command 1: No-Docker lane

Run this first for fast feedback:

```bash
bash scripts/test-no-docker.sh --collect:"XPlat Code Coverage"
```

Equivalent Makefile alias:

```bash
make test-no-docker
```

This lane does not require Docker, Testcontainers, RabbitMQ, PostgreSQL, Redis, or the Docker Compose platform stack.

It runs:

| Project | Purpose |
| --- | --- |
| `InventoryService.UnitTests` | Reservation/release business logic. |
| `OrderService.UnitTests` | Request validation, order state transitions, outbox, inventory/payment event handlers. |
| `PaymentService.UnitTests` | Payment attempt rules, duplicate-payment guard, cancellation/refund-required handling, advisory lock key generation. |
| `InventoryService.IntegrationTests` | Build/readiness checks that do not require external infrastructure. |
| `PaymentService.IntegrationTests` | Build/readiness checks that do not require external infrastructure. |
| `Platform.EndToEndTests` | In-process/static workflow, notification/reporting, and gateway catalog/policy verification. |
| `EventBus.RabbitMQ.Tests` except `Category=Docker` | RabbitMQ topology/routing/retry policy tests that do not connect to a broker. |

Expected result:

```text
Failed: 0
Skipped: 0
```

## Command 2: Docker-required lane

Run this after Docker is available. By default it also expects the Docker Compose platform stack to be running.

Start the stack:

```bash
docker compose -f deploy/docker-compose.yml up -d --build \
  postgres redis rabbitmq \
  order-service inventory-service payment-service \
  notification-service reporting-service api-gateway
```

Then run:

```bash
bash scripts/test-after-docker.sh --collect:"XPlat Code Coverage"
```

Equivalent Makefile aliases:

```bash
make up
make test-after-docker
```

This lane runs:

| Check | Purpose |
| --- | --- |
| `EventBus.RabbitMQ.Tests` with `Category=Docker` | Real RabbitMQ retry/DLQ/publisher-confirm behavior through Testcontainers. |
| `OrderService.IntegrationTests` with `Category=Docker` | API integration tests with a real PostgreSQL Testcontainer and RabbitMQ hosted services removed from the test host. |
| `scripts/platform-smoke-test.sh` | Live Docker Compose happy-path order flow through ApiGateway. |
| `scripts/dlq-inspect.py` | Read-only RabbitMQ DLQ check; expects zero DLQ messages after a clean smoke test. |

Expected result:

```text
Failed: 0
Skipped: 0
SMOKE TEST PASSED
total_dlq_messages=0
```

### Docker dotnet tests only

To run only Testcontainers-backed dotnet tests without live stack smoke/DLQ checks:

```bash
bash scripts/test-after-docker.sh --dotnet-only --collect:"XPlat Code Coverage"
```

### Include live demo script

To also run the presentation demo script:

```bash
bash scripts/test-after-docker.sh --include-live-demo
```

## Why the split exists

Some tests are pure unit/static checks and should run anywhere. Other tests intentionally verify real broker/database behavior and need Docker. Keeping the lanes separate makes failures easier to understand:

- `test-no-docker.sh` failure means code or in-process logic is broken.
- `test-after-docker.sh` failure can be code, Testcontainers, local Docker, or live stack readiness.

The older `scripts/test-with-summary.sh` remains available for a one-shot all-test run, but the two-lane commands above are the recommended workflow.

## Test category rule

Any test that needs Docker, Testcontainers, RabbitMQ, PostgreSQL, Redis, or the live Compose stack must be marked:

```csharp
[Trait("Category", "Docker")]
```

Tests without that trait must pass in the no-Docker lane.
