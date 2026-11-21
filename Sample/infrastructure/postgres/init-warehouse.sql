\set ON_ERROR_STOP on

SELECT 'CREATE DATABASE "Warehouse"'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'Warehouse')
\gexec

\connect "Warehouse"

BEGIN;

CREATE SCHEMA IF NOT EXISTS inspection;

CREATE TABLE IF NOT EXISTS inspection.requests (
    return_id uuid PRIMARY KEY,
    inspection_id uuid NOT NULL UNIQUE,
    order_id uuid NOT NULL,
    requested_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    command_id text NOT NULL UNIQUE,
    reply_to text NOT NULL,
    correlation_id text NOT NULL,
    headers jsonb NOT NULL,
    accepted boolean,
    decided_at timestamptz,
    CHECK ((accepted IS NULL) = (decided_at IS NULL))
);

CREATE TABLE IF NOT EXISTS inspection.outbox (
    return_id uuid PRIMARY KEY REFERENCES inspection.requests (return_id),
    message_id uuid NOT NULL UNIQUE,
    next_attempt_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    attempts integer NOT NULL DEFAULT 0,
    published_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_inspection_outbox_pending ON inspection.outbox (next_attempt_at)
    WHERE published_at IS NULL;

COMMIT;
