-- Etc/GMT+5 is POSIX-sign-inverted: despite the "+5" in the name, tzdata's
-- Etc/GMT+N zones resolve to UTC-N (POSIX's convention runs backwards from
-- the everyday "+N is east of UTC" reading). Etc/GMT+5 therefore resolves
-- to fixed UTC-5, i.e. EST year-round with no DST -- which is what the
-- demand report actually uses, confirmed empirically (see
-- docs/decisions.md, "demand report is fixed EST"). Deliberately NOT
-- America/Toronto: that zone observes DST and would be wrong here.
INSERT INTO series (source_id, code, name, unit, cadence, source_timezone, hour_convention)
VALUES
    (
        (SELECT id FROM source WHERE name = 'ieso'),
        'ieso.demand.market',
        'IESO Market Demand',
        'MW',
        interval '1 hour',
        'Etc/GMT+5',
        'ending'
    ),
    (
        (SELECT id FROM source WHERE name = 'ieso'),
        'ieso.demand.ontario',
        'IESO Ontario Demand',
        'MW',
        interval '1 hour',
        'Etc/GMT+5',
        'ending'
    );
