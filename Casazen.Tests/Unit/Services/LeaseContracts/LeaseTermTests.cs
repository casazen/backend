using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Services.LeaseContracts;

/// <summary>LT-03 (A7-03): the contract states the term computed from the dates, never a fixed "3+2".</summary>
public class LeaseTermTests
{
    [Theory]
    [InlineData("2026-09-01", "2030-08-31", 48, 0, "4 anni")]
    [InlineData("2026-09-01", "2029-08-31", 36, 0, "3 anni")]
    [InlineData("2026-09-01", "2028-02-29", 18, 0, "18 mesi")]
    [InlineData("2026-09-01", "2027-08-31", 12, 0, "1 anno")]
    [InlineData("2026-09-01", "2026-09-30", 1, 0, "1 mese")]
    [InlineData("2026-09-01", "2026-11-10", 2, 10, "2 mesi e 10 giorni")]
    [InlineData("2026-09-01", "2030-09-01", 48, 1, "4 anni e 1 giorno")]
    [InlineData("2026-09-15", "2026-09-15", 0, 1, "1 giorno")]
    public void Between_InclusiveEndDate_ReturnsActualTerm(string start, string end, int months, int days, string italian)
    {
        var term = LeaseTerm.Between(Utc(start), Utc(end));

        Assert.NotNull(term);
        Assert.Equal(new LeaseTerm(months, days), term);
        Assert.Equal(italian, term.Value.ToItalianText());
    }

    [Fact]
    public void Between_EndBeforeStart_ReturnsNull()
    {
        Assert.Null(LeaseTerm.Between(Utc("2026-09-01"), Utc("2026-08-31")));
    }

    private static DateTime Utc(string date) =>
        DateTime.SpecifyKind(DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
}
