using System.Text.Json.Serialization;
using GridVault.Data.Observations;
using GridVault.Data.Series;
using GridVault.Domain.Observations;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace GridVault.Api.Series;

/// <summary>
/// GET /series/{seriesCode}/observations -- answers "what did we know about
/// hour X as of time Y". as_of is inclusive against transaction_time, which
/// is the source's own publish timestamp (see CLAUDE.md's determinism
/// rule), so this answers "what had IESO published by as_of", not "what had
/// GridVault fetched by as_of" -- see docs/decisions.md.
/// </summary>
public static class ObservationsEndpoint
{
    private static readonly Duration MaxRange = Duration.FromDays(90);

    public static void MapObservationsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/series/{seriesCode}/observations", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        string seriesCode,
        string? from,
        string? to,
        [FromQuery(Name = "as_of")] string? asOf,
        SeriesRepository seriesRepository,
        ObservationRepository observationRepository,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return Problem("Query parameters 'from' and 'to' are both required.");
        }

        if (!TryParseOffsetTimestamp(from, out var fromInstant, out var fromError))
        {
            return Problem($"'from': {fromError}");
        }

        if (!TryParseOffsetTimestamp(to, out var toInstant, out var toError))
        {
            return Problem($"'to': {toError}");
        }

        Instant asOfInstant;
        if (string.IsNullOrWhiteSpace(asOf))
        {
            asOfInstant = SystemClock.Instance.GetCurrentInstant();
        }
        else if (!TryParseOffsetTimestamp(asOf, out asOfInstant, out var asOfError))
        {
            return Problem($"'as_of': {asOfError}");
        }

        if (fromInstant >= toInstant)
        {
            return Problem("'from' must be strictly before 'to'.");
        }

        if (toInstant - fromInstant > MaxRange)
        {
            return Problem($"The range between 'from' and 'to' cannot exceed {MaxRange.Days} days.");
        }

        var series = await seriesRepository.TryGetByCodeAsync(seriesCode, cancellationToken);
        if (series is null)
        {
            return Results.NotFound(new { error = $"No series with code '{seriesCode}'." });
        }

        var observations = await observationRepository.GetAsOfAsync(
            series.Id, fromInstant, toInstant, asOfInstant, cancellationToken);

        var response = new ObservationsResponse(
            series.Code,
            FormatInstant(asOfInstant),
            FormatInstant(fromInstant),
            FormatInstant(toInstant),
            observations
                .Select(o => new ObservationResponse(
                    FormatInstant(o.ValidTimeStart),
                    o.Value,
                    FormatStatus(o.Status),
                    FormatInstant(o.TransactionTime),
                    o.IngestionRunId))
                .ToList());

        return Results.Ok(response);
    }

    private static IResult Problem(string detail) => Results.BadRequest(new { error = detail });

    private static bool TryParseOffsetTimestamp(string text, out Instant instant, out string? error)
    {
        var result = OffsetDateTimePattern.ExtendedIso.Parse(text);
        if (result.Success)
        {
            instant = result.Value.ToInstant();
            error = null;
            return true;
        }

        instant = default;
        error = "must be an ISO-8601 timestamp with an explicit UTC offset " +
            $"(e.g. '2026-08-01T00:00:00Z'). Got '{text}'.";
        return false;
    }

    private static string FormatInstant(Instant instant) => InstantPattern.General.Format(instant);

    private static string FormatStatus(ObservationStatus status) => status switch
    {
        ObservationStatus.Observed => "observed",
        ObservationStatus.Retracted => "retracted",
        ObservationStatus.NotPublished => "not_published",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown observation status."),
    };
}

public sealed record ObservationsResponse(
    [property: JsonPropertyName("series_code")] string SeriesCode,
    [property: JsonPropertyName("as_of")] string AsOf,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("observations")] IReadOnlyList<ObservationResponse> Observations);

public sealed record ObservationResponse(
    [property: JsonPropertyName("valid_time_start")] string ValidTimeStart,
    [property: JsonPropertyName("value")] decimal? Value,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("transaction_time")] string TransactionTime,
    [property: JsonPropertyName("ingestion_run_id")] long IngestionRunId);
