# Design decisions

One paragraph per decision: what we chose, what we gave up. Newest at the
bottom.

## 2026-08-13 — NodaTime for time handling

Added NodaTime rather than relying on built-in `TimeZoneInfo`/`DateTime`. The
IESO time-conversion problem (Eastern Prevailing Time, hour-ending
convention, real DST transitions with 23- and 25-hour days) needs types that
make "which kind of timestamp is this" a compile-time distinction — `Instant`
vs `LocalDateTime` vs `ZonedDateTime` — rather than `DateTime`'s ambiguous
`Kind` property. The cost is one more dependency and a small learning curve
for anyone unfamiliar with NodaTime's type system; we think that cost is
worth it for code whose entire job is not getting DST wrong.

## 2026-08-13 — DbUp over FluentMigrator for migrations

Picked DbUp: migrations are plain, ordered `.sql` scripts run in a
transaction and tracked in a journal table, forward-only, no fluent C# DSL.
This matches the project's stance on SQL — hand-tuned, reviewed as SQL, not
generated or wrapped — and "forward-only" extends naturally to "never write
a migration that unwinds a decision," which is the same philosophy as the
append-only fact table. We gave up FluentMigrator's `Down()` migrations and
its cross-database abstraction; neither buys anything here since GridVault
is Postgres-only for its lifetime and rollback-by-migration isn't how we'd
recover from a bad schema change on an append-only history table anyway.

## 2026-08-13 — observation is append-only, not close-on-write bitemporal

Rejected the textbook bitemporal pattern (a `transaction_time_end` column
that gets `UPDATE`d when a new vintage arrives) in favor of pure
append-only: every vintage is an `INSERT`, there is no "close the previous
row" step, and "current value as of X" is computed at read time as the row
with the greatest `transaction_time <= X` for a given
`(series_id, valid_time_start)`. What this costs: every as-of read pays for
a sort/distinct rather than a direct index lookup on a single "current" row.
We're deferring the optimization for that — either a partial index over
just the latest vintage per `(series_id, valid_time_start)`, or a
separate `observation_latest` table maintained incrementally on write — not
because it's unnecessary, but because we don't have real query volume yet
to know which one is worth the complexity. What we avoided taking on: UPDATE
churn (and the resulting autovacuum pressure) on the hottest table in the
system, and a race condition between concurrent ingestion runs both trying
to "close" the same previous row.

## 2026-08-13 — no DEFAULT partition on observation

A `DEFAULT` partition would silently absorb any insert whose
`valid_time_start` falls outside the pre-created monthly ranges. We
deliberately don't have one: an out-of-range insert means either the parser
computed a bad timestamp or partition headroom ran out, and in both cases we
want the ingestion run to fail loudly and visibly rather than have rows
quietly land in an unpartitioned catch-all that then has to be split out
later (attaching a real partition over a range that already has rows sitting
in DEFAULT requires a full table scan and can fail outright if the ranges
conflict). The cost is that we now depend on the partition tripwire
(`PartitionMaintenance.EnsureFuturePartitionsAsync`) actually getting wired
into alerting before we run out of headroom — see the next entry.

## 2026-08-13 — partition range is a placeholder; tripwire deferred to Milestone 3

`Script0005_CreateObservationPartitions.sql` pre-creates monthly partitions
covering 24 months of history and 13 months of headroom from whenever the
migration actually runs. The 24-month historical figure is a guess — we
don't yet know how far back real IESO backfill needs to go, and will revisit
once that's known. Automated partition creation (pg_partman or a scheduled
job) is out of scope for Milestone 1; instead, `PartitionMaintenance` in
`GridVault.Data` exists now as a method
(`EnsureFuturePartitionsAsync(minMonthsAhead)`) that throws if headroom is
short, with an integration test proving it actually throws when asked for
unreasonable headroom. Nothing calls it on a schedule yet — that's Milestone
3's job, once there's an alerting path for it to feed into. Until then this
is a known, accepted gap: the system will work fine until whatever month we
run past the pre-created range.

## 2026-08-13 — retraction/status semantics on observation

Append-only has no way to express "this value was withdrawn" — silence
would read as "unchanged," which is wrong. `observation.value` is nullable
and there's a `status` column (`observed` / `retracted` / `not_published`).
The load-bearing rule, needed for Milestone 2's idempotency check to be
correct: a transition between any two distinct statuses is itself a change
worth a new vintage, even when the numeric value is unchanged or null in
both rows. This is the same "absence must never mean unknown" problem the
append-only design has everywhere else, just applied to presence instead of
value. This rule is now also recorded in `CLAUDE.md` since it's a
correctness constraint the ingestion loader has to honor, not just a schema
detail.

## 2026-08-13 — ingestion_run_id lineage column on observation

Every `observation` row carries the `ingestion_run_id` of the run that wrote
it, and every `ingestion_run` row carries the raw storage key of the payload
it was parsed from. Eight bytes per row buys full lineage: given a
suspicious price, we can get back to the exact bytes IESO served and when we
fetched them. Cheaper to add now, before the table has data, than as a
backfilled column later.

## 2026-08-13 — unique constraint on (series_id, valid_time_start, transaction_time)

Append-only still needs a backstop against a retried write inserting a
literal duplicate row (same series, same hour, same transaction_time). The
unique index includes `valid_time_start` because Postgres requires a
partitioned table's unique indexes to include the partition key — this is a
structural requirement, not a design choice, but worth noting since it's why
the index looks the way it does.

## 2026-08-13 — transaction_time is deterministic, not wall-clock-at-insert

`transaction_time` must never be `DateTime.UtcNow` (or
`SystemClock.Instance.GetCurrentInstant()`) evaluated at write time.
Precedence: the source's own publish timestamp when the report carries one,
otherwise the `fetched_at` instant encoded in the raw storage key for that
ingestion run. Wall-clock-at-insert would make every vintage's timestamp
depend on when we happened to run the loader, not on when the fact was
actually known — which breaks Milestone 2's "replay from raw storage
reproduces identical final state" requirement outright, since a replay run
today would stamp a July revision as an August one. This is now in
`CLAUDE.md`'s rules section since every correctness claim about the
bitemporal model downstream depends on it.

## 2026-08-13 — Quartz.NET and OpenTelemetry not wired yet

The stack list includes Quartz.NET (scheduling) and OpenTelemetry
(tracing/metrics), but Milestone 1 is explicitly "no ingestion yet" — there
are no jobs to schedule and nothing meaningful to trace. Adding either now
would mean configuring infrastructure with nothing behind it, which is the
"no half-finished implementations" rule applied to dependencies, not just
code. Serilog is wired in both hosts now since structured logging is useful
immediately (migration output, request logs) and costs nothing to configure
early. Quartz and OpenTelemetry land with Milestone 2, when there's an
actual job and an actual request path to instrument.

## 2026-08-13 — Instant<->timestamptz mapping registered centrally, not per call site

`Npgsql.NodaTime`'s `UseNodaTime()` (enabled once, in `GridVaultDataSource.Create`)
covers the ADO.NET provider layer — a `timestamptz` column already comes back
from the reader as a boxed `Instant`, so Dapper's column deserializer just
casts it. Writing a parameter is a separate Dapper code path: Dapper resolves
each parameter's `DbType` itself via a hardcoded switch over known CLR types
before Npgsql ever sees the value, doesn't recognize `Instant`, and throws
`NotSupportedException` rather than guess. `NodaTimeDapperTypeHandlers`
registers a `SqlMapper.TypeHandler<Instant>` that does no conversion of its
own — it sets `NpgsqlDbType.TimestampTz` and hands the `Instant` straight to
Npgsql, so the plugin still does the actual marshalling. Registration lives
inside `GridVaultDataSource.Create` specifically so there is exactly one
place that can drift out of sync between the app and the tests: every host
(API, Ingestion, integration tests) already calls that factory to get a data
source, so there's no separate step to remember. A round-trip test
(`NodaTimeTypeMappingTests`) writes an `Instant` through a real `timestamptz`
column and asserts it comes back identical, so a regression here fails on
that specific assertion rather than as an opaque query error somewhere else.

## 2026-08-13 — pinned SSH.NET to 2026.0.0 in GridVault.IntegrationTests

`Testcontainers.PostgreSql` 4.13.0 (latest) transitively pulls `SSH.NET`
2025.1.0, which has a known high-severity CVE
(GHSA-q939-rpr3-3284); our `Directory.Build.props`
warnings-as-errors setting correctly failed the build on NuGet's audit
warning (NU1903) rather than letting it slide. Added a direct
`PackageReference` to `SSH.NET` 2026.0.0, which NuGet's nearest-wins
resolution picks over the transitive version. This should be revisited (the
pin removed) once Testcontainers bumps its own dependency.

## 2026-08-20 — demand report is fixed EST (UTC-5), not Eastern Prevailing Time

Confirmed empirically that the hourly demand report's hour-ending data is
fixed EST year-round, not `America/Toronto` with DST, despite the original
assumption in early drafts of `CLAUDE.md`. Evidence: every archived DST
transition date checked (2018-03-11, 2018-11-04, 2020-03-08, 2020-11-01,
2021-03-14, 2021-11-07, and 2026-03-08 as originally flagged) has exactly 24
rows in the file — a true EPT hour-ending series would show 23 rows on
spring-forward and 25 on fall-back, never 24 on both. IESO's own report
documentation labels hours in this report as EST. Independently corroborated
via the file's HTTP `Last-Modified` header (RFC-mandated GMT) against the
report's internal `Created at` timestamp across three consecutive days in
August 2026 (mid-DST, when EPT and EST diverge by an hour): the offset was
consistently ~5h01m (Aug 18/19/20), matching UTC-5 exactly rather than the
UTC-4 that EPT would show during DST. `series.source_timezone` for the
demand series is `Etc/GMT+5` (fixed offset), not `America/Toronto`. This
also confirms timezone is genuinely a per-series property, not a
project-wide constant: the post-Market-Renewal Program Day-Ahead Market
report is expected to operate in true Eastern Prevailing Time, so a global
assumption would have been wrong for at least one report we already know
about.

## 2026-08-20 — Created-at header's zone is a fact separate from the data rows' zone

The demand report's `Created at` header becomes `transaction_time` under the
source-publish-timestamp precedence rule (see `CLAUDE.md`), and it's a naked
local timestamp with no zone marker of its own. It is not automatically the
same field as the zone the `Date`/`Hour` data rows are published in, even
though for this report both happen to resolve to UTC-5 today — one is a
report-generation timestamp, the other is a market-data convention, and
nothing guarantees a future report keeps them equal. We chose not to add a
schema column for the header's zone: `series.source_timezone` is documented
as covering only the data rows' (valid_time) zone, and the demand loader
hardcodes UTC-5 for the header specifically, established by the same
`Last-Modified` evidence above. What we gave up is generality — if a future
report's header zone turns out to differ from its data zone, this will need
a real column rather than a per-source constant. We're accepting that now
rather than generalizing for a case we don't have evidence of yet.
