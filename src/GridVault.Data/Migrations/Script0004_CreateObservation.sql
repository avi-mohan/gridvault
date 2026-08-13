-- Append-only bitemporal fact table. There is no transaction_time_end and no
-- UPDATE ever touches this table: a new vintage is a new row. "Current value
-- as of X" is computed at read time by taking the row with the greatest
-- transaction_time <= X for a given (series_id, valid_time_start). See
-- docs/decisions.md for why, and what it costs.
--
-- status captures presence, not just value, so that a transition to/from
-- 'retracted' or 'not_published' is itself a new vintage even when the
-- numeric value is unchanged or null in both rows. Absence of a new row
-- must always mean "unchanged", never "unknown".
--
-- Deliberately no DEFAULT partition: an insert that lands outside the
-- pre-created monthly range should fail the ingestion run loudly rather than
-- silently collect in a catch-all partition. See docs/decisions.md.
CREATE TABLE observation
(
    id               bigint GENERATED ALWAYS AS IDENTITY,
    series_id        bigint NOT NULL REFERENCES series (id),
    valid_time_start timestamptz NOT NULL,
    valid_time_end   timestamptz NOT NULL,
    transaction_time timestamptz NOT NULL,
    value            numeric,
    status           text NOT NULL CHECK (status IN ('observed', 'retracted', 'not_published')),
    ingestion_run_id bigint NOT NULL REFERENCES ingestion_run (id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id, valid_time_start)
) PARTITION BY RANGE (valid_time_start);

-- Idempotency backstop: a retried write for the same run can't insert a
-- literal duplicate. valid_time_start (the partition key) is required to be
-- part of any unique index on a partitioned table.
CREATE UNIQUE INDEX ux_observation_series_valid_txn
    ON observation (series_id, valid_time_start, transaction_time);

-- Serves the as-of query directly: DISTINCT ON (valid_time_start) ...
-- WHERE transaction_time <= @asOf ORDER BY valid_time_start, transaction_time DESC.
CREATE INDEX ix_observation_asof
    ON observation (series_id, valid_time_start, transaction_time DESC);
