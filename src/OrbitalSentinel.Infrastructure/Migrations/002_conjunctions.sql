-- Runs persist immutable orbital-element manifests, not propagated sample arrays.
CREATE TABLE conjunction_scopes (
    scope_key text PRIMARY KEY
);

CREATE TABLE prediction_runs (
    id uuid PRIMARY KEY,
    sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
    request_key text NOT NULL UNIQUE,
    scope_key text NOT NULL REFERENCES conjunction_scopes(scope_key),
    source text NOT NULL CHECK (source IN ('Real','Simulation')),
    scenario_id uuid NULL,
    created_at timestamptz NOT NULL,
    prediction_start timestamptz NOT NULL,
    prediction_end timestamptz NOT NULL,
    manifest jsonb NOT NULL,
    status text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Running','Completed','Failed')),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    last_error text NULL,
    completed_at timestamptz NULL,
    metrics jsonb NULL,
    CHECK (prediction_end > prediction_start),
    CHECK ((source='Real' AND scenario_id IS NULL) OR (source='Simulation' AND scenario_id IS NOT NULL))
);
CREATE INDEX ix_prediction_runs_scope ON prediction_runs(scope_key,sequence);

CREATE TABLE conjunction_scan_jobs (
    prediction_run_id uuid PRIMARY KEY REFERENCES prediction_runs(id),
    event_id uuid NOT NULL UNIQUE,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    lease_token uuid NULL,
    lease_expires_at timestamptz NULL,
    CHECK ((lease_token IS NULL) = (lease_expires_at IS NULL))
);

CREATE TABLE conjunctions (
    id uuid PRIMARY KEY,
    scope_key text NOT NULL REFERENCES conjunction_scopes(scope_key),
    source text NOT NULL CHECK (source IN ('Real','Simulation')),
    scenario_id uuid NULL,
    primary_object_id uuid NOT NULL,
    secondary_object_id uuid NOT NULL,
    primary_name text NOT NULL,
    secondary_name text NOT NULL,
    primary_norad_id bigint NULL,
    secondary_norad_id bigint NULL,
    first_identified_at timestamptz NOT NULL,
    latest_assessment_id uuid NULL,
    CHECK (primary_object_id < secondary_object_id)
);
CREATE INDEX ix_conjunction_pair ON conjunctions(scope_key,primary_object_id,secondary_object_id);

CREATE TABLE conjunction_assessments (
    id uuid PRIMARY KEY,
    conjunction_id uuid NOT NULL REFERENCES conjunctions(id),
    prediction_run_id uuid NOT NULL REFERENCES prediction_runs(id),
    primary_object_id uuid NOT NULL,
    secondary_object_id uuid NOT NULL,
    encounter_ordinal integer NOT NULL CHECK (encounter_ordinal >= 0),
    detected_at timestamptz NOT NULL,
    tca_utc timestamptz NOT NULL,
    miss_distance_meters double precision NOT NULL CHECK (miss_distance_meters >= 0 AND miss_distance_meters < 'Infinity'::float8),
    relative_velocity_meters_per_second double precision NOT NULL CHECK (relative_velocity_meters_per_second >= 0 AND relative_velocity_meters_per_second < 'Infinity'::float8),
    payload jsonb NOT NULL,
    UNIQUE (conjunction_id,prediction_run_id),
    UNIQUE (prediction_run_id,primary_object_id,secondary_object_id,encounter_ordinal),
    CHECK (primary_object_id < secondary_object_id)
);
ALTER TABLE conjunctions ADD CONSTRAINT fk_conjunction_latest_assessment
    FOREIGN KEY (latest_assessment_id) REFERENCES conjunction_assessments(id);
CREATE INDEX ix_conjunction_assessments_run ON conjunction_assessments(prediction_run_id,tca_utc);
CREATE INDEX ix_conjunction_assessments_history ON conjunction_assessments(conjunction_id,detected_at DESC);
