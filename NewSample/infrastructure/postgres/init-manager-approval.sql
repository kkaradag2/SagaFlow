\set ON_ERROR_STOP on

SELECT 'CREATE DATABASE "ManagerApproval"'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'ManagerApproval')
\gexec

\connect "ManagerApproval"

BEGIN;

CREATE SCHEMA IF NOT EXISTS approval;

CREATE TABLE IF NOT EXISTS approval.requests (
    id uuid PRIMARY KEY,
    order_id uuid NOT NULL,
    total_quantity integer NOT NULL CHECK (total_quantity > 0),
    requested_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    command_id text NOT NULL UNIQUE,
    reply_to text NOT NULL,
    correlation_id text NOT NULL,
    headers jsonb NOT NULL,
    approved boolean,
    decided_at timestamptz,
    CHECK ((approved IS NULL) = (decided_at IS NULL))
);

CREATE TABLE IF NOT EXISTS approval.outbox (
    approval_id uuid PRIMARY KEY REFERENCES approval.requests (id),
    message_id uuid NOT NULL UNIQUE,
    next_attempt_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    attempts integer NOT NULL DEFAULT 0,
    published_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_approval_outbox_pending ON approval.outbox (next_attempt_at)
    WHERE published_at IS NULL;

COMMIT;
