# Reliability Design

## Transactional outbox

Business data and integration events are committed together. HTTP request handlers never publish directly to RabbitMQ.

## Idempotent consumers

Consumers store `(MessageId, ConsumerName)` in `processed_messages`. Business updates and processed-message records are committed in the same transaction where possible.

## Retry and DLQ

Transient failures are retried through delayed retry queues. Poison messages are routed to service-specific dead-letter queues with original routing key, message ID, correlation ID, and error metadata.

## Inventory concurrency

Inventory will use optimistic concurrency in Phase 3 so that competing reservations cannot oversell a product.
