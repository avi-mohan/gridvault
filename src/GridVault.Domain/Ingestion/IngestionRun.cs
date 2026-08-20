using NodaTime;

namespace GridVault.Domain.Ingestion;

public enum IngestionRunStatus
{
    Running,
    Succeeded,
    Failed,
    Partial,
}

/// <summary>
/// Bookkeeping for a single fetch/land/parse/load attempt: what it covered,
/// how it went, and — via RawStorageKey — where the immutable payload it
/// was parsed from lives. A replay run reuses the original RawStorageKey
/// rather than landing a new payload.
/// </summary>
public sealed record IngestionRun(
    long Id,
    long SourceId,
    long? SeriesId,
    Instant WindowStart,
    Instant WindowEnd,
    IngestionRunStatus Status,
    Instant StartedAt,
    Instant? FinishedAt,
    int? RowsFetched,
    int? RowsWritten,
    string? RawStorageKey,
    string? ErrorDetail);
