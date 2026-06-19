#!/usr/bin/env python3
"""Inspect RabbitMQ DLQ messages through the management API without removing messages.

This script is intentionally read-only by default. It uses ack_requeue_true for
message peeks so inspected messages remain in the DLQ.
"""
from __future__ import annotations

import argparse
import base64
import json
import sys
import urllib.error
import urllib.parse
import urllib.request
from typing import Any

DEFAULT_QUEUES = [
    "inventory.failed.dlq",
    "payment.failed.dlq",
    "notification.failed.dlq",
    "reporting.failed.dlq",
]


def auth_header(username: str, password: str) -> str:
    token = base64.b64encode(f"{username}:{password}".encode("utf-8")).decode("ascii")
    return f"Basic {token}"


def request_json(method: str, url: str, username: str, password: str, body: dict[str, Any] | None = None) -> Any:
    data = None if body is None else json.dumps(body).encode("utf-8")
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Authorization", auth_header(username, password))
    request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=10) as response:  # noqa: S310 - local dev tool
            response_body = response.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {exc.code} from RabbitMQ API {url}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Could not reach RabbitMQ API {url}: {exc}") from exc

    if not response_body:
        return None
    return json.loads(response_body)


def queue_url(base_url: str, vhost: str, queue: str) -> str:
    return f"{base_url.rstrip('/')}/api/queues/{urllib.parse.quote(vhost, safe='')}/{urllib.parse.quote(queue, safe='')}"


def inspect_queue(args: argparse.Namespace, queue: str) -> int:
    base_queue_url = queue_url(args.url, args.vhost, queue)
    stats = request_json("GET", base_queue_url, args.username, args.password)
    message_count = int(stats.get("messages", 0))
    ready_count = int(stats.get("messages_ready", 0))
    unacked_count = int(stats.get("messages_unacknowledged", 0))

    print(f"\n{queue}")
    print("-" * len(queue))
    print(f"messages={message_count} ready={ready_count} unacknowledged={unacked_count}")

    if message_count == 0 or args.peek <= 0:
        return message_count

    get_url = f"{base_queue_url}/get"
    messages = request_json(
        "POST",
        get_url,
        args.username,
        args.password,
        {
            "count": min(args.peek, message_count),
            "ackmode": "ack_requeue_true",
            "encoding": "auto",
            "truncate": args.truncate,
        },
    )

    for index, message in enumerate(messages, start=1):
        properties = message.get("properties") or {}
        headers = properties.get("headers") or {}
        print(f"\n  message #{index}")
        print(f"    routing_key: {message.get('routing_key')}")
        print(f"    message_id: {properties.get('message_id')}")
        print(f"    correlation_id: {properties.get('correlation_id')}")
        print(f"    x-error-type: {headers.get('x-error-type')}")
        print(f"    x-error-message: {headers.get('x-error-message')}")
        print(f"    x-original-routing-key: {headers.get('x-original-routing-key')}")
        print(f"    x-retry-count: {headers.get('x-retry-count')}")
        if args.payload:
            payload = message.get("payload")
            print(f"    payload: {payload}")

    return message_count


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Inspect platform RabbitMQ DLQs without removing messages.")
    parser.add_argument("--url", default="http://localhost:15672", help="RabbitMQ management base URL.")
    parser.add_argument("--username", default="guest")
    parser.add_argument("--password", default="guest")
    parser.add_argument("--vhost", default="/")
    parser.add_argument("--queue", action="append", dest="queues", help="Queue to inspect. Can be repeated.")
    parser.add_argument("--peek", type=int, default=3, help="Number of messages to peek per non-empty queue.")
    parser.add_argument("--truncate", type=int, default=50000)
    parser.add_argument("--payload", action="store_true", help="Print payload bodies for peeked messages.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    queues = args.queues or DEFAULT_QUEUES
    print("RabbitMQ DLQ inspection")
    print(f"Management API: {args.url}")
    print("Mode: read-only peek; messages are requeued after inspection")

    try:
        total = sum(inspect_queue(args, queue) for queue in queues)
    except RuntimeError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    print("\nSummary")
    print("-------")
    print(f"total_dlq_messages={total}")
    return 0 if total == 0 else 3


if __name__ == "__main__":
    raise SystemExit(main())
