using GridVault.Data;
using GridVault.Data.Series;
using GridVault.Domain.Time;
using NodaTime;
using Npgsql;

namespace GridVault.IntegrationTests.Series;

/// <summary>
/// A tripwire, not a correctness test of the converter (that's
/// EasternHourEndingConverterTests). This asserts the SEEDED zone id is
/// actually fixed-offset, so it fails loudly the moment someone "fixes"
/// Script0008 to use America/Toronto — which looks like the more familiar
/// choice but is wrong for this report (see docs/decisions.md).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SeededDemandSeriesTimezoneTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;

    public SeededDemandSeriesTimezoneTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dataSource = GridVaultDataSource.Create(_fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Theory]
    [InlineData("ieso.demand.market")]
    [InlineData("ieso.demand.ontario")]
    public async Task SeededSeries_ResolvesToAConstantOffsetAcrossEasternFallBackDate(string seriesCode)
    {
        var repository = new SeriesRepository(_dataSource);
        var series = await repository.GetByCodeAsync(seriesCode);
        var zone = DateTimeZoneProviders.Tzdb[series.SourceTimezone];

        // 2026-11-01 is the date America/Toronto falls back on (EDT ->
        // EST). A fixed-offset zone shows the SAME offset on both sides of
        // that boundary; America/Toronto would show -4 then -5. If the
        // seed is ever changed to America/Toronto, the two offsets below
        // stop being equal and this test fails.
        var date = new LocalDate(2026, 11, 1);
        var earlyInTheDay = zone.AtStartOfDay(date).ToInstant();
        var lateInTheDay = zone.AtStartOfDay(date.PlusDays(1)).ToInstant() - Duration.FromHours(1);

        var offsetEarly = zone.GetUtcOffset(earlyInTheDay);
        var offsetLate = zone.GetUtcOffset(lateInTheDay);

        Assert.Equal(Offset.FromHours(-5), offsetEarly);
        Assert.Equal(Offset.FromHours(-5), offsetLate);
        Assert.Equal(offsetEarly, offsetLate);

        // Same check from the domain side: a fixed-offset zone has exactly
        // 24 hour-ending slots on every date, including this one --
        // America/Toronto has 25 here (it's the fall-back day).
        var slotCount = Enumerable.Range(1, 25).Count(hourEnding => CanConvert(zone, date, hourEnding));
        Assert.Equal(24, slotCount);
    }

    private static bool CanConvert(DateTimeZone zone, LocalDate date, int hourEnding)
    {
        try
        {
            EasternHourEndingConverter.ToUtcInterval(zone, date, hourEnding);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
