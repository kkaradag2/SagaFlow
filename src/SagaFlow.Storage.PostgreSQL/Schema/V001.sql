CREATE TABLE sagaflow.instances (
    id text COLLATE "C" CONSTRAINT pk_sagaflow_instances PRIMARY KEY,
    revision bigint NOT NULL CHECK (revision > 0),
    document json NOT NULL
);

CREATE TABLE sagaflow.incoming_invocations (
    id uuid PRIMARY KEY,
    target_id text COLLATE "C" UNIQUE REFERENCES sagaflow.instances (id),
    decision json
);
