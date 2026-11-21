\set ON_ERROR_STOP on

SELECT 'CREATE DATABASE "Shipment"'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'Shipment')
\gexec

\connect "Shipment"

BEGIN;

CREATE SCHEMA IF NOT EXISTS shipment;

CREATE TABLE IF NOT EXISTS shipment.shipments (
    order_id uuid PRIMARY KEY,
    tracking_number text NOT NULL UNIQUE,
    status text NOT NULL DEFAULT 'Created',
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    cancelled_at timestamptz,
    CHECK (
        (status = 'Created' AND cancelled_at IS NULL) OR
        (status = 'Cancelled' AND cancelled_at IS NOT NULL)
    )
);

COMMIT;
