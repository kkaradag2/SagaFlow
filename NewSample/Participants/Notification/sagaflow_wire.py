import json
import time
from uuid import uuid4

import pika


def required_text(value):
    if not isinstance(value, str) or not value.strip():
        raise ValueError("A nonblank string is required.")
    return value


def header_text(headers, key):
    value = headers[key]
    return required_text(value.decode("utf-8") if isinstance(value, bytes) else value)


def read_command(properties, body, expected_command, step, outcome_queues):
    headers = properties.headers or {}
    context = {key: value for key, value in headers.items() if key == "sf-version" or key.startswith("sf-ex-")}
    for key in ("sf-ex-saga-id", "sf-ex-execution-id", "sf-ex-domain", "sf-ex-process",
                "sf-ex-process-version", "sf-ex-created-at"):
        header_text(context, key)

    if (header_text(context, "sf-version") != "1"
            or properties.content_type != "application/json"
            or properties.type != expected_command
            or header_text(context, "sf-ex-kind") != "forward"
            or header_text(context, "sf-ex-step-id") != step
            or properties.message_id != header_text(context, "sf-ex-command-id")
            or properties.type != header_text(context, "sf-ex-command-type")
            or properties.reply_to not in outcome_queues
            or properties.reply_to != header_text(context, "sf-ex-reply-to")
            or properties.correlation_id != header_text(context, "sf-ex-correlation-id")):
        raise ValueError("Invalid command properties or execution context.")

    return context, json.loads(body.decode("utf-8"))


def publish_outcome(channel, command, context, success, result):
    # Keep the incoming execution metadata opaque, including the recovery snapshot.
    headers = {**context, "sf-causation-id": command.message_id, "sf-outcome": "success" if success else "failure"}
    outcome = pika.BasicProperties(
        content_type="application/json", content_encoding="utf-8", delivery_mode=2,
        type=command.type + ("Completed" if success else "Failed"),
        message_id=uuid4().hex, correlation_id=command.correlation_id,
        timestamp=int(time.time()), headers=headers,
    )
    channel.basic_publish("", command.reply_to, json.dumps(result).encode("utf-8"), outcome, mandatory=True)
    return outcome
