using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>LT-06 (#269, A7-07): the rent schedule is computed from the lease only (dates, rent, cadence, due day).</summary>
public class RentInstallmentPlanTests
{
    [Fact]
    public void Build_FourYearLeaseStartingOnTheFirst_FortyEightCalendarMonthsDueOnTheBillingDay()
    {
        var plan = RentInstallmentPlan.Build(new DateOnly(2026, 9, 1), new DateOnly(2030, 8, 31), RentCadence.Monthly, 5, 1200m);

        Assert.Equal(48, plan.Installments.Count);
        Assert.Null(plan.PartialFinalPeriod);
        Assert.Equal(new RentInstallment(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 5), 1200m), plan.Installments[0]);
        Assert.Equal(new RentInstallment(new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 28), new DateOnly(2027, 2, 5), 1200m), plan.Installments[5]);
        Assert.Equal(new DateOnly(2030, 8, 31), plan.Installments[^1].PeriodEnd);
        Assert.All(plan.Installments, i => Assert.Equal(1200m, i.Amount));
    }

    [Fact]
    public void Build_LeaseStartingMidMonth_PeriodsAnchoredOnTheStartAndDueOnTheFirstBillingDayInsideThePeriod()
    {
        var plan = RentInstallmentPlan.Build(new DateOnly(2026, 3, 15), new DateOnly(2027, 3, 14), RentCadence.Monthly, 5, 800m);

        Assert.Equal(12, plan.Installments.Count);
        // 15 March - 14 April: the 5th of March is before the period, so the 5th of April.
        Assert.Equal(new RentInstallment(new DateOnly(2026, 3, 15), new DateOnly(2026, 4, 14), new DateOnly(2026, 4, 5), 800m), plan.Installments[0]);
        Assert.All(plan.Installments, i => Assert.InRange(i.DueDate, i.PeriodStart, i.PeriodEnd));
    }

    [Fact]
    public void Build_StartOnTheThirtyFirst_MonthEndsClampedFromTheStartNeverDrift()
    {
        var plan = RentInstallmentPlan.Build(new DateOnly(2027, 1, 31), new DateOnly(2027, 5, 30), RentCadence.Monthly, 28, 500m);

        Assert.Equal(
            [new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 31), new DateOnly(2027, 4, 30)],
            plan.Installments.Select(i => i.PeriodStart));
        Assert.Equal(new DateOnly(2027, 2, 27), plan.Installments[0].PeriodEnd);
        Assert.Equal(new DateOnly(2027, 2, 28), plan.Installments[0].DueDate);
        Assert.Null(plan.PartialFinalPeriod);
    }

    [Theory]
    [InlineData(RentCadence.Monthly, 1, 12)]
    [InlineData(RentCadence.Bimonthly, 2, 6)]
    [InlineData(RentCadence.Quarterly, 3, 4)]
    [InlineData(RentCadence.Semiannual, 6, 2)]
    public void Build_Cadence_OneInstallmentEveryMonthsOfTheCadence(RentCadence cadence, int months, int expected)
    {
        var plan = RentInstallmentPlan.Build(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), cadence, 1, 100m);

        Assert.Equal(months, RentInstallmentPlan.MonthsPer(cadence));
        Assert.Equal(expected, plan.Installments.Count);
        Assert.Equal(new DateOnly(2026, 1, 1).AddMonths(months).AddDays(-1), plan.Installments[0].PeriodEnd);
    }

    [Fact]
    public void Build_LeaseEndingInsideAPeriod_ReturnsThePartialPeriodWithoutInventingAProRataAmount()
    {
        var plan = RentInstallmentPlan.Build(new DateOnly(2026, 1, 1), new DateOnly(2026, 7, 15), RentCadence.Quarterly, 10, 3000m);

        Assert.Equal(2, plan.Installments.Count);
        Assert.Equal(new RentPeriod(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 15)), plan.PartialFinalPeriod);
        Assert.DoesNotContain(plan.Installments, i => i.PeriodEnd > new DateOnly(2026, 7, 15));
    }

    [Theory]
    [InlineData(2026, 9, 1, 5, 2026, 9, 5)]
    [InlineData(2026, 9, 5, 5, 2026, 9, 5)]
    [InlineData(2026, 9, 6, 5, 2026, 10, 5)]
    [InlineData(2026, 12, 20, 1, 2027, 1, 1)]
    public void DueDate_FirstBillingDayOnOrAfterThePeriodStart(int y, int m, int d, int billingDay, int ey, int em, int ed)
    {
        Assert.Equal(new DateOnly(ey, em, ed), RentInstallmentPlan.DueDate(new DateOnly(y, m, d), billingDay));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(31)]
    public void DueDate_BillingDayOutsideOneToTwentyEight_Throws(int billingDay)
    {
        Assert.False(RentInstallmentPlan.IsValidBillingDay(billingDay));
        Assert.Throws<ArgumentOutOfRangeException>(() => RentInstallmentPlan.DueDate(new DateOnly(2026, 1, 1), billingDay));
    }

    [Fact]
    public void Defaults_FromTheLease_StartDayCappedAtTwentyEightAndMonthlyRentTimesMonths()
    {
        Assert.Equal(15, RentInstallmentPlan.DefaultBillingDay(new DateOnly(2026, 3, 15)));
        Assert.Equal(28, RentInstallmentPlan.DefaultBillingDay(new DateOnly(2026, 3, 31)));
        Assert.Equal(3_600.30m, RentInstallmentPlan.DefaultAmount(1_200.10m, RentCadence.Quarterly));
    }

    [Fact]
    public void Build_NonPositiveAmount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RentInstallmentPlan.Build(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), RentCadence.Monthly, 1, 0m));
    }
}
