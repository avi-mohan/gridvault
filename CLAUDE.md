# GridVault

Market data ingestion and serving platform for Ontario electricity market data
(IESO). It ingests public hourly data on a schedule, stores every revision, and
serves it over a REST API that can answer "what did we know about hour X as of
time Y?"

## Why the bitemporal part matters

IESO publishes preliminary demand and price figures that get restated hours or
days later. Overwriting the old value destroys the ability to backtest
honestly — a model would see data it couldn't have had at the time. So we
store (valid_time, transaction_time) pairs and never mutate history.

## Stack

- C# / .NET 10 (current LTS as of writing — confirmed via `dotnet --list-sdks`,
  which reported 10.0.302 installed locally). Re-check this if it's been a
  while; .NET LTS releases land every two years on even-numbered versions.
- PostgreSQL 16, accessed via Npgsql + Dapper. **Not EF Core** — this is bulk
  time-series ingest and hand-tuned SQL, and query plans need to be
  intentional.
- ASP.NET Core minimal APIs for the read side
- Quartz.NET for scheduling inside the worker
- Serilog (structured, JSON sink) + OpenTelemetry (not wired in yet — see
  docs/decisions.md and README's "Not yet built")
- Docker Compose for local dev: Postgres, MinIO (S3 stand-in). The worker
  and API run via `dotnet run`, not as compose services.
- xUnit + Testcontainers for integration tests, WireMock.NET for upstream
  stubs
- GitHub Actions for CI. Terraform stays out of scope.

## Architecture

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

## Rules I care about

- Every timestamp stored is UTC, typed `timestamptz`. Never `timestamp`.
- The hourly demand report is fixed EST (UTC-5) year-round, hour-ending, with
  **no DST transitions** — every archived DST transition date has exactly 24
  rows, never 23 or 25. Do not assume Eastern Prevailing Time for this
  report. Timezone is a per-series property (`series.source_timezone`), not
  a global assumption, because it is not uniform across IESO reports: the
  post-Market-Renewal Program Day-Ahead Market does operate in Eastern
  Prevailing Time. Conversion happens once, at the parse boundary, in one
  clearly-named place per zone. See `docs/decisions.md` for how the demand
  report's zone was established.
- `transaction_time` on an observation row is deterministic, never
  wall-clock-at-insert (`DateTime.UtcNow`/`SystemClock.GetCurrentInstant()`
  at write time). Precedence: use the source's own publish timestamp when
  the report carries one; otherwise use the `fetched_at` instant encoded in
  the raw storage key for that ingestion run. This is what makes replay
  reproducible — re-ingesting from landed raw payloads six months from now
  must produce the same vintages with the same timestamps, not new ones
  stamped with today's date.
- Ingestion must be idempotent. Re-running any window produces no duplicates
  and no spurious new vintages — a revision row is only written when the
  value actually differs from the latest known one, **or when its status
  changes** (observed/retracted/not_published are distinct states; a
  transition between any two of them is a real change worth a new vintage
  even if the numeric value is unchanged or null in both).
- No `dynamic`, no swallowed exceptions, no `async void`. Nullable reference
  types on.
- If you're unsure about an IESO data format or endpoint, say so and ask — do
  not invent a schema and build on it.

## How to work on this repo

- Small commits, conventional commit messages.
- Prefer boring, obvious code.
- When you make a design tradeoff, add a one-paragraph note to
  `docs/decisions.md` explaining what you chose and what you gave up.
