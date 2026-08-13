CREATE TABLE ingestion_run
(
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_id       bigint NOT NULL REFERENCES source (id),
    -- Nullable: a single fetch can cover more than one series (e.g. an IESO
    -- report with multiple zones in one payload).
    series_id       bigint REFERENCES series (id),
    window_start    timestamptz NOT NULL,
    window_end      timestamptz NOT NULL,
    status          text NOT NULL CHECK (status IN ('running', 'succeeded', 'failed', 'partial')),
    started_at      timestamptz NOT NULL,
    finished_at     timestamptz,
    rows_fetched    integer,
    rows_written    integer,
    -- Pointer to the immutable raw payload landed in object storage for this
    -- run. Also the fallback source for transaction_time when the upstream
    -- report carries no publish timestamp of its own.
    raw_storage_key text,
    error_detail    text
);

CREATE INDEX ix_ingestion_run_series_window ON ingestion_run (series_id, window_start);
