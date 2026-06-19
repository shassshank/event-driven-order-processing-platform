#!/usr/bin/env python3
"""Replay already-published outbox events into RabbitMQ.

Use this after adding a new event-driven projection/consumer service, such as
NotificationService or ReportingService, when old orders already exist but their
original RabbitMQ publishes happened before the new queues/bindings existed.

The script is intentionally dependency-free. It reads outbox rows through the
running PostgreSQL Docker container using psql, then publishes each envelope to
RabbitMQ's management HTTP API.
"""

from __future__ import annotations

import argparse
import base64
import csv
import json
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Iterable


OUTBOX_TABLES = [
    "outbox_messages",
    "inventory_outbox_messages",
    "payment_outbox_messages",
    "notification_outbox_messages",
]


@dataclass(frozen=True)
class OutboxEvent:
    table: str
    row_id: str
    routing_key: str
    payload: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Replay published outbox events to RabbitMQ.")
    parser.add_argument("--postgres-container", default="order-platform-postgres")
    parser.add_argument("--postgres-user", default="postgres")
    parser.add_argument("--postgres-db", default="orders")
    parser.add_argument("--rabbitmq-url", default="http://localhost:15672")
    parser.add_argument("--rabbitmq-user", default="guest")
    parser.add_argument("--rabbitmq-password", default="guest")
    parser.add_argument("--exchange", default="order-platform.exchange")
    parser.add_argument("--status", default="Published", help="Outbox Status value to replay. Default: Published")
    parser.add_argument("--dry-run", action="store_true", help="Print replay plan without publishing.")
    parser.add_argument("--limit", type=int, default=0, help="Maximum number of events to replay. 0 means no limit.")
    return parser.parse_args()


def run_docker_psql(args: argparse.Namespace, sql: str) -> str:
    command = [
        "docker",
        "exec",
        args.postgres_container,
        "psql",
        "-U",
        args.postgres_user,
        "-d",
        args.postgres_db,
        "-q",
        "-t",
        "-A",
        "-c",
        sql,
    ]

    result = subprocess.run(command, text=True, capture_output=True, check=False)
    if result.returncode != 0:
        raise RuntimeError(
            "psql command failed. Is the PostgreSQL container running?\n"
            f"Command: {' '.join(command)}\n"
            f"STDERR:\n{result.stderr.strip()}"
        )

    return result.stdout


def table_exists(args: argparse.Namespace, table: str) -> bool:
    output = run_docker_psql(args, f"select to_regclass('public.{table}');")
    return table in output


def read_events(args: argparse.Namespace) -> list[OutboxEvent]:
    events: list[OutboxEvent] = []

    for table in OUTBOX_TABLES:
        if not table_exists(args, table):
            print(f"Skipping {table}: table does not exist yet.")
            continue

        sql = f'''copy (
            select '{table}' as table_name,
                   "Id"::text as row_id,
                   "RoutingKey" as routing_key,
                   "Payload"::text as payload
            from {table}
            where "Status" = '{args.status.replace("'", "''")}'
            order by "OccurredOnUtc", "Id"
        ) to stdout with csv'''

        output = run_docker_psql(args, sql)
        for row in csv.reader(output.splitlines()):
            if len(row) != 4:
                continue
            events.append(OutboxEvent(row[0], row[1], row[2], row[3]))

    if args.limit > 0:
        events = events[: args.limit]

    return events


def build_auth_header(args: argparse.Namespace) -> str:
    raw = f"{args.rabbitmq_user}:{args.rabbitmq_password}".encode("utf-8")
    return "Basic " + base64.b64encode(raw).decode("ascii")


def publish_event(args: argparse.Namespace, event: OutboxEvent) -> bool:
    try:
        envelope = json.loads(event.payload)
    except json.JSONDecodeError as exc:
        print(f"SKIP {event.table}#{event.row_id}: payload is not valid JSON: {exc}", file=sys.stderr)
        return False

    message_id = str(envelope.get("messageId") or "")
    correlation_id = str(envelope.get("correlationId") or "")
    event_type = str(envelope.get("eventType") or "")

    body = {
        "properties": {
            "content_type": "application/json",
            "delivery_mode": 2,
            "message_id": message_id,
            "correlation_id": correlation_id,
            "type": event_type,
            "headers": {
                "x-replayed": True,
                "x-replayed-from-table": event.table,
                "x-replayed-from-row-id": event.row_id,
                "x-replayed-at-utc": datetime.now(timezone.utc).isoformat(),
            },
        },
        "routing_key": event.routing_key,
        "payload": event.payload,
        "payload_encoding": "string",
    }

    if args.dry_run:
        print(f"DRY RUN {event.table}#{event.row_id}: {event.routing_key} {event_type} {message_id}")
        return True

    exchange_path = urllib.parse.quote(args.exchange, safe="")
    url = f"{args.rabbitmq_url.rstrip('/')}/api/exchanges/%2F/{exchange_path}/publish"
    request = urllib.request.Request(
        url,
        data=json.dumps(body).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "Authorization": build_auth_header(args),
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            response_body = response.read().decode("utf-8")
    except urllib.error.URLError as exc:
        print(f"FAILED {event.table}#{event.row_id}: RabbitMQ publish failed: {exc}", file=sys.stderr)
        return False

    try:
        response_json = json.loads(response_body)
    except json.JSONDecodeError:
        print(f"FAILED {event.table}#{event.row_id}: RabbitMQ returned non-JSON response: {response_body}", file=sys.stderr)
        return False

    routed = bool(response_json.get("routed"))
    status = "REPLAYED" if routed else "UNROUTED"
    print(f"{status} {event.table}#{event.row_id}: {event.routing_key} {event_type} {message_id}")
    return routed


def main() -> int:
    args = parse_args()

    print("Reading published outbox events from PostgreSQL...")
    events = read_events(args)
    print(f"Found {len(events)} event(s) with Status='{args.status}'.")

    if not events:
        print("Nothing to replay.")
        return 0

    success = 0
    failed = 0
    for event in events:
        if publish_event(args, event):
            success += 1
        else:
            failed += 1

    print("=" * 72)
    print("REPLAY SUMMARY")
    print("=" * 72)
    print(f"Total: {len(events)} | Routed: {success} | Failed/unrouted: {failed}")

    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
