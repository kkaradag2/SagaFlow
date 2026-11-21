\set ON_ERROR_STOP on

SELECT 'CREATE DATABASE "Return"'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'Return')
\gexec

\connect "Return"

BEGIN;

CREATE SCHEMA IF NOT EXISTS returns;

CREATE TABLE IF NOT EXISTS returns.return_cases (
    return_id uuid PRIMARY KEY,
    order_id uuid NOT NULL,
    customer_id varchar(100) NOT NULL CHECK (length(btrim(customer_id)) > 0),
    amount numeric(12, 2) NOT NULL CHECK (amount > 0),
    fail_step varchar(100),
    created_at timestamptz NOT NULL DEFAULT now(),
    saga_instance_id uuid UNIQUE,
    saga_start_unconfirmed boolean NOT NULL DEFAULT false
);

CREATE TABLE IF NOT EXISTS returns.return_items (
    return_id uuid NOT NULL REFERENCES returns.return_cases (return_id),
    line_number integer NOT NULL CHECK (line_number BETWEEN 1 AND 100),
    product_id varchar(100) NOT NULL CHECK (length(btrim(product_id)) > 0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 10000),
    PRIMARY KEY (return_id, line_number)
);

COMMIT;
