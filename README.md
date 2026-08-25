# GridVault

Market data ingestion and serving platform for Ontario's electricity market
(IESO). It ingests public hourly data on a schedule, stores every revision,
and serves it over a REST API that can answer "what did we know about hour
X as of time Y?"

IESO publishes preliminary demand figures that get restated hours or days
later. Overwriting the old value on restatement would destroy the ability
to backtest honestly — a model would end up seeing data it couldn't
actually have had at the time. GridVault stores `(valid_time,
transaction_time)` pairs and never mutates history: every restatement is a
new row, and "current value as of some past instant" is a query, not a
lookup.

Design tradeoffs as they come up are recorded in
**[`docs/decisions.md`](docs/decisions.md)** — one paragraph per nontrivial
decision, what was chosen and what was given up. That file is the most
useful thing in this repo if you want to understand *why* something looks
the way it does, not just what it does.

## Quickstart

```bash
cp .env.example .env   # fill in local credentials; .env is gitignored
docker compose up -d   # Postgres 16 + MinIO
```

Set `ConnectionStrings:GridVault` (e.g. via `dotnet user-secrets` or an
environment variable) pointing at the Postgres instance from compose, then:

```bash
dotnet run --project src/GridVault.Ingestion  # applies migrations, seeds series, schedules the daily IESO fetch
dotnet run --project src/GridVault.Api        # read API on http://localhost:5236
```

The ingestion worker applies migrations immediately but the IESO fetch
itself runs on a daily UTC cron — right after a fresh `docker compose up`
there's schema and series metadata but no observations yet, so the API
answers with an empty list rather than an error:

```bash
$ curl "http://localhost:5236/series/ieso.demand.market/observations?from=2026-08-01T00:00:00Z&to=2026-08-01T04:00:00Z"
{"series_code":"ieso.demand.market","as_of":"2026-08-25T19:30:02Z","from":"2026-08-01T00:00:00Z","to":"2026-08-01T04:00:00Z","observations":[]}
```

Once the ingestion job has actually run — or against a database seeded with
real ingested data, as below — the same endpoint returns actual vintages:

```bash
$ curl "http://localhost:5236/series/ieso.demand.ontario/observations?from=2026-08-01T00:00:00Z&to=2026-08-01T04:00:00Z&as_of=2026-08-25T00:00:00Z"
{"series_code":"ieso.demand.ontario","as_of":"2026-08-25T00:00:00Z","from":"2026-08-01T00:00:00Z","to":"2026-08-01T04:00:00Z","observations":[{"valid_time_start":"2026-08-01T00:00:00Z","value":19042,"status":"observed","transaction_time":"2026-08-02T09:00:00Z","ingestion_run_id":1},{"valid_time_start":"2026-08-01T01:00:00Z","value":19338,"status":"observed","transaction_time":"2026-08-02T10:00:00Z","ingestion_run_id":1},{"valid_time_start":"2026-08-01T02:00:00Z","value":15371,"status":"observed","transaction_time":"2026-08-02T11:00:00Z","ingestion_run_id":1},{"valid_time_start":"2026-08-01T03:00:00Z","value":16385,"status":"observed","transaction_time":"2026-08-02T12:00:00Z","ingestion_run_id":1}]}
```

`from`/`to` are a half-open `[from, to)` range over `valid_time_start`,
`as_of` is optional (defaults to now) and inclusive against
`transaction_time`. Every timestamp needs an explicit UTC offset — a naive
one is rejected:

```bash
$ curl -w '\nHTTP %{http_code}\n' "http://localhost:5236/series/ieso.demand.ontario/observations?from=2026-08-01T00:00:00&to=2026-08-01T04:00:00"
{"error":"'from': must be an ISO-8601 timestamp with an explicit UTC offset (e.g. '2026-08-01T00:00:00Z'). Got '2026-08-01T00:00:00'."}
HTTP 400
```

`as_of` answers "what had IESO published by then", not "what had GridVault
itself fetched by then" — see the "as-of read endpoint" entry (2026-08-25)
in [`docs/decisions.md`](docs/decisions.md) for why, and what that gap
actually means for a backtest.

## Stack

- C# / .NET 10
- PostgreSQL 16 via Npgsql + Dapper (no EF Core — this is hand-tuned
  time-series SQL)
- ASP.NET Core minimal APIs for the read side
- Quartz.NET for scheduling inside the worker
- Serilog (structured JSON) + OpenTelemetry
- Docker Compose for local dev: Postgres, MinIO (S3 stand-in)
- xUnit + Testcontainers for integration tests, WireMock.NET for upstream
  stubs

## Layout

```
src/
  GridVault.Domain/        # entities, value objects, no I/O
  GridVault.Ingestion/     # worker service: fetch -> land -> parse -> load
  GridVault.Api/           # read-side REST API
  GridVault.Data/          # Postgres access, migrations, repositories
tests/
  GridVault.UnitTests/
  GridVault.IntegrationTests/
```

Ingestion is strictly: fetch raw -> write immutable raw payload to object
storage -> parse -> upsert into Postgres. The raw landing step is
non-negotiable; it's what makes replay possible without re-hitting the
source.

## Timezones are a per-series property, not a global assumption

The hourly demand report's data rows are fixed EST (UTC-5) year-round with
no DST transitions, confirmed empirically rather than assumed — see the
"demand report is fixed EST" entry (2026-08-20) in
[`docs/decisions.md`](docs/decisions.md) for the evidence. Other IESO
reports (e.g. the post-Market-Renewal Day-Ahead Market) are expected to use
true Eastern Prevailing Time instead, which is why `series.source_timezone`
is data on each series row rather than a project-wide constant.

## Testing

```bash
dotnet test
```

`GridVault.IntegrationTests` uses Testcontainers to spin up a real Postgres
instance per run — no local Postgres or `.env` needed for tests, only
Docker.

## Not yet built

v1's scope is deliberately narrow: scheduled ingestion of the IESO hourly
demand report, an as-of read endpoint, and CI. Deliberately scoped out for
now, not missing by oversight:

- **Pagination** on the observations endpoint — the 90-day range cap keeps
  a single response to ~2,160 rows, so it hasn't been needed yet.
- **OpenTelemetry** tracing/metrics — listed in the stack but not wired in;
  there's now an actual request path and an actual ingestion job to
  instrument, so this is next.
- **Scheduled partition maintenance** — `PartitionMaintenance.EnsureFuturePartitionsAsync`
  exists and is tested but nothing calls it on a schedule yet; the
  pre-created partition range is a guessed placeholder.
- **Price series** — demand only for v1.
- **Replay** — re-ingesting from previously-landed raw payloads to
  reproduce historical vintages. The raw landing step already makes this
  possible; the replay driver itself doesn't exist yet.
- **Backfill** — populating history predating GridVault's own ingestion
  start.
- **Auth** on the API.

See [`docs/decisions.md`](docs/decisions.md) for the reasoning behind
what's already built, including tradeoffs that are worth knowing about even
though they didn't need code changes to resolve (e.g. the as-of query's
current EXPLAIN plan).
