# GridVault

Market data ingestion and serving platform for Ontario electricity market
data (IESO). It ingests public hourly data on a schedule, stores every
revision, and serves it over a REST API that can answer "what did we know
about hour X as of time Y?"

## Why bitemporal

IESO publishes preliminary demand and price figures that get restated hours
or days later. Overwriting the old value destroys the ability to backtest
honestly — a model would see data it couldn't have had at the time. GridVault
stores `(valid_time, transaction_time)` pairs and never mutates history.

See `docs/decisions.md` for the design tradeoffs behind that and other
choices as they're made.

## Stack

- C# / .NET 10
- PostgreSQL 16 via Npgsql + Dapper (no EF Core — this is hand-tuned
  time-series SQL)
- ASP.NET Core minimal APIs for the read side
- Quartz.NET for scheduling inside the worker (Milestone 2)
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

## Local setup

Prerequisites: .NET 10 SDK, Docker.

```bash
cp .env.example .env   # fill in local credentials; .env is gitignored
docker compose up -d   # Postgres 16 + MinIO
```

Set a `ConnectionStrings:GridVault` value (e.g. via
`dotnet user-secrets` or an environment variable) pointing at the Postgres
instance from compose. Running `GridVault.Ingestion` applies pending DbUp
migrations from `src/GridVault.Data/Migrations` on startup.

```bash
dotnet build
dotnet run --project src/GridVault.Api        # health check at /health
dotnet run --project src/GridVault.Ingestion  # runs migrations, schedules the IESO demand fetch job
```

## Testing

```bash
dotnet test
```

`GridVault.IntegrationTests` uses Testcontainers to spin up a real Postgres
instance per run — no local Postgres or `.env` needed for tests, only
Docker.

## Status

GridVault targets one working v1, not a milestone roadmap. Scope: scheduled
ingestion of the IESO hourly demand report, an as-of read endpoint, and CI.
Explicitly not in scope: price data, replay, backfill, and Terraform — see
`docs/decisions.md` for what's been decided so far and why.
