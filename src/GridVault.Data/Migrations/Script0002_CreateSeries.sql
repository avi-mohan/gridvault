CREATE TABLE series
(
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_id       bigint NOT NULL REFERENCES source (id),
    code            text NOT NULL UNIQUE,
    name            text NOT NULL,
    unit            text NOT NULL,
    cadence         interval NOT NULL,
    source_timezone text NOT NULL,
    hour_convention text NOT NULL CHECK (hour_convention IN ('ending', 'beginning')),
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_series_source ON series (source_id);
