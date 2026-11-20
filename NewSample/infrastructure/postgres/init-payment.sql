\set ON_ERROR_STOP on

SELECT 'CREATE DATABASE "Payment"'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'Payment')
\gexec

\connect "Payment"

BEGIN;

CREATE SCHEMA IF NOT EXISTS payment;

CREATE TABLE IF NOT EXISTS payment.authorizations (
    order_id uuid PRIMARY KEY,
    amount numeric(18, 2) NOT NULL CHECK (amount > 0),
    status text NOT NULL DEFAULT 'Authorized',
    authorized_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    cancelled_at timestamptz,
    CHECK (
        (status = 'Authorized' AND cancelled_at IS NULL) OR
        (status = 'Cancelled' AND cancelled_at IS NOT NULL)
    )
);

COMMIT;
