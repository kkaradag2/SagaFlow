\set ON_ERROR_STOP on
\getenv order_password SAGAFLOW_POSTGRES_ORDER_PASSWORD
\getenv return_password SAGAFLOW_POSTGRES_RETURN_PASSWORD
\getenv procurement_password SAGAFLOW_POSTGRES_PROCUREMENT_PASSWORD

CREATE ROLE sagaflow_order LOGIN PASSWORD :'order_password';
CREATE ROLE sagaflow_return LOGIN PASSWORD :'return_password';
CREATE ROLE sagaflow_procurement LOGIN PASSWORD :'procurement_password';

CREATE DATABASE sagaflow_order OWNER sagaflow_order;
CREATE DATABASE sagaflow_return OWNER sagaflow_return;
CREATE DATABASE sagaflow_procurement OWNER sagaflow_procurement;

REVOKE CONNECT ON DATABASE sagaflow_order, sagaflow_return, sagaflow_procurement FROM PUBLIC;
