using Casazen.Core.Condominium;
using Casazen.Core.Entities.Enums;
using Xunit;

namespace Casazen.Tests.Unit.Condominium;

public class TenantSettlementMathTests
{
    private static readonly TenantSettlementMath.Occupancy Full = new(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 10));

    [Fact]
    public void Settle_DebitCreditAndEvenUnderOneCent()
    {
        Assert.Equal((120m, TenantSettlementDirection.Debit), Amount(TenantSettlementMath.Settle(1920m, 0m, 1800m)));
        Assert.Equal((-50m, TenantSettlementDirection.Credit), Amount(TenantSettlementMath.Settle(100m, -30m, 120m)));
        Assert.Equal((0m, TenantSettlementDirection.Balanced), Amount(TenantSettlementMath.Settle(1m, 0m, 0.995m)));
        Assert.Equal((0.01m, TenantSettlementDirection.Debit), Amount(TenantSettlementMath.Settle(1m, 0m, 0.99m)));
    }

    [Fact]
    public void Allocate_PartialDays_AndASecondContractTakesTheRest()
    {
        var line = new TenantSettlementMath.LineInput(Guid.NewGuid(), 100m, Full.Start, Full.End, true);
        var first = TenantSettlementMath.Allocate([line], new TenantSettlementMath.Occupancy(Full.Start, new DateOnly(2025, 1, 4)), TenantAllocationRule.DailyProration);
        var second = TenantSettlementMath.Allocate([line], new TenantSettlementMath.Occupancy(new DateOnly(2025, 1, 5), Full.End), TenantAllocationRule.DailyProration);
        Assert.Equal(40m, Assert.Single(first).AllocatedAmount);
        Assert.Equal(60m, Assert.Single(second).AllocatedAmount);
    }

    [Fact]
    public void Allocate_NeedsReviewAndUnconfirmedExtraordinary_AreNotCharged()
    {
        var ordinary = new TenantSettlementMath.LineInput(Guid.NewGuid(), 100m, Full.Start, Full.End, false);
        var result = TenantSettlementMath.Allocate([ordinary], Full, TenantAllocationRule.DailyProration);
        Assert.False(Assert.Single(result).Charged);
        Assert.Equal(0m, result[0].AllocatedAmount);
    }

    [Fact]
    public void Allocate_ContractRule_OverridesDaily()
    {
        Assert.Equal(TenantAllocationRule.MonthlyProration, TenantSettlementMath.RuleFromContract("mensile"));
        Assert.Equal(TenantAllocationRule.DailyProration, TenantSettlementMath.RuleFromContract(null));
        Assert.Equal(TenantAllocationRule.ContractSpecific, TenantSettlementMath.RuleFromContract("come da regolamento speciale"));
        var line = new TenantSettlementMath.LineInput(Guid.NewGuid(), 100m, Full.Start, Full.End, true);
        var skipped = TenantSettlementMath.Allocate([line], Full, TenantAllocationRule.ContractSpecific);
        Assert.False(Assert.Single(skipped).Charged);
    }

    [Fact]
    public void Snapshot_ReplaysTheSameAmount()
    {
        var snapshot = new TenantSettlementMath.Snapshot
        {
            Recoverable = 1920m,
            Prior = 0m,
            Advances = 1800m,
            Amount = 120m,
            Direction = TenantSettlementDirection.Debit,
        };
        var replay = TenantSettlementMath.Replay(TenantSettlementMath.Serialize(snapshot));
        Assert.Equal(120m, replay.Amount);
    }

    [Fact]
    public void Proposal_FixedAdvanceWins_BudgetSplits_ManualIsSettledLater()
    {
        var period = Guid.NewGuid();
        var fixedAdvance = TenantExpenseProposal.Resolve(
            true, period, "2025", 1920m,
            new TenantExpenseProposal.Request(CondominiumAdvanceMode.FixedContractAmount, 150m, RentCadence.Monthly, null, null, null));
        Assert.Equal(TenantExpenseProposal.Source.FixedContract, fixedAdvance.Source);
        Assert.Equal(150m, fixedAdvance.InstallmentAmount);
        Assert.False(fixedAdvance.SettledAtConsuntivo);

        var budget = TenantExpenseProposal.Resolve(
            true, period, "2025", 1920m,
            new TenantExpenseProposal.Request(CondominiumAdvanceMode.BudgetAllocation, null, RentCadence.Monthly, null, null, null));
        Assert.Equal(TenantExpenseProposal.Source.ConfirmedBudget, budget.Source);
        Assert.Equal(160m, budget.InstallmentAmount);
        Assert.Equal(1920m, budget.AnnualAmount);

        var manual = TenantExpenseProposal.Resolve(
            false, null, null, null,
            new TenantExpenseProposal.Request(null, null, RentCadence.Monthly, null, null, 150m));
        Assert.Equal(TenantExpenseProposal.Source.Manual, manual.Source);
        Assert.True(manual.SettledAtConsuntivo);
        Assert.Equal(150m, manual.InstallmentAmount);
    }

    private static (decimal Amount, TenantSettlementDirection Direction) Amount(TenantSettlementMath.Totals totals) =>
        (totals.Amount, totals.Direction);
}
