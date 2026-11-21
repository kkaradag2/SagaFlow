\set ON_ERROR_STOP on

SELECT 'CREATE DATABASE "Inventory"'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'Inventory')
\gexec

\connect "Inventory"

BEGIN;

CREATE SCHEMA IF NOT EXISTS inventory;

CREATE TABLE IF NOT EXISTS inventory.stock (
    product_id varchar(100) COLLATE "C" PRIMARY KEY,
    available_quantity bigint NOT NULL CHECK (available_quantity >= 0)
);

CREATE TABLE IF NOT EXISTS inventory.reservations (
    order_id uuid PRIMARY KEY,
    status text NOT NULL DEFAULT 'Reserved',
    reserved_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    released_at timestamptz,
    CHECK (
        (status = 'Reserved' AND released_at IS NULL) OR
        (status = 'Released' AND released_at IS NOT NULL)
    )
);

CREATE TABLE IF NOT EXISTS inventory.reservation_items (
    order_id uuid NOT NULL REFERENCES inventory.reservations (order_id),
    product_id varchar(100) COLLATE "C" NOT NULL REFERENCES inventory.stock (product_id),
    quantity integer NOT NULL CHECK (quantity > 0),
    PRIMARY KEY (order_id, product_id)
);

CREATE TABLE IF NOT EXISTS inventory.return_restocks (
    return_id uuid PRIMARY KEY,
    order_id uuid NOT NULL,
    items jsonb NOT NULL CHECK (jsonb_typeof(items) = 'array'),
    restocked_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

INSERT INTO inventory.stock (product_id, available_quantity) VALUES
    ('sku-1', 100),
    ('sku-2', 100),
    ('sku-bulk', 2000),
    ('sku-limited', 5)
ON CONFLICT (product_id) DO NOTHING;

COMMIT;
