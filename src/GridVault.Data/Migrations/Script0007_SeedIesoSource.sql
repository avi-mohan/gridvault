-- Seeds the IESO source row. The demand series rows are NOT seeded here:
-- series.source_timezone is NOT NULL and we don't yet know whether the
-- demand report is Eastern Prevailing Time or fixed EST (see
-- docs/decisions.md). Seeding series with a guessed timezone would be
-- exactly the "invent a schema and build on it" mistake CLAUDE.md warns
-- against. A follow-up migration adds the demand series once that's
-- confirmed.
INSERT INTO source (name, description, base_url)
VALUES (
    'ieso',
    'Independent Electricity System Operator (Ontario)',
    'https://reports-public.ieso.ca/public/'
);
