using Dapper;
using GridVault.Domain.Ingestion;
using NodaTime;
using Npgsql;

namespace GridVault.Data.IngestionRuns;

public sealed class IngestionRunRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static IngestionRunRepository()
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public IngestionRunRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Records the start of a run. StartedAt is operational bookkeeping,
    /// not a fact's transaction_time, so wall-clock-at-call is fine here.
    /// </summary>
    public async Task<long> InsertRunningAsync(
        long sourceId,
        long? seriesId,
        Instant windowStart,
        Instant windowEnd,
        Instant startedAt,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO ingestion_run (source_id, series_id, window_start, window_end, status, started_at)
            VALUES (@SourceId, @SeriesId, @WindowStart, @WindowEnd, 'running', @StartedAt)
            RETURNING id
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new { SourceId = sourceId, SeriesId = seriesId, WindowStart = windowStart, WindowEnd = windowEnd, StartedAt = startedAt },
            cancellationToken: cancellationToken));
    }

    public async Task CompleteAsync(
        long id,
        IngestionRunStatus status,
        Instant finishedAt,
        int rowsFetched,
        int rowsWritten,
        string? rawStorageKey,
        string? errorDetail,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE ingestion_run
            SET status = @Status,
                finished_at = @FinishedAt,
                rows_fetched = @RowsFetched,
                rows_written = @RowsWritten,
                raw_storage_key = @RawStorageKey,
                error_detail = @ErrorDetail
            WHERE id = @Id
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                Status = FormatStatus(status),
                FinishedAt = finishedAt,
                RowsFetched = rowsFetched,
                RowsWritten = rowsWritten,
                RawStorageKey = rawStorageKey,
                ErrorDetail = errorDetail,
            },
            cancellationToken: cancellationToken));
    }

    private static string FormatStatus(IngestionRunStatus status) => status switch
    {
        IngestionRunStatus.Running => "running",
        IngestionRunStatus.Succeeded => "succeeded",
        IngestionRunStatus.Failed => "failed",
        IngestionRunStatus.Partial => "partial",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown ingestion run status."),
    };
}
