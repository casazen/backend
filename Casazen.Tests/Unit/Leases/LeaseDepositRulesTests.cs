using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-OPS-1c: the statement of the return of the security deposit. The legal interest of each calendar year is the deposit times the rate of the
/// year times the days it was held in it over the days of the year (365 or 366), to the cent year by year; the days run from the day it was paid to
/// the day before it is returned; the interest already paid and the sums kept come off; a year without a rate, or sums kept above what is owed, are
/// refused.
/// </summary>
public class LeaseDepositRulesTests
{
    private static readonly IReadOnlyList<LeaseDepositDeduction> NoDeductions = [];

    private static LeaseDepositRate Rate(int year, decimal percent) => new(year, percent);

    private static readonly DateOnly Paid = new(2023, 11, 1);
    private static readonly DateOnly Returned = new(2025, 3, 1);

    private static IReadOnlyList<LeaseDepositRate> ThreeYears => [Rate(2023, 5m), Rate(2024, 2.5m), Rate(2025, 2m)];

    [Fact]
    public void Calculate_TheInterestOfEachYear_FollowsTheDaysHeldAndTheDaysOfThatYear_AndTheTotalIsTheirSum()
    {
        // 1,200.00 held from 1 November 2023 to 28 February 2025 (the day of the return earns nothing): 61 days of 2023, the whole of the leap
        // year 2024, 59 days of 2025.
        var outcome = LeaseDepositRules.Calculate(1200m, Paid, Returned, ThreeYears, 0m, NoDeductions);

        Assert.Equal(LeaseDepositProblem.None, outcome.Problem);
        var statement = outcome.Calculation!;
        Assert.Collection(
            statement.Years,
            y => Assert.Equal((2023, 5m, 61, 365, 10.03m), (y.Year, y.RatePercent, y.Days, y.DaysInYear, y.Interest)), // 1200 x 5 % x 61/365 = 10.0273...
            y => Assert.Equal((2024, 2.5m, 366, 366, 30.00m), (y.Year, y.RatePercent, y.Days, y.DaysInYear, y.Interest)), // a whole leap year is exactly the rate
            y => Assert.Equal((2025, 2m, 59, 365, 3.88m), (y.Year, y.RatePercent, y.Days, y.DaysInYear, y.Interest))); // 1200 x 2 % x 59/365 = 3.8794...
        Assert.Equal(43.91m, statement.Interest);
        Assert.Equal(43.91m, statement.InterestDue);
        Assert.Equal(1243.91m, statement.Returned);
    }

    [Fact]
    public void Calculate_TheInterestAlreadyPaidAndTheSumsKept_ComeOffWhatIsReturned()
    {
        IReadOnlyList<LeaseDepositDeduction> kept = [new("Pittura delle pareti", 150m), new("  Pulizie straordinarie ", 50m)];

        var statement = LeaseDepositRules.Calculate(1200m, Paid, Returned, ThreeYears, interestPaid: 30m, kept).Calculation!;

        Assert.Equal(13.91m, statement.InterestDue); // 43.91 accrued, 30.00 already paid
        Assert.Equal(200m, statement.DeductionsTotal);
        Assert.Equal(1013.91m, statement.Returned); // 1200.00 + 13.91 - 200.00
        Assert.Equal("Pulizie straordinarie", statement.Deductions[1].Reason); // the reason is trimmed
    }

    [Fact]
    public void Calculate_RoundsTheInterestOfAYearOnceToTheCent_HalfAwayFromZero()
    {
        // 36.50 at 5 % for one day of a 365-day year is exactly 0.005: half a cent goes up (0.01), where a banker's rounding would stop at 0.00.
        var half = LeaseDepositRules.Calculate(36.50m, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 2), [Rate(2025, 5m)], 0m, NoDeductions).Calculation!;

        Assert.Equal(0.01m, half.Interest);

        // A whole year is exactly the rate of the deposit, leap or not.
        var leap = LeaseDepositRules.Calculate(1000m, new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1), [Rate(2024, 2.5m)], 0m, NoDeductions).Calculation!;
        var common = LeaseDepositRules.Calculate(1000m, new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1), [Rate(2025, 2.5m)], 0m, NoDeductions).Calculation!;
        Assert.Equal(25.00m, leap.Interest);
        Assert.Equal(25.00m, common.Interest);
    }

    [Fact]
    public void YearsHeld_AreTheCalendarYearsWithAtLeastADayHeld_TheDayOfTheReturnDoesNotCount()
    {
        Assert.Equal(new[] { 2023, 2024, 2025 }, LeaseDepositRules.YearsHeld(Paid, Returned));
        Assert.Equal(new[] { 2023 }, LeaseDepositRules.YearsHeld(new DateOnly(2023, 12, 31), new DateOnly(2024, 1, 1)));
        Assert.Equal(new[] { 2024 }, LeaseDepositRules.YearsHeld(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1)));
        Assert.Empty(LeaseDepositRules.YearsHeld(Paid, Paid));
    }

    [Fact]
    public void Calculate_AYearWithoutARate_ADayThatIsNotAfterThePayment_AndABadRate_AreRefused()
    {
        var missing = LeaseDepositRules.Calculate(1200m, Paid, Returned, [Rate(2023, 5m), Rate(2025, 2m)], 0m, NoDeductions);
        Assert.Equal(LeaseDepositProblem.RateMissing, missing.Problem);
        Assert.Equal(new[] { 2024 }, missing.MissingYears);
        Assert.Null(missing.Calculation);

        Assert.Equal(LeaseDepositProblem.DatesInvalid, LeaseDepositRules.Calculate(1200m, Paid, Paid, ThreeYears, 0m, NoDeductions).Problem);
        foreach (var bad in new[] { -0.5m, 100.5m, 2.555m })
        {
            Assert.Equal(
                LeaseDepositProblem.RateInvalid,
                LeaseDepositRules.Calculate(1200m, Paid, Returned, [Rate(2023, bad), Rate(2024, 2m), Rate(2025, 2m)], 0m, NoDeductions).Problem);
        }

        // Two rates for one year.
        Assert.Equal(
            LeaseDepositProblem.RateInvalid,
            LeaseDepositRules.Calculate(1200m, Paid, Returned, [.. ThreeYears, Rate(2024, 3m)], 0m, NoDeductions).Problem);
    }

    [Fact]
    public void Calculate_InterestPaidAboveWhatAccrued_ABadDeduction_AndSumsKeptAboveWhatIsOwed_AreRefused()
    {
        Assert.Equal(LeaseDepositProblem.InterestPaidInvalid, LeaseDepositRules.Calculate(1200m, Paid, Returned, ThreeYears, 43.92m, NoDeductions).Problem);
        Assert.Equal(LeaseDepositProblem.InterestPaidInvalid, LeaseDepositRules.Calculate(1200m, Paid, Returned, ThreeYears, -1m, NoDeductions).Problem);

        foreach (var bad in new LeaseDepositDeduction[] { new("", 10m), new("   ", 10m), new("Danni", 0m), new("Danni", -5m), new("Danni", 10.005m), new(new string('x', 201), 10m) })
            Assert.Equal(LeaseDepositProblem.DeductionInvalid, LeaseDepositRules.Calculate(1200m, Paid, Returned, ThreeYears, 0m, [bad]).Problem);
        var tooMany = Enumerable.Range(0, LeaseDepositRules.MaxDeductions + 1).Select(i => new LeaseDepositDeduction($"Voce {i}", 1m)).ToList();
        Assert.Equal(LeaseDepositProblem.DeductionInvalid, LeaseDepositRules.Calculate(1200m, Paid, Returned, ThreeYears, 0m, tooMany).Problem);

        // 1,200.00 + 43.91 owed: keeping 1,243.92 would leave a debt of the tenant, not a return; keeping exactly what is owed leaves zero.
        Assert.Equal(LeaseDepositProblem.DeductionsExceed, LeaseDepositRules.Calculate(1200m, Paid, Returned, ThreeYears, 0m, [new("Danni", 1243.92m)]).Problem);
        Assert.Equal(0m, LeaseDepositRules.Calculate(1200m, Paid, Returned, ThreeYears, 0m, [new("Danni", 1243.91m)]).Calculation!.Returned);
    }
}
