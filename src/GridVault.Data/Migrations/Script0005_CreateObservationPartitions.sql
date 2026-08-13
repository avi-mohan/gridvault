-- Pre-creates monthly partitions of observation covering 24 months of
-- history and 13 months of headroom from whenever this migration actually
-- runs. This range is a placeholder — we don't yet know how far back real
-- IESO backfill needs to go. See docs/decisions.md.
--
-- There is no DEFAULT partition (see Script0004), so anything past this
-- range fails loudly on insert. GridVault.Data's partition tripwire
-- (PartitionMaintenance) is meant to catch the "running out of headroom"
-- case before it happens — see docs/decisions.md and Milestone 3.
DO $$
DECLARE
    partition_start date := date_trunc('month', now() - interval '24 months');
    partition_end   date := date_trunc('month', now() + interval '13 months');
    cursor_date     date := partition_start;
    partition_name  text;
BEGIN
    WHILE cursor_date < partition_end LOOP
        partition_name := format('observation_%s', to_char(cursor_date, 'YYYY_MM'));

        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS %I PARTITION OF observation FOR VALUES FROM (%L) TO (%L)',
            partition_name,
            cursor_date,
            cursor_date + interval '1 month'
        );

        cursor_date := cursor_date + interval '1 month';
    END LOOP;
END;
$$;
