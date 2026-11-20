\set ON_ERROR_STOP on

SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'newsample_order') AS legacy_order_exists
\gset

\if :legacy_order_exists
ALTER DATABASE newsample_order RENAME TO "Order";
\endif

SELECT 'CREATE DATABASE "Order"'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'Order')
\gexec

\connect "Order"

BEGIN;

CREATE SCHEMA IF NOT EXISTS ordering;

CREATE TABLE IF NOT EXISTS ordering.orders (
    id uuid PRIMARY KEY,
    customer_id varchar(100) NOT NULL CHECK (length(btrim(customer_id)) > 0),
    total numeric(18, 2) NOT NULL CHECK (total > 0),
    status text NOT NULL DEFAULT 'Created',
    created_at timestamptz NOT NULL,
    cancelled_at timestamptz,
    CHECK (
        (status = 'Created' AND cancelled_at IS NULL) OR
        (status = 'Cancelled' AND cancelled_at IS NOT NULL)
    )
);

CREATE TABLE IF NOT EXISTS ordering.order_items (
    order_id uuid NOT NULL REFERENCES ordering.orders (id),
    line_number integer NOT NULL CHECK (line_number BETWEEN 1 AND 100),
    product_id varchar(100) NOT NULL CHECK (length(btrim(product_id)) > 0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 10000),
    unit_price numeric(8, 2) NOT NULL CHECK (unit_price > 0),
    PRIMARY KEY (order_id, line_number)
);

COMMIT;
