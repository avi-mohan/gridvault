using NodaTime;

namespace GridVault.Domain.Observations;

public enum ObservationStatus
{
    Observed,
    Retracted,
    NotPublished,
}

/// <summary>
/// A single vintage of a fact: the value known for [ValidTimeStart,
/// ValidTimeEnd) as recorded at TransactionTime. Immutable — a change in
/// the underlying fact is a new Observation row, never a mutation of this
/// one.
/// </summary>
public sealed record Observation(
    long SeriesId,
    Instant ValidTimeStart,
    Instant ValidTimeEnd,
    Instant TransactionTime,
    decimal? Value,
    ObservationStatus Status,
    long IngestionRunId);
