# Security Notes

This is a local demo system. It intentionally uses simple local credentials and lightweight demo gateway authentication for repeatable execution.

## Demo-only credentials

The Docker Compose defaults are not production secrets:

- PostgreSQL: `postgres/postgres`
- RabbitMQ: `guest/guest`
- ApiGateway API key: `local-dev-key`
- ApiGateway bearer token: `local-dev-token`

Do not reuse these values in a real environment.

## Production hardening path

Before production deployment, replace or add:

- OIDC/JWT authentication and policy-based authorization.
- Managed secret storage.
- TLS between clients and services.
- Real payment provider idempotency keys and webhook verification.
- Audited refund execution and reconciliation jobs.
- Production-grade DLQ re-drive with approvals and audit logs.
- Centralized logs, metrics, tracing, alerting, and dashboards.

## Reporting issues

For public repository use, open an issue with reproduction details. Do not include secrets, tokens, or private data in issues.
