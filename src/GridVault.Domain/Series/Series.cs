using NodaTime;

namespace GridVault.Domain.Series;

public enum HourConvention
{
    Beginning,
    Ending,
}

/// <summary>
/// A configured data series: which source it comes from, its unit and
/// cadence, and the timezone/hour-convention its upstream values are
/// published in. SourceTimezone is data, not a hardcoded assumption in the
/// converter, since different IESO reports may use different conventions.
/// </summary>
public sealed record Series(
    long Id,
    long SourceId,
    string Code,
    string Name,
    string Unit,
    Period Cadence,
    string SourceTimezone,
    HourConvention HourConvention);
