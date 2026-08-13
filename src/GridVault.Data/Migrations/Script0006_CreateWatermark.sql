CREATE TABLE watermark
(
    series_id                 bigint PRIMARY KEY REFERENCES series (id),
    last_processed_window_end timestamptz NOT NULL,
    status                    text NOT NULL DEFAULT 'ok',
    updated_at                timestamptz NOT NULL DEFAULT now()
);
