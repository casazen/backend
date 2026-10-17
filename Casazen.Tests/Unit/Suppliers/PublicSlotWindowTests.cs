using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-03b: the window of days a read of slots covers, shared by the public page (SP-09) and the supplier's preview "how customers
/// see you": from the day asked for and never before today, 1 to 62 days (14 when left out), cut at the horizon.
/// </summary>
public class PublicSlotWindowTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);
    private static readonly DateOnly Horizon = Today.AddDays(35);

    [Fact]
    public void RangeOf_NothingAsked_IsTodayAndTheNext13Days()
    {
        var range = PublicSlotWindow.RangeOf(Today, Horizon, from: null, days: null);

        Assert.Equal(new PublicSlotRange(Today, Today.AddDays(13)), range);
    }

    [Theory]
    [InlineData(-5, "2026-10-08")] // a day before today is today
    [InlineData(0, "2026-10-08")]
    [InlineData(3, "2026-10-11")]
    public void RangeOf_TheFirstDay_IsTheDayAskedForNeverBeforeToday(int offset, string first)
    {
        var range = PublicSlotWindow.RangeOf(Today, Horizon, Today.AddDays(offset), days: 7);

        Assert.Equal(DateOnly.Parse(first, System.Globalization.CultureInfo.InvariantCulture), range!.Value.First);
        Assert.Equal(range.Value.First.AddDays(6), range.Value.Last);
    }

    [Theory]
    [InlineData(0, 1)] // below 1 is 1
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(62, 62)]
    [InlineData(500, 62)] // above the most is the most
    public void RangeOf_TheNumberOfDays_IsClampedToTheLimits(int days, int expected)
    {
        var range = PublicSlotWindow.RangeOf(Today, Today.AddDays(365), from: null, days);

        Assert.Equal(expected, range!.Value.Last.DayNumber - range.Value.First.DayNumber + 1);
    }

    [Fact]
    public void RangeOf_TheLastDay_IsCutAtTheHorizon()
    {
        var range = PublicSlotWindow.RangeOf(Today, Horizon, Today.AddDays(30), days: 30);

        Assert.Equal(new PublicSlotRange(Today.AddDays(30), Horizon), range);
    }

    [Fact]
    public void RangeOf_AFirstDayBeyondTheHorizon_IsAnEmptyWindow_AndAMadeUpDateDoesNotOverflow()
    {
        Assert.Null(PublicSlotWindow.RangeOf(Today, Horizon, Horizon.AddDays(1), days: 14));
        Assert.Null(PublicSlotWindow.RangeOf(Today, Horizon, DateOnly.MaxValue, days: 62));
    }

    [Fact]
    public void RangeOf_TheHorizonDayItselfCanStillBeBooked()
    {
        var range = PublicSlotWindow.RangeOf(Today, Horizon, Horizon, days: 5);

        Assert.Equal(new PublicSlotRange(Horizon, Horizon), range);
    }
}
