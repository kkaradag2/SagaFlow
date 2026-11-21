\set ON_ERROR_STOP on

SELECT 'CREATE DATABASE "Procurement"'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'Procurement')
\gexec

\connect "Procurement"

BEGIN;

CREATE SCHEMA IF NOT EXISTS purchasing;

CREATE TABLE IF NOT EXISTS purchasing.requests (
    id uuid PRIMARY KEY,
    employee_id varchar(100) NOT NULL,
    customer_id varchar(100) NOT NULL,
    items jsonb NOT NULL CHECK (jsonb_typeof(items) = 'array'),
    fail_step varchar(100),
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    saga_instance_id uuid UNIQUE,
    start_unconfirmed boolean NOT NULL DEFAULT false
);

COMMIT;
