using NodaTime;

namespace GridVault.Domain.Time;

/// <summary>
/// Converts an hour-ending local date/hour label to a UTC instant interval,
/// for any zone — not just Eastern. Different IESO reports use different
/// zones (the demand report is fixed EST; the post-MRP Day-Ahead Market
/// uses true Eastern Prevailing Time), so the zone is a parameter, not a
/// hardcoded assumption. Everything downstream of this class works in UTC
/// only. The America/Toronto convenience overload exists because that was
/// the first zone this was built for and callers/tests already depend on
/// it; there is nothing Eastern-specific left in the conversion logic
/// itself.
/// </summary>
public static class EasternHourEndingConverter
{
    private static readonly DateTimeZone EasternZone =
        DateTimeZoneProviders.Tzdb["America/Toronto"];

    /// <summary>
    /// Converts an hour-ending label for a given local calendar date, in
    /// America/Toronto, into the UTC instant interval it represents.
    /// </summary>
    public static Interval ToUtcInterval(LocalDate localDate, int hourEnding) =>
        ToUtcInterval(EasternZone, localDate, hourEnding);

    /// <summary>
    /// Converts an hour-ending label for a given local calendar date, in
    /// the given zone, into the UTC instant interval it represents.
    ///
    /// hourEnding counts elapsed hours since local midnight (HE1 is the
    /// first hour of the day), not a wall-clock hour label. That is what
    /// makes it well-defined on a DST transition day in a zone that has
    /// one: local midnight is always a single, unambiguous instant, so
    /// counting whole hours forward from it needs no special-casing. On a
    /// 25-hour day the *wall-clock* hour 1:00-2:00 occurs twice, but as a
    /// sequential index HE2 and the following HE3 are still two distinct,
    /// correctly-ordered one-hour slices. A fixed-offset zone (no DST) is
    /// just the degenerate case where every day has exactly 24 slots.
    /// </summary>
    public static Interval ToUtcInterval(DateTimeZone zone, LocalDate localDate, int hourEnding)
    {
        var startOfDay = zone.AtStartOfDay(localDate).ToInstant();
        var startOfNextDay = zone.AtStartOfDay(localDate.PlusDays(1)).ToInstant();
        var hoursInDay = (int)(startOfNextDay - startOfDay).TotalHours;

        if (hourEnding < 1 || hourEnding > hoursInDay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hourEnding),
                hourEnding,
                $"{localDate} has {hoursInDay} hour-ending slots in {zone.Id}; valid range is 1..{hoursInDay}.");
        }

        var start = startOfDay + Duration.FromHours(hourEnding - 1);
        var end = start + Duration.FromHours(1);
        return new Interval(start, end);
    }
}
