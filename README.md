# GridVault

[![CI](https://github.com/avi-mohan/gridvault/actions/workflows/ci.yml/badge.svg)](https://github.com/avi-mohan/gridvault/actions/workflows/ci.yml)

GridVault is a market data platform for Ontario's electricity market
(IESO), built around one requirement: a backtest must never see a number
before it was actually knowable. It ingests public hourly data on a
schedule, stores every revision, and serves it over a REST API that
answers "what did we know about hour X as of time Y?" — the question that
keeps lookahead bias out of a backtest.

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

IESO republished 2026-08-17's hour-ending-1 Ontario demand a day later
with a revised value — both vintages below are real ingested data, landed
by two separate daily runs. This is the entire pitch of the project in two
commands:

```bash
$ curl "http://localhost:5236/series/ieso.demand.ontario/observations?from=2026-08-17T05:00:00Z&to=2026-08-17T06:00:00Z&as_of=2026-08-17T12:30:11Z"
{"series_code":"ieso.demand.ontario","as_of":"2026-08-17T12:30:11Z","from":"2026-08-17T05:00:00Z","to":"2026-08-17T06:00:00Z","observations":[{"valid_time_start":"2026-08-17T05:00:00Z","value":16615,"status":"observed","transaction_time":"2026-08-17T12:30:10Z","ingestion_run_id":1}]}

$ curl "http://localhost:5236/series/ieso.demand.ontario/observations?from=2026-08-17T05:00:00Z&to=2026-08-17T06:00:00Z&as_of=2026-08-18T12:30:10Z"
{"series_code":"ieso.demand.ontario","as_of":"2026-08-18T12:30:10Z","from":"2026-08-17T05:00:00Z","to":"2026-08-17T06:00:00Z","observations":[{"valid_time_start":"2026-08-17T05:00:00Z","value":16190,"status":"observed","transaction_time":"2026-08-18T12:30:09Z","ingestion_run_id":2}]}
```

Same hour, same `from`/`to`, different `as_of`: the first query lands
between the two publish times and returns the original 16615; the second
lands after the revision and returns 16190. Note the two different
`ingestion_run_id`s — `IesoDemandFetchJob` fetches exactly one file (one
`Created at` header) per run, so a same-hour revision means two runs here.
That's a property of this job's shape, not a rule the schema enforces: a
future replay run ingesting several previously-landed payloads in one
execution would write several `transaction_time`s under a single
`ingestion_run_id`.

`from`/`to` are a half-open `[from, to)` range over `valid_time_start`,
capped at 90 days. `as_of` is optional (defaults to now) and inclusive
against `transaction_time`. Every timestamp needs an explicit offset — a
naive one is rejected:

```bash
$ curl -w '\nHTTP %{http_code}\n' "http://localhost:5236/series/ieso.demand.ontario/observations?from=2026-08-17T00:00:00&to=2026-08-18T00:00:00"
{"error":"'from': must be an ISO-8601 timestamp with an explicit offset (e.g. '2026-08-01T00:00:00Z'). Got '2026-08-17T00:00:00'."}
HTTP 400
```

`as_of` answers "what had IESO published by then", not "what had GridVault
itself fetched by then" — see the ["as-of read endpoint" entry](docs/decisions.md#2026-08-25--the-as-of-read-endpoint-answers-what-had-ieso-published-not-what-had-gridvault-fetched)
(2026-08-25) in `docs/decisions.md` for why, and what that gap actually
means for a backtest.

The ingestion worker applies migrations immediately but the IESO fetch
itself runs on a daily UTC cron, so if you've only just run `docker compose
up` with no ingestion runs yet, expect an empty list rather than the above
— the series exists (migrations seed it) but nothing has been fetched:

```bash
$ curl "http://localhost:5236/series/ieso.demand.ontario/observations?from=2024-09-01T00:00:00Z&to=2024-09-02T00:00:00Z"
{"series_code":"ieso.demand.ontario","as_of":"2026-08-26T18:13:49.7174696Z","from":"2024-09-01T00:00:00Z","to":"2024-09-02T00:00:00Z","observations":[]}
```

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
["demand report is fixed EST" entry](docs/decisions.md#2026-08-20--demand-report-is-fixed-est-utc-5-not-eastern-prevailing-time)
(2026-08-20) in `docs/decisions.md` for the evidence. Other IESO
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
demand report and an as-of read endpoint, both built, tested, and running
in CI. Every item below was cut on purpose, not forgotten — two of them
have a full tradeoff write-up in `docs/decisions.md`, linked below; the
rest have their reasoning stated right in the bullet, or (for the last
three) no scope decision at all, just not built yet.

### Scoped out, with reasoning recorded

- **Pagination** on the observations endpoint — the 90-day range cap keeps
  a single response to ~2,160 rows, so it hasn't been needed yet.
- **OpenTelemetry** tracing/metrics — listed in the stack but not wired in;
  see the ["Quartz.NET and OpenTelemetry not wired yet"](docs/decisions.md#2026-08-13--quartznet-and-opentelemetry-not-wired-yet)
  entry (Quartz has since landed; OpenTelemetry hasn't).
- **Scheduled partition maintenance** — `PartitionMaintenance.EnsureFuturePartitionsAsync`
  exists and is tested but nothing calls it on a schedule yet; see the
  ["partition range is a placeholder"](docs/decisions.md#2026-08-13--partition-range-is-a-placeholder-tripwire-deferred-to-milestone-3)
  entry.
- **Price series** — demand only for v1.

### Not there yet — no scope decision behind these, just time

- **Replay** — re-ingesting from previously-landed raw payloads to
  reproduce historical vintages. The raw landing step already makes this
  possible; the replay driver itself doesn't exist yet.
- **Backfill** — populating history predating GridVault's own ingestion
  start.
- **Auth** on the API.

See [`docs/decisions.md`](docs/decisions.md) for the reasoning behind
what's already built — including the as-of query's current `EXPLAIN` plan,
which is written up there.
