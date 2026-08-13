using Dapper;
using GridVault.Data;
using GridVault.Data.Observations;
using NodaTime;
using Npgsql;

namespace GridVault.IntegrationTests.Observations;

[Collection(nameof(PostgresCollection))]
public class ObservationRepositoryAsOfTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;

    public ObservationRepositoryAsOfTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dataSource = GridVaultDataSource.Create(_fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task GetAsOfAsync_ReturnsTheVintageThatWasCurrentAtEachAsOfInstant()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();

        var sourceId = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO source (name) VALUES ('IESO-asof-test') RETURNING id");

        var seriesId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO series (source_id, code, name, unit, cadence, source_timezone, hour_convention)
            VALUES (@SourceId, 'ONTARIO_DEMAND_ASOF_TEST', 'Ontario Demand', 'MW', interval '1 hour', 'America/Toronto', 'ending')
            RETURNING id
            """,
            new { SourceId = sourceId });

        // Relative to "now" rather than a hardcoded calendar date, so this
        // test keeps working no matter when it's actually run — it just
        // needs to land inside whatever partition range the migrations
        // pre-created at container start.
        var now = SystemClock.Instance.GetCurrentInstant();
        var validStart = now - Duration.FromDays(1);
        var validEnd = validStart + Duration.FromHours(1);

        var t1 = validStart + Duration.FromMinutes(5);   // preliminary
        var t2 = t1 + Duration.FromHours(3);              // revised
        var t3 = t2 + Duration.FromHours(20);             // final

        var runId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO ingestion_run (source_id, series_id, window_start, window_end, status, started_at, finished_at)
            VALUES (@SourceId, @SeriesId, @ValidStart, @ValidEnd, 'succeeded', @T3, @T3)
            RETURNING id
            """,
            new { SourceId = sourceId, SeriesId = seriesId, ValidStart = validStart, ValidEnd = validEnd, T3 = t3 });

        await InsertObservationAsync(connection, seriesId, validStart, validEnd, t1, 1000m, runId);
        await InsertObservationAsync(connection, seriesId, validStart, validEnd, t2, 1050m, runId);
        await InsertObservationAsync(connection, seriesId, validStart, validEnd, t3, 1042m, runId);

        var repository = new ObservationRepository(_dataSource);

        var beforeFirstVintage = await repository.GetAsOfAsync(seriesId, validStart, validEnd, t1 - Duration.FromMinutes(1));
        var afterFirstVintage = await repository.GetAsOfAsync(seriesId, validStart, validEnd, t1 + Duration.FromMinutes(1));
        var afterSecondVintage = await repository.GetAsOfAsync(seriesId, validStart, validEnd, t2 + Duration.FromMinutes(1));
        var afterThirdVintage = await repository.GetAsOfAsync(seriesId, validStart, validEnd, t3 + Duration.FromMinutes(1));

        Assert.Empty(beforeFirstVintage);
        Assert.Equal(1000m, Assert.Single(afterFirstVintage).Value);
        Assert.Equal(1050m, Assert.Single(afterSecondVintage).Value);
        Assert.Equal(1042m, Assert.Single(afterThirdVintage).Value);
    }

    private static async Task InsertObservationAsync(
        NpgsqlConnection connection,
        long seriesId,
        Instant validStart,
        Instant validEnd,
        Instant transactionTime,
        decimal value,
        long ingestionRunId)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO observation
                (series_id, valid_time_start, valid_time_end, transaction_time, value, status, ingestion_run_id)
            VALUES
                (@SeriesId, @ValidStart, @ValidEnd, @TransactionTime, @Value, 'observed', @IngestionRunId)
            """,
            new
            {
                SeriesId = seriesId,
                ValidStart = validStart,
                ValidEnd = validEnd,
                TransactionTime = transactionTime,
                Value = value,
                IngestionRunId = ingestionRunId,
            });
    }
}
