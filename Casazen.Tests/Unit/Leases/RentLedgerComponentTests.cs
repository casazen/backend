using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// CondoLedger phase 1: a rent line is rent plus condominium advance (and any other part), the total is the sum, a budget
/// does not rewrite a fixed contractual advance, leftover cents land on the last installment, and posting a month twice
/// does not add a second line.
/// </summary>
public class RentLedgerComponentTests
{
    [Fact]
    public void FixedContractAdvance_TwelveMonths_EachInstallmentIsRentPlusAdvance()
    {
        var components = RentLedgerLine.FixedContract(900m, 150m);
        var plan = RentInstallmentPlan.Build(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), RentCadence.Monthly, 1, 1_050m);
        var ledger = new RentMonthLedger();

        Assert.Equal(12, plan.Installments.Count);
        foreach (var installment in plan.Installments)
        {
            var posted = ledger.Post(installment.PeriodStart, installment.PeriodEnd, components);
            Assert.Equal(1_050m, posted.AmountDue);
            Assert.Equal(installment.Amount, posted.AmountDue);
            Assert.Equal(900m, Assert.Single(posted.Components, c => c.Kind == RentLedgerComponentKind.Rent).Amount);
            Assert.Equal(150m, Assert.Single(posted.Components, c => c.Kind == RentLedgerComponentKind.CondominiumAdvance).Amount);
        }

        Assert.Equal(12, ledger.Count);
        Assert.Equal(10_800m, plan.Installments.Count * 900m);
        Assert.Equal(1_800m, plan.Installments.Count * 150m);
        Assert.Equal(12_600m, plan.Installments.Sum(i => i.Amount));
    }

    [Fact]
    public void SplitRemainderOnLast_LeftoverCents_GoOnTheLastInstallmentAndTheSumMatches()
    {
        var three = RentLedgerMoney.SplitRemainderOnLast(100m, 3);
        Assert.Equal([33.33m, 33.33m, 33.34m], three);
        Assert.Equal(100m, three.Sum());

        var year = RentLedgerMoney.SplitRemainderOnLast(10m, 12);
        Assert.Equal(12, year.Count);
        Assert.Equal(0.83m, year[0]);
        Assert.All(year.Take(11), part => Assert.Equal(0.83m, part));
        Assert.Equal(0.87m, year[^1]);
        Assert.Equal(10m, year.Sum());
    }

    [Fact]
    public void EnsureAmountDue_RejectsATotalThatIsNotTheSumOfTheComponents()
    {
        var components = RentLedgerLine.FixedContract(900m, 150m);
        RentLedgerLine.EnsureAmountDue(1_050m, components);

        var error = Assert.Throws<InvalidOperationException>(() => RentLedgerLine.EnsureAmountDue(900m, components));
        Assert.Contains("1050", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Post_SameMonthTwice_KeepsTheFirstLine()
    {
        var ledger = new RentMonthLedger();
        var january = new DateOnly(2026, 1, 1);
        var first = ledger.Post(january, new DateOnly(2026, 1, 31), RentLedgerLine.FixedContract(900m, 150m));
        var again = ledger.Post(january, new DateOnly(2026, 1, 31), RentLedgerLine.FixedContract(1_000m, 0m));

        Assert.Same(first, again);
        Assert.Equal(1, ledger.Count);
        Assert.Equal(1_050m, again.AmountDue);
        Assert.Equal(2, again.Components.Count);
    }

    [Fact]
    public void ChargeableAdvance_FixedContractAmount_WinsOverABudget()
    {
        var confirmed = new RentCondominiumTerms(
            CondominiumAdvanceMode.FixedContractAmount, 150m, RentCadence.Monthly, Confirmed: true);
        var unconfirmed = confirmed with { Confirmed = false };
        var fromBudget = new RentCondominiumTerms(
            CondominiumAdvanceMode.BudgetAllocation, AdvanceAmount: null, RentCadence.Monthly, Confirmed: true);
        var needsReview = new RentCondominiumTerms(
            CondominiumAdvanceMode.NeedsReview, 150m, RentCadence.Monthly, Confirmed: true);

        Assert.Equal(150m, confirmed.ChargeableAdvance(budgetAmount: 200m));
        Assert.Equal(0m, unconfirmed.ChargeableAdvance(budgetAmount: 200m));
        Assert.Equal(200m, fromBudget.ChargeableAdvance(budgetAmount: 200m));
        Assert.Equal(0m, needsReview.ChargeableAdvance(budgetAmount: 200m));
        Assert.Equal(150m, confirmed.UniformAdvanceFor(RentCadence.Monthly));
        Assert.Equal(450m, confirmed.UniformAdvanceFor(RentCadence.Quarterly));
    }

    [Fact]
    public void InstallmentTotalAfterTerms_ChangedFixedAdvance_ReplacesThePreviousOne()
    {
        var previous = new RentCondominiumTerms(
            CondominiumAdvanceMode.FixedContractAmount, 150m, RentCadence.Monthly, Confirmed: true);
        var next = previous with { AdvanceAmount = 200m };

        var total = RentLedgerLine.InstallmentTotalAfterTerms(
            1_050m,
            900m,
            previous.UniformAdvanceFor(RentCadence.Monthly),
            next.UniformAdvanceFor(RentCadence.Monthly));

        Assert.Equal(1_100m, total);
        Assert.Equal(
            1_200m,
            RentLedgerLine.InstallmentTotalAfterTerms(1_200m, 900m, 150m, 200m));
    }

    [Fact]
    public void InstallmentTotalAfterTerms_None_DropsTheAdvanceFromThePlanTotal()
    {
        var previous = new RentCondominiumTerms(
            CondominiumAdvanceMode.FixedContractAmount, 150m, RentCadence.Monthly, Confirmed: true);
        var none = new RentCondominiumTerms(
            CondominiumAdvanceMode.None, AdvanceAmount: null, Cadence: null, Confirmed: true);

        var total = RentLedgerLine.InstallmentTotalAfterTerms(
            1_050m,
            900m,
            previous.UniformAdvanceFor(RentCadence.Monthly),
            none.UniformAdvanceFor(RentCadence.Monthly));

        Assert.Equal(0m, none.ChargeableAdvance(budgetAmount: 200m));
        Assert.Equal(900m, total);
        Assert.Equal(900m, RentLedgerLine.Sum(RentLedgerLine.ForStoredTotal(total, 900m, none.UniformAdvanceFor(RentCadence.Monthly))));
        Assert.Equal(RentLedgerComponentKind.Rent, Assert.Single(RentLedgerLine.ForStoredTotal(total, 900m, 0m)).Kind);
    }
}
