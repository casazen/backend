using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

public class SupplierAgendaRangeTests
{
    private static readonly DateOnly Day = new(2026, 10, 8);

    [Fact]
    public void Check_ARangeOfOneDayUpToTheLimit_IsFine()
    {
        Assert.Equal(SupplierRangeProblem.None, SupplierAgendaRange.Check(Day, Day, 62));
        Assert.Equal(SupplierRangeProblem.None, SupplierAgendaRange.Check(Day, Day.AddDays(61), 62));
    }

    [Fact]
    public void Check_ALastDayBeforeTheFirst_IsReversed()
    {
        Assert.Equal(SupplierRangeProblem.Reversed, SupplierAgendaRange.Check(Day, Day.AddDays(-1), 62));
    }

    [Fact]
    public void Check_ARangeLongerThanTheLimit_IsTooLong_BothEndsCounted()
    {
        Assert.Equal(SupplierRangeProblem.TooLong, SupplierAgendaRange.Check(Day, Day.AddDays(62), 62));
        Assert.Equal(SupplierRangeProblem.TooLong, SupplierAgendaRange.Check(Day, Day.AddDays(1), 1));
    }

    [Theory]
    [InlineData("0001-01-01", "0001-01-31")]
    [InlineData("0001-01-01", "2026-10-08")]
    [InlineData("2026-10-08", "9999-12-31")]
    [InlineData("9999-12-31", "9999-12-31")]
    [InlineData("9999-12-01", "9999-12-31")]
    [InlineData("9999-12-31", "9999-12-01")]
    public void Check_ADayAtAnEndOfTheCalendar_IsOutOfBounds_BeforeAnyOtherCheck(string from, string to)
    {
        Assert.Equal(
            SupplierRangeProblem.OutOfBounds,
            SupplierAgendaRange.Check(DateOnly.Parse(from), DateOnly.Parse(to), 62));
    }

    [Fact]
    public void IsRepresentable_TheEdgesAreExactlyTheMarginFromTheEndsOfTheCalendar()
    {
        var first = DateOnly.MinValue.AddDays(SupplierAgendaLimits.RangeEdgeDays);
        var last = DateOnly.MaxValue.AddDays(-SupplierAgendaLimits.RangeEdgeDays);

        Assert.True(SupplierAgendaRange.IsRepresentable(first));
        Assert.False(SupplierAgendaRange.IsRepresentable(first.AddDays(-1)));
        Assert.True(SupplierAgendaRange.IsRepresentable(last));
        Assert.False(SupplierAgendaRange.IsRepresentable(last.AddDays(1)));
    }

    [Fact]
    public void IsRepresentable_ADayThatPasses_LeavesRoomForTheDaysTheAgendaLooksAheadOrBack()
    {
        var last = DateOnly.MaxValue.AddDays(-SupplierAgendaLimits.RangeEdgeDays);
        var first = DateOnly.MinValue.AddDays(SupplierAgendaLimits.RangeEdgeDays);

        Assert.Null(Record.Exception(() => last.AddDays(SupplierAgendaLimits.ConflictSuggestionDays + 3)));
        Assert.Null(Record.Exception(() => last.AddDays(SupplierAgendaLimits.DefaultCalendarDays)));
        Assert.Null(Record.Exception(() => first.AddDays(-3)));
        Assert.True(SupplierAgendaLimits.RangeEdgeDays > SupplierAgendaLimits.ConflictSuggestionDays + 3);
    }

    [Fact]
    public void Ensure_ARangeThatIsFine_ThrowsNothing()
    {
        SupplierAgendaRange.Ensure(Day, Day.AddDays(61), 62);
    }

    [Theory]
    [InlineData("2026-10-08", "2026-10-07", 62)]
    [InlineData("2026-10-08", "2026-12-09", 62)]
    [InlineData("0001-01-01", "0001-01-31", 62)]
    [InlineData("9999-12-31", "9999-12-31", 62)]
    public void Ensure_ARangeThatIsNotFine_IsAnArgumentOutOfRange_NeverAnOverflowFurtherOn(string from, string to, int maxDays)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SupplierAgendaRange.Ensure(DateOnly.Parse(from), DateOnly.Parse(to), maxDays));
    }
}
