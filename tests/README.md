# Test Layout

The test suite is split into two operational lanes.

## No-Docker lane

Run:

```bash
bash scripts/test-no-docker.sh --collect:"XPlat Code Coverage"
```

Projects included:

- `InventoryService.UnitTests`
- `OrderService.UnitTests`
- `PaymentService.UnitTests`
- `InventoryService.IntegrationTests`
- `PaymentService.IntegrationTests`
- `Platform.EndToEndTests`
- `EventBus.RabbitMQ.Tests` with `Category!=Docker`

These tests must not require Docker, RabbitMQ, PostgreSQL, Redis, or the live Compose stack.

## Docker-required lane

Run after Docker is available and the platform stack is up:

```bash
bash scripts/test-after-docker.sh --collect:"XPlat Code Coverage"
```

Projects/checks included:

- `EventBus.RabbitMQ.Tests` with `Category=Docker`
- `OrderService.IntegrationTests` with `Category=Docker`
- `scripts/platform-smoke-test.sh`
- `scripts/dlq-inspect.py`

Use this variant to run only Testcontainers-backed dotnet tests without live stack checks:

```bash
bash scripts/test-after-docker.sh --dotnet-only --collect:"XPlat Code Coverage"
```

## Test categories

- `Category=Docker` means the test needs Docker/Testcontainers or live infrastructure.
- Tests without this category are expected to run in the no-Docker lane.

## Adding new tests

- Put pure domain/application logic tests in `*.UnitTests` projects.
- Put API/database/broker tests in integration projects and mark infrastructure-dependent tests with `Category=Docker`.
- Keep test names behavior-focused: `Method_or_feature_should_expected_behavior`.
- Avoid hidden infrastructure dependencies in no-Docker tests.
