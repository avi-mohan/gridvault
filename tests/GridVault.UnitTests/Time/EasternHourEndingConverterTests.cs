using GridVault.Domain.Time;
using NodaTime;

namespace GridVault.UnitTests.Time;

public class EasternHourEndingConverterTests
{
    private static readonly DateTimeZone EasternZone =
        DateTimeZoneProviders.Tzdb["America/Toronto"];

    [Fact]
    public void ToUtcInterval_OnOrdinaryDay_Has24HourEndingSlots()
    {
        var date = new LocalDate(2026, 6, 15); // no DST transition nearby

        Assert.Throws<ArgumentOutOfRangeException>(() => EasternHourEndingConverter.ToUtcInterval(date, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EasternHourEndingConverter.ToUtcInterval(date, 25));

        var he1 = EasternHourEndingConverter.ToUtcInterval(date, 1);
        var he24 = EasternHourEndingConverter.ToUtcInterval(date, 24);
        Assert.Equal(Duration.FromHours(23), he24.Start - he1.Start);
        Assert.Equal(Duration.FromHours(1), he1.Duration);
    }

    [Theory]
    [InlineData(2026, 3)]
    [InlineData(2027, 3)]
    public void ToUtcInterval_OnSpringForwardDay_Has23HourEndingSlots(int year, int month)
    {
        var date = FindTransitionDate(year, month);

        var last = EasternHourEndingConverter.ToUtcInterval(date, 23);
        var firstOfNextDay = EasternHourEndingConverter.ToUtcInterval(date.PlusDays(1), 1);

        Assert.Equal(firstOfNextDay.Start, last.End);
        Assert.Throws<ArgumentOutOfRangeException>(() => EasternHourEndingConverter.ToUtcInterval(date, 24));
    }

    [Theory]
    [InlineData(2026, 11)]
    [InlineData(2027, 11)]
    public void ToUtcInterval_OnFallBackDay_Has25HourEndingSlots(int year, int month)
    {
        var date = FindTransitionDate(year, month);

        var last = EasternHourEndingConverter.ToUtcInterval(date, 25);
        var firstOfNextDay = EasternHourEndingConverter.ToUtcInterval(date.PlusDays(1), 1);

        Assert.Equal(firstOfNextDay.Start, last.End);
        Assert.Throws<ArgumentOutOfRangeException>(() => EasternHourEndingConverter.ToUtcInterval(date, 26));
    }

    [Theory]
    [InlineData(2026, 3, 23)]
    [InlineData(2026, 11, 25)]
    public void ToUtcInterval_SlotsAreContiguousAndOneHourEach_OnTransitionDays(int year, int month, int slotCount)
    {
        var date = FindTransitionDate(year, month);

        var previous = EasternHourEndingConverter.ToUtcInterval(date, 1);
        Assert.Equal(Duration.FromHours(1), previous.Duration);

        for (var hourEnding = 2; hourEnding <= slotCount; hourEnding++)
        {
            var current = EasternHourEndingConverter.ToUtcInterval(date, hourEnding);
            Assert.Equal(Duration.FromHours(1), current.Duration);
            Assert.Equal(previous.End, current.Start);
            previous = current;
        }
    }

    [Fact]
    public void ToUtcInterval_FixedOffsetZone_OnEasternFallBackDate_Has24DistinctConsecutiveHours()
    {
        var fixedOffsetZone = DateTimeZoneProviders.Tzdb["Etc/GMT+5"];
        var date = new LocalDate(2026, 11, 1);

        // Confirm this really is the date Eastern falls back on — the whole
        // point of this test is that the fixed-offset zone does NOT follow
        // Eastern's DST calendar, so the date needs to actually be one
        // where they'd disagree.
        Assert.Equal(FindTransitionDate(2026, 11), date);
        Assert.Equal(25, CountHourEndingSlots(EasternZone, date));

        var intervals = Enumerable.Range(1, 24)
            .Select(hourEnding => EasternHourEndingConverter.ToUtcInterval(fixedOffsetZone, date, hourEnding))
            .ToList();

        Assert.Equal(24, intervals.Select(interval => interval.Start).Distinct().Count());

        for (var i = 0; i < intervals.Count; i++)
        {
            Assert.Equal(Duration.FromHours(1), intervals[i].Duration);
            if (i > 0)
            {
                Assert.Equal(intervals[i - 1].End, intervals[i].Start);
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => EasternHourEndingConverter.ToUtcInterval(fixedOffsetZone, date, 25));
    }

    private static int CountHourEndingSlots(DateTimeZone zone, LocalDate date)
    {
        var startOfDay = zone.AtStartOfDay(date).ToInstant();
        var startOfNextDay = zone.AtStartOfDay(date.PlusDays(1)).ToInstant();
        return (int)(startOfNextDay - startOfDay).TotalHours;
    }

    /// <summary>
    /// Finds the DST transition date in the given month by scanning for the
    /// day whose local-midnight-to-local-midnight span isn't 24 hours,
    /// rather than hardcoding a date that could be wrong for a given year.
    /// </summary>
    private static LocalDate FindTransitionDate(int year, int month)
    {
        var daysInMonth = CalendarSystem.Iso.GetDaysInMonth(year, month);

        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new LocalDate(year, month, day);
            var startOfDay = EasternZone.AtStartOfDay(date).ToInstant();
            var startOfNextDay = EasternZone.AtStartOfDay(date.PlusDays(1)).ToInstant();

            if (startOfNextDay - startOfDay != Duration.FromHours(24))
            {
                return date;
            }
        }

        throw new InvalidOperationException($"No DST transition found in {year}-{month:D2} for {EasternZone.Id}.");
    }
}
