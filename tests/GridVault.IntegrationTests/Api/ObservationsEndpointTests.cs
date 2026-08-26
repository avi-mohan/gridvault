using System.Net;
using System.Net.Http.Json;
using Dapper;
using GridVault.Api.Series;
using GridVault.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NodaTime;
using NodaTime.Text;
using Npgsql;

namespace GridVault.IntegrationTests.Api;

[Collection(nameof(PostgresCollection))]
public class ObservationsEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ObservationsEndpointTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:GridVault"] = _fixture.ConnectionString,
                })));

        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task GetObservations_AsOf_ReturnsTheVintageThatWasCurrentAtThatInstant()
    {
        var seriesCode = $"test.asof.{Guid.NewGuid():N}";
        await using var dataSource = GridVaultDataSource.Create(_fixture.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();

        var (sourceId, seriesId) = await SeedSeriesAsync(connection, seriesCode);

        var now = SystemClock.Instance.GetCurrentInstant();
        var validStart = now - Duration.FromDays(1);
        var validEnd = validStart + Duration.FromHours(1);
        var t1 = validStart + Duration.FromMinutes(5); // preliminary
        var t2 = t1 + Duration.FromHours(3);            // revised

        var runId = await InsertIngestionRunAsync(connection, sourceId, seriesId, t2);
        await InsertObservationAsync(connection, seriesId, validStart, validEnd, t1, 1000m, "observed", runId);
        await InsertObservationAsync(connection, seriesId, validStart, validEnd, t2, 1050m, "observed", runId);

        var from = FormatInstant(validStart);
        var to = FormatInstant(validEnd);

        // 1. as_of between the two vintages -- the preliminary value.
        var betweenVintages = await _client.GetFromJsonAsync<ObservationsResponse>(
            $"/series/{seriesCode}/observations?from={from}&to={to}&as_of={FormatInstant(t1 + Duration.FromMinutes(1))}");

        // 2. as_of after both -- the revised value.
        var afterBothVintages = await _client.GetFromJsonAsync<ObservationsResponse>(
            $"/series/{seriesCode}/observations?from={from}&to={to}&as_of={FormatInstant(t2 + Duration.FromMinutes(1))}");

        Assert.Equal(1000m, Assert.Single(betweenVintages!.Observations).Value);
        Assert.Equal("observed", Assert.Single(betweenVintages.Observations).Status);
        Assert.Equal(1050m, Assert.Single(afterBothVintages!.Observations).Value);
    }

    [Fact]
    public async Task GetObservations_AsOfExactlyEqualsTransactionTime_IncludesTheVintage()
    {
        // as_of is documented as inclusive (transaction_time <= as_of); a
        // vintage published at exactly the boundary instant must still be
        // visible, not excluded by an off-by-one.
        var seriesCode = $"test.boundary.{Guid.NewGuid():N}";
        await using var dataSource = GridVaultDataSource.Create(_fixture.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();

        var (sourceId, seriesId) = await SeedSeriesAsync(connection, seriesCode);

        var now = SystemClock.Instance.GetCurrentInstant();
        var validStart = now - Duration.FromDays(1);
        var validEnd = validStart + Duration.FromHours(1);
        var transactionTime = validStart + Duration.FromMinutes(5);

        var runId = await InsertIngestionRunAsync(connection, sourceId, seriesId, transactionTime);
        await InsertObservationAsync(connection, seriesId, validStart, validEnd, transactionTime, 1234m, "observed", runId);

        var response = await _client.GetFromJsonAsync<ObservationsResponse>(
            $"/series/{seriesCode}/observations?from={FormatInstant(validStart)}&to={FormatInstant(validEnd)}" +
            $"&as_of={FormatInstant(transactionTime)}");

        Assert.Equal(1234m, Assert.Single(response!.Observations).Value);
    }

    [Fact]
    public async Task GetObservations_SubSecondTransactionTime_RoundTripsThroughEchoedAsOfAndTransactionTime()
    {
        // The response echoes as_of (defaulted from SystemClock, which has
        // sub-second precision) and each row's transaction_time. A client
        // that feeds either straight back as as_of must get the same row
        // back -- if formatting floors the fraction, transaction_time <=
        // as_of can flip from true to false for a value copied verbatim
        // from this same response.
        var seriesCode = $"test.subsecond.{Guid.NewGuid():N}";
        await using var dataSource = GridVaultDataSource.Create(_fixture.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();

        var (sourceId, seriesId) = await SeedSeriesAsync(connection, seriesCode);

        var now = SystemClock.Instance.GetCurrentInstant();
        var validStart = now - Duration.FromDays(1);
        var validEnd = validStart + Duration.FromHours(1);
        // Well in the past relative to "now" so it's covered regardless of
        // how long the test takes to reach the no-as_of request below.
        var transactionTime = validStart + Duration.FromMinutes(5) + Duration.FromMilliseconds(437);

        var runId = await InsertIngestionRunAsync(connection, sourceId, seriesId, transactionTime);
        await InsertObservationAsync(connection, seriesId, validStart, validEnd, transactionTime, 4321m, "observed", runId);

        var from = FormatInstant(validStart);
        var to = FormatInstant(validEnd);

        var initial = await _client.GetFromJsonAsync<ObservationsResponse>(
            $"/series/{seriesCode}/observations?from={from}&to={to}");
        var observation = Assert.Single(initial!.Observations);
        Assert.Equal(4321m, observation.Value);

        var byEchoedAsOf = await _client.GetFromJsonAsync<ObservationsResponse>(
            $"/series/{seriesCode}/observations?from={from}&to={to}&as_of={Uri.EscapeDataString(initial.AsOf)}");
        var byEchoedTransactionTime = await _client.GetFromJsonAsync<ObservationsResponse>(
            $"/series/{seriesCode}/observations?from={from}&to={to}&as_of={Uri.EscapeDataString(observation.TransactionTime)}");

        Assert.Equal(4321m, Assert.Single(byEchoedAsOf!.Observations).Value);
        Assert.Equal(4321m, Assert.Single(byEchoedTransactionTime!.Observations).Value);
    }

    [Fact]
    public async Task GetObservations_NaiveTimestamp_ReturnsBadRequest()
    {
        // No UTC offset on either bound -- must be rejected rather than
        // guessing a zone (CLAUDE.md: conversion happens once, at an
        // explicit boundary, never by assumption).
        var response = await _client.GetAsync(
            "/series/whatever/observations?from=2026-08-01T00:00:00&to=2026-08-02T00:00:00");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetObservations_RetractedRow_IsReturnedExplicitlyNotOmitted()
    {
        var seriesCode = $"test.retracted.{Guid.NewGuid():N}";
        await using var dataSource = GridVaultDataSource.Create(_fixture.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();

        var (sourceId, seriesId) = await SeedSeriesAsync(connection, seriesCode);

        var now = SystemClock.Instance.GetCurrentInstant();
        var validStart = now - Duration.FromDays(1);
        var validEnd = validStart + Duration.FromHours(1);
        var t1 = validStart + Duration.FromMinutes(5);

        var runId = await InsertIngestionRunAsync(connection, sourceId, seriesId, t1);
        await InsertObservationAsync(connection, seriesId, validStart, validEnd, t1, value: null, status: "retracted", runId);

        var response = await _client.GetFromJsonAsync<ObservationsResponse>(
            $"/series/{seriesCode}/observations?from={FormatInstant(validStart)}&to={FormatInstant(validEnd)}" +
            $"&as_of={FormatInstant(t1 + Duration.FromMinutes(1))}");

        // A retracted row must appear as itself, not be silently omitted --
        // absence of a row means "no vintage existed", never "unknown".
        var observation = Assert.Single(response!.Observations);
        Assert.Null(observation.Value);
        Assert.Equal("retracted", observation.Status);
    }

    private static async Task<(long SourceId, long SeriesId)> SeedSeriesAsync(NpgsqlConnection connection, string seriesCode)
    {
        var sourceId = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO source (name) VALUES (@Name) RETURNING id",
            new { Name = $"src-{Guid.NewGuid():N}" });

        var seriesId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO series (source_id, code, name, unit, cadence, source_timezone, hour_convention)
            VALUES (@SourceId, @Code, 'Test Series', 'MW', interval '1 hour', 'Etc/GMT+5', 'ending')
            RETURNING id
            """,
            new { SourceId = sourceId, Code = seriesCode });

        return (sourceId, seriesId);
    }

    private static async Task<long> InsertIngestionRunAsync(NpgsqlConnection connection, long sourceId, long seriesId, Instant at)
    {
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO ingestion_run (source_id, series_id, window_start, window_end, status, started_at, finished_at)
            VALUES (@SourceId, @SeriesId, @At, @At, 'succeeded', @At, @At)
            RETURNING id
            """,
            new { SourceId = sourceId, SeriesId = seriesId, At = at });
    }

    private static async Task InsertObservationAsync(
        NpgsqlConnection connection,
        long seriesId,
        Instant validStart,
        Instant validEnd,
        Instant transactionTime,
        decimal? value,
        string status,
        long runId)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO observation
                (series_id, valid_time_start, valid_time_end, transaction_time, value, status, ingestion_run_id)
            VALUES
                (@SeriesId, @ValidStart, @ValidEnd, @TransactionTime, @Value, @Status, @RunId)
            """,
            new
            {
                SeriesId = seriesId,
                ValidStart = validStart,
                ValidEnd = validEnd,
                TransactionTime = transactionTime,
                Value = value,
                Status = status,
                RunId = runId,
            });
    }

    // ExtendedIso, not General: General floors to whole seconds, and
    // SystemClock-derived instants (as used throughout these tests) reliably
    // carry a sub-second remainder -- General would silently shift as_of
    // earlier than the transaction_time being compared against it.
    private static string FormatInstant(Instant instant) => Uri.EscapeDataString(InstantPattern.ExtendedIso.Format(instant));
}
