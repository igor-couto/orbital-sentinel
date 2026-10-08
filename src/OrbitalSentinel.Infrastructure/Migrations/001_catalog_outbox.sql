CREATE TABLE orbital_objects (
    id uuid PRIMARY KEY,
    norad_catalog_id bigint NULL CHECK (norad_catalog_id > 0),
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    international_designator text NULL,
    object_type text NOT NULL,
    current_version bigint NOT NULL CHECK (current_version > 0),
    updated_at timestamptz NOT NULL
);
CREATE UNIQUE INDEX uq_orbital_objects_norad ON orbital_objects(norad_catalog_id)
    WHERE norad_catalog_id IS NOT NULL;
CREATE INDEX ix_orbital_objects_name ON orbital_objects(lower(name));

CREATE TABLE orbital_element_sets (
    orbital_object_id uuid NOT NULL REFERENCES orbital_objects(id),
    version bigint NOT NULL CHECK (version > 0),
    epoch_utc timestamptz NOT NULL,
    elements jsonb NOT NULL,
    fingerprint text NOT NULL,
    ingested_at timestamptz NOT NULL,
    PRIMARY KEY (orbital_object_id, version)
);

CREATE TABLE outbox_messages (
    id uuid PRIMARY KEY,
    sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
    event_type text NOT NULL,
    orbital_object_id uuid NULL REFERENCES orbital_objects(id),
    orbit_version bigint NULL,
    partition_key text NOT NULL,
    payload jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    published_at timestamptz NULL,
    retry_count integer NOT NULL DEFAULT 0 CHECK (retry_count >= 0),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_error text NULL,
    lease_token uuid NULL,
    lease_expires_at timestamptz NULL,
    dead_lettered_at timestamptz NULL,
    FOREIGN KEY (orbital_object_id, orbit_version) REFERENCES orbital_element_sets(orbital_object_id, version),
    CHECK ((lease_token IS NULL) = (lease_expires_at IS NULL))
);
CREATE INDEX ix_outbox_pending ON outbox_messages(next_attempt_at, sequence)
    WHERE published_at IS NULL AND dead_lettered_at IS NULL;
CREATE INDEX ix_outbox_object_order ON outbox_messages(orbital_object_id, sequence)
    WHERE published_at IS NULL AND dead_lettered_at IS NULL;

CREATE TABLE processed_messages (
    consumer_name text NOT NULL,
    event_id uuid NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (consumer_name, event_id)
);

CREATE TABLE trajectory_predictions (
    orbital_object_id uuid PRIMARY KEY REFERENCES orbital_objects(id),
    orbit_version bigint NOT NULL,
    start_utc timestamptz NOT NULL,
    end_utc timestamptz NOT NULL,
    payload jsonb NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (orbital_object_id, orbit_version) REFERENCES orbital_element_sets(orbital_object_id, version),
    CHECK (end_utc >= start_utc)
);

CREATE TABLE job_schedules (
    name text PRIMARY KEY,
    next_run_at timestamptz NOT NULL
);

CREATE TABLE consumer_failures (
    id uuid PRIMARY KEY,
    topic text NOT NULL,
    partition integer NOT NULL,
    offset_position bigint NOT NULL,
    message_key text NULL,
    payload text NOT NULL,
    error text NOT NULL,
    failed_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (topic, partition, offset_position)
);
