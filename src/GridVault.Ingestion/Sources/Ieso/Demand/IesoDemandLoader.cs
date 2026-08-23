using GridVault.Data.Observations;
using GridVault.Data.Series;
using GridVault.Domain.Observations;
using GridVault.Domain.Time;
using NodaTime;
using NodaTime.Text;

namespace GridVault.Ingestion.Sources.Ieso.Demand;

public sealed record DemandLoadResult(int ValuesProcessed, int VintagesWritten);

/// <summary>
/// Turns a parsed demand report into observation vintages. Each row
/// produces up to two upserts (market demand, Ontario demand are separate
/// series), each independently observed/not_published depending on whether
/// that column's cell was populated.
/// </summary>
public sealed class IesoDemandLoader
{
    public const string MarketSeriesCode = "ieso.demand.market";
    public const string OntarioSeriesCode = "ieso.demand.ontario";

    // The Created-at header's zone is a fact separate from the data rows'
    // zone (series.source_timezone) -- see docs/decisions.md. Both happen
    // to be UTC-5 today, but this is a deliberate, independent constant,
    // not a reuse of the series' configured timezone.
    private static readonly DateTimeZone HeaderZone = DateTimeZoneProviders.Tzdb["Etc/GMT+5"];
    private static readonly LocalDateTimePattern CreatedAtPattern =
        LocalDateTimePattern.CreateWithInvariantCulture("yyyy-MM-dd HH:mm:ss");

    private readonly SeriesRepository _seriesRepository;
    private readonly ObservationRepository _observationRepository;

    public IesoDemandLoader(SeriesRepository seriesRepository, ObservationRepository observationRepository)
    {
        _seriesRepository = seriesRepository;
        _observationRepository = observationRepository;
    }

    /// <summary>
    /// fetchedAtFallback is used only if the header's Created-at value
    /// can't be parsed as a timestamp -- the parser already guarantees the
    /// header line itself is present, but not that its value is in the
    /// expected format.
    /// </summary>
    public async Task<DemandLoadResult> LoadAsync(
        ParsedDemandReport report,
        Instant fetchedAtFallback,
        long ingestionRunId,
        CancellationToken cancellationToken = default)
    {
        var marketSeries = await _seriesRepository.GetByCodeAsync(MarketSeriesCode, cancellationToken);
        var ontarioSeries = await _seriesRepository.GetByCodeAsync(OntarioSeriesCode, cancellationToken);

        var transactionTime = TryParseCreatedAt(report.CreatedAtRaw, out var parsedCreatedAt)
            ? parsedCreatedAt
            : fetchedAtFallback;

        var written = 0;
        foreach (var row in report.Rows)
        {
            if (await UpsertColumnAsync(marketSeries, row.Date, row.HourEnding, row.MarketDemand, transactionTime, ingestionRunId, cancellationToken))
            {
                written++;
            }

            if (await UpsertColumnAsync(ontarioSeries, row.Date, row.HourEnding, row.OntarioDemand, transactionTime, ingestionRunId, cancellationToken))
            {
                written++;
            }
        }

        return new DemandLoadResult(report.Rows.Count * 2, written);
    }

    private async Task<bool> UpsertColumnAsync(
        GridVault.Domain.Series.Series series,
        LocalDate date,
        int hourEnding,
        decimal? value,
        Instant transactionTime,
        long ingestionRunId,
        CancellationToken cancellationToken)
    {
        var zone = DateTimeZoneProviders.Tzdb[series.SourceTimezone];
        var interval = EasternHourEndingConverter.ToUtcInterval(zone, date, hourEnding);
        var status = value.HasValue ? ObservationStatus.Observed : ObservationStatus.NotPublished;

        var observation = new Observation(
            series.Id,
            interval.Start,
            interval.End,
            transactionTime,
            value,
            status,
            ingestionRunId);

        return await _observationRepository.UpsertVintageAsync(observation, cancellationToken);
    }

    private static bool TryParseCreatedAt(string raw, out Instant instant)
    {
        var result = CreatedAtPattern.Parse(raw);
        if (!result.Success)
        {
            instant = default;
            return false;
        }

        instant = HeaderZone.AtStrictly(result.Value).ToInstant();
        return true;
    }
}
