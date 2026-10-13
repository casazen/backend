using System.Globalization;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SU-11 (A4-15): calendar dates of the supplier KPI periods, for a Europe/Rome "today".</summary>
public class SupplierKpiPeriodsTests
{
    [Theory]
    [InlineData(SupplierKpiPeriod.CurrentMonth, "2026-09-25", "2026-09-01", "2026-09-25")]
    [InlineData(SupplierKpiPeriod.CurrentMonth, "2026-09-01", "2026-09-01", "2026-09-01")]
    [InlineData(SupplierKpiPeriod.PreviousMonth, "2026-09-25", "2026-08-01", "2026-08-31")]
    [InlineData(SupplierKpiPeriod.PreviousMonth, "2026-03-10", "2026-02-01", "2026-02-28")]
    [InlineData(SupplierKpiPeriod.PreviousMonth, "2026-01-15", "2025-12-01", "2025-12-31")]
    [InlineData(SupplierKpiPeriod.Last30Days, "2026-09-25", "2026-08-27", "2026-09-25")]
    [InlineData(SupplierKpiPeriod.CurrentYear, "2026-09-25", "2026-01-01", "2026-09-25")]
    public void Resolve_Period_ReturnsItsRomeCalendarDates(SupplierKpiPeriod period, string today, string from, string to)
    {
        var (actualFrom, actualTo) = SupplierKpiPeriods.Resolve(period, Date(today));

        Assert.Equal(Date(from), actualFrom);
        Assert.Equal(Date(to), actualTo);
    }

    [Fact]
    public void Resolve_UndefinedPeriod_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SupplierKpiPeriods.Resolve((SupplierKpiPeriod)99, new DateOnly(2026, 9, 25)));
    }

    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
