using GridVault.Data;
using GridVault.Data.IngestionRuns;
using GridVault.Data.Observations;
using GridVault.Data.Series;
using GridVault.Domain.Ingestion;
using GridVault.Ingestion.Sources.Ieso.Demand;
using NodaTime;
using Npgsql;

namespace GridVault.IntegrationTests.Sources.Ieso.Demand;

[Collection(nameof(PostgresCollection))]
public class IesoDemandLoaderTests : IAsyncLifetime
{
    // Real excerpt from PUB_Demand_2026_v229.csv (fixtures/ieso/demand/),
    // fetched from reports-public.ieso.ca, plus one constructed blank-value
    // row (real files fetched so far never contained one) to exercise the
    // not_published path.
    private const string RealExcerptWithConstructedBlankRow = """
        \\Hourly Demand Report,,,
        \\Created at 2026-08-18 07:30:09,,,
        \\For 2026,,,
        Date,Hour,Market Demand,Ontario Demand
        2026-08-17,1,18711,16190
        2026-08-17,2,18494,15978
        2026-08-18,24,,
        """;

    // Distinct dates from the excerpt above so this test's write-count
    // assertions can't collide with the other tests in this class -- the
    // PostgresFixture container is shared and not truncated between tests
    // (see PostgresFixture's own doc comment), so tests that both write and
    // assert exact counts need non-overlapping valid_time ranges.
    private const string IdempotencyExcerpt = """
        \\Hourly Demand Report,,,
        \\Created at 2026-08-19 07:30:09,,,
        \\For 2026,,,
        Date,Hour,Market Demand,Ontario Demand
        2026-08-19,10,19918,18608
        2026-08-19,11,20317,19099
        """;

    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;

    public IesoDemandLoaderTests(PostgresFixture fixture)
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
    public async Task LoadAsync_RealExcerpt_WritesBothSeriesWithHeaderDerivedTransactionTime()
    {
        var runId = await CreateIngestionRunAsync();
        var report = IesoDemandReportParser.Parse(RealExcerptWithConstructedBlankRow);
        var loader = new IesoDemandLoader(new SeriesRepository(_dataSource), new ObservationRepository(_dataSource));

        // fetchedAtFallback is deliberately wrong-looking (year 2000) so
        // that if the loader ever fell back to it instead of the header,
        // this test would fail loudly rather than silently pass.
        var fetchedAtFallback = Instant.FromUtc(2000, 1, 1, 0, 0);

        var result = await loader.LoadAsync(report, fetchedAtFallback, runId);

        // Exact write counts aren't asserted here (see
        // LoadAsync_RunTwiceOnTheSameContent_WritesNoAdditionalVintages for
        // that): this excerpt's dates overlap with
        // LoadAsync_BlankValueCell_WritesNotPublishedStatusForBothSeries in
        // the same shared, non-truncated container, so which of the two
        // runs first isn't guaranteed. Checking current state via
        // GetAsOfAsync below is safe regardless of run order.
        Assert.Equal(6, result.ValuesProcessed); // 3 rows * 2 series

        var expectedTransactionTime = Instant.FromUtc(2026, 8, 18, 12, 30, 9); // 07:30:09 UTC-5
        var observationRepository = new ObservationRepository(_dataSource);
        var seriesRepository = new SeriesRepository(_dataSource);

        var marketSeries = await seriesRepository.GetByCodeAsync(IesoDemandLoader.MarketSeriesCode);
        var he1 = EasternHourEndingConverterInterval(2026, 8, 17, 1);

        var asOf = await observationRepository.GetAsOfAsync(
            marketSeries.Id, he1.start, he1.start + Duration.FromHours(1), expectedTransactionTime + Duration.FromSeconds(1));

        var observation = Assert.Single(asOf);
        Assert.Equal(18711m, observation.Value);
        Assert.Equal(expectedTransactionTime, observation.TransactionTime);
    }

    [Fact]
    public async Task LoadAsync_BlankValueCell_WritesNotPublishedStatusForBothSeries()
    {
        var runId = await CreateIngestionRunAsync();
        var report = IesoDemandReportParser.Parse(RealExcerptWithConstructedBlankRow);
        var loader = new IesoDemandLoader(new SeriesRepository(_dataSource), new ObservationRepository(_dataSource));

        await loader.LoadAsync(report, Instant.FromUtc(2000, 1, 1, 0, 0), runId);

        var seriesRepository = new SeriesRepository(_dataSource);
        var observationRepository = new ObservationRepository(_dataSource);
        var marketSeries = await seriesRepository.GetByCodeAsync(IesoDemandLoader.MarketSeriesCode);
        var he24 = EasternHourEndingConverterInterval(2026, 8, 18, 24);

        var asOf = await observationRepository.GetAsOfAsync(
            marketSeries.Id, he24.start, he24.start + Duration.FromHours(1), SystemClock.Instance.GetCurrentInstant());

        var observation = Assert.Single(asOf);
        Assert.Null(observation.Value);
        Assert.Equal(GridVault.Domain.Observations.ObservationStatus.NotPublished, observation.Status);
    }

    [Fact]
    public async Task LoadAsync_RunTwiceOnTheSameContent_WritesNoAdditionalVintages()
    {
        var runId = await CreateIngestionRunAsync();
        var report = IesoDemandReportParser.Parse(IdempotencyExcerpt);
        var loader = new IesoDemandLoader(new SeriesRepository(_dataSource), new ObservationRepository(_dataSource));
        var fetchedAtFallback = Instant.FromUtc(2000, 1, 1, 0, 0);

        var first = await loader.LoadAsync(report, fetchedAtFallback, runId);
        var second = await loader.LoadAsync(report, fetchedAtFallback, runId);

        Assert.Equal(4, first.VintagesWritten); // 2 rows * 2 series
        Assert.Equal(0, second.VintagesWritten);
    }

    private async Task<long> CreateIngestionRunAsync()
    {
        var runRepository = new IngestionRunRepository(_dataSource);
        var now = SystemClock.Instance.GetCurrentInstant();

        await using var connection = await _dataSource.OpenConnectionAsync();
        var sourceId = await Dapper.SqlMapper.ExecuteScalarAsync<long>(
            connection,
            "INSERT INTO source (name) VALUES (@Name) RETURNING id",
            new { Name = $"ieso-loader-test-{Guid.NewGuid()}" });

        return await runRepository.InsertRunningAsync(sourceId, null, now, now, now);
    }

    private static (Instant start, Instant end) EasternHourEndingConverterInterval(int year, int month, int day, int hourEnding)
    {
        var zone = NodaTime.DateTimeZoneProviders.Tzdb["Etc/GMT+5"];
        var interval = GridVault.Domain.Time.EasternHourEndingConverter.ToUtcInterval(
            zone, new LocalDate(year, month, day), hourEnding);
        return (interval.Start, interval.End);
    }
}
