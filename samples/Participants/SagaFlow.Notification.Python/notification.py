import copy
import json
import logging
import os
import time
import uuid

import pika


COMMANDS = {
    "notification": "SendOrderNotification",
    "managerNotification": "NotifyManager",
    "acceptedNotification": "NotifyReturnAccepted",
    "rejectedNotification": "NotifyReturnRejected",
}


def required_text(value):
    if not isinstance(value, str) or not value.strip():
        raise ValueError("A nonblank string is required.")
    return value


class Notifications:
    def __init__(self):
        self.orders = {}
        self.managers = {}
        self.returns = {}

    def handle(self, command, payload, compensation):
        if not isinstance(payload, dict):
            raise ValueError("Notification requires a JSON object.")
        fields = {key.lower(): value for key, value in payload.items()}
        if command in ("NotifyReturnAccepted", "NotifyReturnRejected") and compensation:
            raise ValueError("Return notification is a forward operation.")
        if command != "NotifyManager" and fields.get("failureat") == "notification":
            raise ValueError("Sample failure requested at notification.")

        if command == "NotifyManager":
            order_id = uuid.UUID(required_text(fields["id"])).hex
            quantity = fields["totalquantity"]
            if type(quantity) is not int or not 0 < quantity < 2**63:
                raise ValueError("Total quantity must be a positive 64-bit integer.")
            result = {"orderId": order_id, "totalQuantity": quantity,
                      "message": f"Manager approval requested for order {order_id}: total quantity {quantity}."}
            stored = self.record(self.managers, order_id, result)
            if stored is result:
                logging.info("%s", result["message"])
            return stored

        recipient = required_text(fields["customerid"]).strip()
        if command == "SendOrderNotification":
            order_id = uuid.UUID(required_text(fields["id"])).hex
            result = {"orderId": order_id, "recipient": recipient,
                      "message": f"Order {order_id} has been completed."}
            return self.record(self.orders, order_id, result)

        return_id = uuid.UUID(required_text(fields["returnid"])).hex
        order_id = uuid.UUID(required_text(fields["orderid"])).hex
        decision = "Accepted" if command == "NotifyReturnAccepted" else "Rejected"
        result = {"returnId": return_id, "orderId": order_id, "recipient": recipient, "decision": decision,
                  "message": f"Return {return_id} for order {order_id} was {decision.lower()}."}
        return self.record(self.returns, return_id, result)

    @staticmethod
    def record(records, key, result):
        stored = records.setdefault(key, result)
        if stored != result:
            raise ValueError("A different notification already exists for this record.")
        return stored


def header_text(headers, key):
    value = headers[key]
    return required_text(value.decode("utf-8") if isinstance(value, bytes) else value)


def quarantine(channel, method, properties, body):
    preserved = copy.copy(properties)
    preserved.delivery_mode = 2
    preserved.expiration = None
    preserved.headers = dict(properties.headers or {}, **{
        "sf-quarantine-source": method.routing_key,
        "sf-quarantine-exchange": method.exchange,
        "sf-quarantine-routing-key": method.routing_key,
        "sf-quarantine-reason": "missing-or-unsupported-wire-version",
    })
    if properties.expiration is not None:
        preserved.headers["sf-quarantine-original-expiration"] = properties.expiration
    channel.basic_publish("", method.routing_key + ".quarantine", body, preserved, mandatory=True)


def handle_delivery(channel, method, properties, body, notifications):
    headers = properties.headers or {}
    if headers.get("sf-version") not in ("1", b"1"):
        quarantine(channel, method, properties, body)
        channel.basic_ack(method.delivery_tag)
        logging.warning("Preserved incompatible Command %s in quarantine", properties.message_id)
        return

    context = {key: value for key, value in headers.items() if key == "sf-version" or key.startswith("sf-ex-")}
    try:
        saga_id = header_text(context, "sf-ex-saga-id")
        execution_id = header_text(context, "sf-ex-execution-id")
        kind = header_text(context, "sf-ex-kind")
        for key in ("sf-ex-domain", "sf-ex-process", "sf-ex-process-version", "sf-ex-step-id", "sf-ex-created-at"):
            header_text(context, key)
        if kind == "compensation":
            header_text(context, "sf-ex-forward-execution-id")
        if (properties.content_type != "application/json" or properties.type not in COMMANDS.values()
                or kind not in ("forward", "compensation")
                or properties.message_id != header_text(context, "sf-ex-command-id")
                or properties.type != header_text(context, "sf-ex-command-type")
                or properties.reply_to != header_text(context, "sf-ex-reply-to")
                or properties.correlation_id != header_text(context, "sf-ex-correlation-id")):
            raise ValueError("Invalid Command properties or execution context.")
        payload = json.loads(body.decode("utf-8"))
    except (ValueError, KeyError) as error:
        logging.warning("Rejecting malformed Command %s: %s", properties.message_id, error)
        channel.basic_reject(method.delivery_tag, requeue=False)
        return

    logging.info("Received %s queue=%s saga=%s execution=%s command=%s correlation=%s replyTo=%s",
                 properties.type, method.routing_key, saga_id, execution_id, properties.message_id,
                 properties.correlation_id, properties.reply_to)
    try:
        result = notifications.handle(properties.type, payload, kind == "compensation")
        suffix, status = "Completed", "success"
    except (KeyError, ValueError) as error:
        result = {"error": str(error)}
        suffix, status = "Failed", "failure"

    # Keep execution values opaque, including the recovery snapshot. Only Outcome fields are new.
    context.update({"sf-causation-id": properties.message_id, "sf-outcome": status})
    outcome = pika.BasicProperties(
        content_type="application/json", content_encoding="utf-8", delivery_mode=2,
        type=properties.type + suffix, message_id=uuid.uuid4().hex,
        correlation_id=properties.correlation_id, timestamp=int(time.time()), headers=context,
    )
    try:
        channel.basic_publish("", properties.reply_to, json.dumps(result).encode("utf-8"), outcome, mandatory=True)
    except (pika.exceptions.UnroutableError, pika.exceptions.NackError):
        logging.exception("Outcome publication failed for Command %s; requeueing", properties.message_id)
        channel.basic_nack(method.delivery_tag, requeue=True)
        return
    channel.basic_ack(method.delivery_tag)
    logging.info("Confirmed %s saga=%s execution=%s outcome=%s causation=%s correlation=%s replyTo=%s",
                 outcome.type, saga_id, execution_id, outcome.message_id, properties.message_id,
                 outcome.correlation_id, properties.reply_to)


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    logging.getLogger("pika").setLevel(logging.WARNING)
    parameters = pika.URLParameters(os.environ["SAGAFLOW_NOTIFICATION_AMQP_URL"])
    parameters.client_properties = {"connection_name": "sagaflow-notification-python"}
    notifications = Notifications()
    with pika.BlockingConnection(parameters) as connection:
        channel = connection.channel()
        exchange = "sagaflow.commands"
        channel.exchange_declare(exchange, exchange_type="direct", durable=True)
        channel.basic_qos(prefetch_count=1)
        channel.confirm_delivery()
        for queue in COMMANDS:
            channel.queue_declare(queue, durable=True)
            channel.queue_bind(queue, exchange, routing_key=queue)
            channel.queue_declare(queue + ".quarantine", durable=True)
            channel.basic_consume(queue, lambda *delivery: handle_delivery(*delivery, notifications), auto_ack=False)
        logging.info("Python Notification listening on %s", ", ".join(COMMANDS))
        try:
            channel.start_consuming()
        except KeyboardInterrupt:
            channel.stop_consuming()


if __name__ == "__main__":
    main()
