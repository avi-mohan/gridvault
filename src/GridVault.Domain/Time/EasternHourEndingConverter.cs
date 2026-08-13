using NodaTime;

namespace GridVault.Domain.Time;

/// <summary>
/// The single place where IESO's Eastern Prevailing Time, hour-ending local
/// dates convert to UTC instants. Everything downstream of this class works
/// in UTC only.
/// </summary>
public static class EasternHourEndingConverter
{
    private static readonly DateTimeZone EasternZone =
        DateTimeZoneProviders.Tzdb["America/Toronto"];

    /// <summary>
    /// Converts an IESO hour-ending label for a given local calendar date
    /// into the UTC instant interval it represents.
    ///
    /// hourEnding counts elapsed hours since local midnight (HE1 is the
    /// first hour of the day), not a wall-clock hour label. That is what
    /// makes it well-defined on the 23-hour and 25-hour DST transition
    /// days: local midnight is always a single, unambiguous instant, so
    /// counting whole hours forward from it needs no special-casing. On a
    /// 25-hour day the *wall-clock* hour 1:00-2:00 occurs twice, but as a
    /// sequential index HE2 and the following HE3 are still two distinct,
    /// correctly-ordered one-hour slices.
    /// </summary>
    public static Interval ToUtcInterval(LocalDate localDate, int hourEnding)
    {
        var startOfDay = EasternZone.AtStartOfDay(localDate).ToInstant();
        var startOfNextDay = EasternZone.AtStartOfDay(localDate.PlusDays(1)).ToInstant();
        var hoursInDay = (int)(startOfNextDay - startOfDay).TotalHours;

        if (hourEnding < 1 || hourEnding > hoursInDay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hourEnding),
                hourEnding,
                $"{localDate} has {hoursInDay} hour-ending slots in {EasternZone.Id}; valid range is 1..{hoursInDay}.");
        }

        var start = startOfDay + Duration.FromHours(hourEnding - 1);
        var end = start + Duration.FromHours(1);
        return new Interval(start, end);
    }
}
