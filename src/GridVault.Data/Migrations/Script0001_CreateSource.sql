CREATE TABLE source
(
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        text NOT NULL UNIQUE,
    description text,
    base_url    text,
    created_at  timestamptz NOT NULL DEFAULT now()
);
