using Dapper;
using GridVault.Domain.Observations;
using NodaTime;
using Npgsql;

namespace GridVault.Data.Observations;

public sealed class ObservationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static ObservationRepository()
    {
        // Schema is snake_case; row DTOs are PascalCase. Set once here
        // rather than aliasing every column in every query.
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public ObservationRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// For each valid-time slot in [rangeStart, rangeEnd) for the series,
    /// returns the vintage that was current as of asOf — i.e. the row with
    /// the greatest transaction_time not after asOf. observation is
    /// append-only, so "current as of X" is always computed this way rather
    /// than stored.
    /// </summary>
    public async Task<IReadOnlyList<Observation>> GetAsOfAsync(
        long seriesId,
        Instant rangeStart,
        Instant rangeEnd,
        Instant asOf,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT ON (valid_time_start)
                series_id,
                valid_time_start,
                valid_time_end,
                transaction_time,
                value,
                status,
                ingestion_run_id
            FROM observation
            WHERE series_id = @SeriesId
              AND valid_time_start >= @RangeStart
              AND valid_time_start < @RangeEnd
              AND transaction_time <= @AsOf
            ORDER BY valid_time_start, transaction_time DESC
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<ObservationRow>(new CommandDefinition(
            sql,
            new { SeriesId = seriesId, RangeStart = rangeStart, RangeEnd = rangeEnd, AsOf = asOf },
            cancellationToken: cancellationToken));

        return rows.Select(MapToObservation).ToList();
    }

    /// <summary>
    /// Inserts a new vintage only if it actually differs from the latest
    /// known vintage as of this row's own transaction_time — not the
    /// global latest, so replay and out-of-order backfill runs compare
    /// against the same baseline a same-order run would have seen. A
    /// status transition (observed/retracted/not_published) always
    /// counts as a difference even when value is unchanged or null in
    /// both, per the rule in CLAUDE.md. The unique index on
    /// (series_id, valid_time_start, transaction_time) remains the
    /// backstop against a literal duplicate insert from a retried run.
    /// Returns whether a row was actually written.
    /// </summary>
    public async Task<bool> UpsertVintageAsync(
        Observation observation,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO observation (series_id, valid_time_start, valid_time_end,
                                      transaction_time, value, status, ingestion_run_id)
            SELECT @SeriesId, @ValidTimeStart, @ValidTimeEnd, @TransactionTime,
                   @Value, @Status, @IngestionRunId
            WHERE NOT EXISTS (
                SELECT 1 FROM (
                    SELECT value, status FROM observation
                    WHERE series_id = @SeriesId AND valid_time_start = @ValidTimeStart
                      AND transaction_time <= @TransactionTime
                    ORDER BY transaction_time DESC LIMIT 1
                ) latest
                WHERE latest.value IS NOT DISTINCT FROM @Value
                  AND latest.status = @Status
            )
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var rowsAffected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                observation.SeriesId,
                observation.ValidTimeStart,
                observation.ValidTimeEnd,
                observation.TransactionTime,
                observation.Value,
                Status = FormatStatus(observation.Status),
                observation.IngestionRunId,
            },
            cancellationToken: cancellationToken));

        return rowsAffected > 0;
    }

    private static string FormatStatus(ObservationStatus status) => status switch
    {
        ObservationStatus.Observed => "observed",
        ObservationStatus.Retracted => "retracted",
        ObservationStatus.NotPublished => "not_published",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown observation status."),
    };

    private static Observation MapToObservation(ObservationRow row) => new(
        row.SeriesId,
        row.ValidTimeStart,
        row.ValidTimeEnd,
        row.TransactionTime,
        row.Value,
        ParseStatus(row.Status),
        row.IngestionRunId);

    private static ObservationStatus ParseStatus(string status) => status switch
    {
        "observed" => ObservationStatus.Observed,
        "retracted" => ObservationStatus.Retracted,
        "not_published" => ObservationStatus.NotPublished,
        _ => throw new InvalidOperationException($"Unknown observation status '{status}'."),
    };

    private sealed class ObservationRow
    {
        public long SeriesId { get; set; }
        public Instant ValidTimeStart { get; set; }
        public Instant ValidTimeEnd { get; set; }
        public Instant TransactionTime { get; set; }
        public decimal? Value { get; set; }
        public string Status { get; set; } = string.Empty;
        public long IngestionRunId { get; set; }
    }
}
