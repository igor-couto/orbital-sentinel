CREATE TABLE catalog_metadata (
    norad_catalog_id bigint NOT NULL CHECK (norad_catalog_id > 0),
    group_name text NOT NULL,
    payload jsonb NOT NULL,
    retrieved_at timestamptz NOT NULL,
    PRIMARY KEY (norad_catalog_id, group_name)
);

-- Refresh legacy visualization samples once so the panel can use SGP4 speed and altitude.
UPDATE job_schedules SET next_run_at = now() WHERE name = 'trajectory-refresh';
