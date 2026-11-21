import json
import logging
import os
import time
from functools import partial
from pathlib import Path
from uuid import UUID

import pika

from sagaflow_wire import read_command, publish_outcome, required_text


def required_id(payload, key):
    value = UUID(required_text(payload[key]))
    if not value.int:
        raise ValueError(f"{key} must be a nonzero UUID.")
    return str(value)


def failure_requested(payload, step):
    fail_step = payload.get("failStep")
    return isinstance(fail_step, str) and fail_step.strip().casefold() == step.casefold()


def notify_return(command, step, payload):
    return_id = required_id(payload, "returnId")
    order_id = required_id(payload, "orderId")
    required_text(payload["customerId"])
    result = {"returnId": return_id, "orderId": order_id}
    if failure_requested(payload, step):
        return False, {**result, "error": "notification_failed"}

    decision = "accepted" if command == "NotifyReturnAccepted" else "rejected"
    logging.info("Simulated %s notification for return %s and order %s", decision, return_id, order_id)
    return True, {**result, "notified": True}


def notify(command, step, payload):
    if not isinstance(payload, dict):
        raise ValueError("Notification requires a JSON object.")

    if command in ("NotifyReturnAccepted", "NotifyReturnRejected"):
        return notify_return(command, step, payload)

    order_id = required_id(payload, "id")
    if failure_requested(payload, step):
        return False, {"error": "notification_failed", "orderId": order_id}

    if command == "NotifyManager":
        quantity = payload["totalQuantity"]
        if type(quantity) is not int or not 0 < quantity < 2**63:
            raise ValueError("Total quantity must be a positive integer.")
        logging.info("Simulated manager notification for order %s with %s items", order_id, quantity)
        return True, {"orderId": order_id, "totalQuantity": quantity, "notified": True}

    required_text(payload["customerId"])
    logging.info("Simulated completion notification for order %s", order_id)
    return True, {"orderId": order_id, "notified": True}


def handle_delivery(channel, delivery, properties, body, expected_command, queue_name, outcome_queues):
    try:
        context, payload = read_command(properties, body, expected_command, queue_name, outcome_queues)
    except (KeyError, ValueError) as error:
        logging.warning("Rejecting malformed command %s: %s", properties.message_id, error)
        channel.basic_reject(delivery.delivery_tag, requeue=False)
        return

    try:
        success, result = notify(expected_command, queue_name, payload)
    except (KeyError, ValueError):
        success, result = False, {"error": "invalid_notification_request"}

    try:
        outcome = publish_outcome(channel, properties, context, success, result)
    except (pika.exceptions.UnroutableError, pika.exceptions.NackError):
        logging.warning("Outcome for command %s was not confirmed; requeuing", properties.message_id)
        time.sleep(1)
        channel.basic_nack(delivery.delivery_tag, requeue=True)
        return

    channel.basic_ack(delivery.delivery_tag)
    logging.info("Confirmed %s saga=%s replyTo=%s causation=%s", outcome.type,
                 context["sf-ex-saga-id"], properties.reply_to, properties.message_id)


def consume(settings):
    url = os.environ.get("SAGAFLOW_NOTIFICATION_AMQP_URL") or settings["ConnectionString"]
    parameters = pika.URLParameters(url)
    parameters.client_properties = {"connection_name": settings["ClientProvidedName"]}
    exchange = settings["CommandExchange"]
    queues = settings["Queues"]
    outcome_queues = frozenset(settings["OutcomeQueues"])

    with pika.BlockingConnection(parameters) as connection:
        channel = connection.channel()
        channel.exchange_declare(exchange, exchange_type="direct", durable=True)
        channel.basic_qos(prefetch_count=1)
        channel.confirm_delivery()
        for queue_name, command in queues.items():
            channel.queue_declare(queue_name, durable=True)
            channel.queue_bind(queue_name, exchange, routing_key=queue_name)
            callback = partial(handle_delivery, expected_command=command, queue_name=queue_name,
                               outcome_queues=outcome_queues)
            channel.basic_consume(queue_name, callback, auto_ack=False)
        logging.info("Notification listening on %s", ", ".join(queues))
        channel.start_consuming()


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    logging.getLogger("pika").setLevel(logging.WARNING)
    settings = json.loads(Path(__file__).with_name("appsettings.json").read_text(encoding="utf-8"))["RabbitMq"]
    try:
        while True:
            try:
                consume(settings)
                return
            except pika.exceptions.AMQPConnectionError as error:
                logging.warning("Notification is waiting for RabbitMQ: %s", type(error).__name__)
                time.sleep(2)
    except KeyboardInterrupt:
        logging.info("Notification stopped")


if __name__ == "__main__":
    main()
