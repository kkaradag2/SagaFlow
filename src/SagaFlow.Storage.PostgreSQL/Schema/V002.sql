CREATE TABLE sagaflow.outbox (
    message_id text PRIMARY KEY,
    saga_instance_id text NOT NULL,
    document json NOT NULL,
    created_at timestamptz NOT NULL,
    retry_at timestamptz NOT NULL,
    lease_until timestamptz NULL,
    owner text NULL,
    published_at timestamptz NULL,
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    CONSTRAINT outbox_lease_owner CHECK ((lease_until IS NULL) = (owner IS NULL))
);
CREATE INDEX outbox_pending_retry_idx ON sagaflow.outbox (retry_at, created_at)
    WHERE published_at IS NULL;
CREATE INDEX outbox_pending_lease_idx ON sagaflow.outbox (lease_until)
    WHERE published_at IS NULL AND lease_until IS NOT NULL;
