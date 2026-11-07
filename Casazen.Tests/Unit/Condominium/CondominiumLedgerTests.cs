using Casazen.Core.Condominium;
using Xunit;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Tests.Unit.Condominium;

/// <summary>CondoLedger's year: the settlement formula, the rate plan, and recoverability.</summary>
public class CondominiumLedgerTests
{
    [Fact]
    public void Conguaglio_CasaA_OpenRates_LeaveADebit()
    {
        var result = Conguaglio.Compute(490.30m, 0m, 0m, 412.80m);
        Assert.NotNull(result);
        Assert.Equal(77.50m, result.Amount);
        Assert.Equal(Conguaglio.Direction.Debit, result.Direction);
    }

    [Fact]
    public void Conguaglio_PriorYearRecovery_IsNotCountedTwice()
    {
        var result = Conguaglio.Compute(2301.64m, 0m, 77.50m, 2464.37m);
        Assert.NotNull(result);
        Assert.Equal(-85.23m, result.Amount);
        Assert.Equal(Conguaglio.Direction.Credit, result.Direction);
    }

    [Fact]
    public void Conguaglio_Extraordinary_StaysAmongWhatIsOwed()
    {
        var result = Conguaglio.Compute(2400m, 600m, 0m, 2400m);
        Assert.NotNull(result);
        Assert.Equal(600m, result.Amount);
    }

    [Fact]
    public void Conguaglio_IsNotTheGapBetweenBudgetAndFinal()
    {
        var paidUp = Conguaglio.Compute(1100m, 0m, 0m, 1000m);
        var behind = Conguaglio.Compute(1100m, 0m, 0m, 500m);
        Assert.Equal(100m, paidUp!.Amount);
        Assert.Equal(600m, behind!.Amount);
    }

    [Fact]
    public void Conguaglio_WithoutAFinal_IsNothing()
    {
        Assert.Null(Conguaglio.Compute(0m, 0m, 0m, 1200m));
    }

    [Fact]
    public void FiscalYear_StartingInJune_IsNotTheCalendarYear()
    {
        var year = FiscalYear.ForStartYear(2024, 6);
        Assert.Equal("2024/2025", year.Label);
        Assert.Equal(new DateOnly(2024, 6, 1), year.Start);
        Assert.Equal(new DateOnly(2025, 5, 31), year.End);
        Assert.True(FiscalYear.TryParseLabel("2024/2025", 6, out var parsed, out _));
        Assert.Equal(year, parsed);
    }

    [Fact]
    public void Generate_SplitsOrdinaryAndExtraordinaryApart_AndPutsThePriorBalanceOnTheFirstRate()
    {
        var rows = OwnerInstallmentPlan.Generate(
            new DateOnly(2025, 6, 1), CondominiumInstallmentCadence.Monthly, 100m, 10m, 77.50m);

        Assert.Equal(12, rows.Count);
        Assert.Equal(100m, rows.Sum(row => row.Ordinary));
        Assert.Equal(10m, rows.Sum(row => row.Extraordinary));
        Assert.Equal(77.50m, rows[0].Prior);
        Assert.All(rows.Skip(1), row => Assert.Equal(0m, row.Prior));
        Assert.Equal(8.33m, rows[0].Ordinary);
        Assert.Equal(8.37m, rows[^1].Ordinary);
        Assert.NotEqual(rows[0].Ordinary, rows[0].Extraordinary);
    }

    [Fact]
    public void ApplyRecovery_DoesNotChangeIssuedOrdinaryOrExtraordinary()
    {
        var issued = OwnerInstallmentPlan.Generate(
            new DateOnly(2025, 6, 1), CondominiumInstallmentCadence.Monthly, 120m, 60m, 0m);
        var ordinary = issued.Select(row => row.Ordinary).ToArray();
        var extraordinary = issued.Select(row => row.Extraordinary).ToArray();

        var next = OwnerInstallmentPlan.ApplyRecovery(issued, 77.50m, PriorYearBalanceDisposition.NextInstallment, issued[0].DueDate);
        var separate = OwnerInstallmentPlan.ApplyRecovery(issued, 77.50m, PriorYearBalanceDisposition.SeparateInstallment, FiscalYear.SeparateBalanceDueDate(new DateOnly(2025, 6, 1)));
        var carry = OwnerInstallmentPlan.ApplyRecovery(issued, 77.50m, PriorYearBalanceDisposition.CarryForward, issued[0].DueDate);
        var refund = OwnerInstallmentPlan.ApplyRecovery(issued, -40m, PriorYearBalanceDisposition.Refund, issued[0].DueDate);

        Assert.Equal(ordinary, next.Select(row => row.Ordinary));
        Assert.Equal(extraordinary, next.Select(row => row.Extraordinary));
        Assert.Equal(77.50m, next[0].Prior);
        Assert.All(next.Skip(1), row => Assert.Equal(0m, row.Prior));

        Assert.Equal(issued.Count + 1, separate.Count);
        Assert.Equal(ordinary, separate.Take(issued.Count).Select(row => row.Ordinary));
        Assert.Equal(extraordinary, separate.Take(issued.Count).Select(row => row.Extraordinary));
        Assert.True(separate[^1].IsPriorOnly);
        Assert.Equal(77.50m, separate[^1].Prior);

        Assert.Same(issued, carry);
        Assert.Same(issued, refund);
    }

    [Fact]
    public void Classification_OrdinaryIsRecoverable_ExtraordinaryAndUnknownAreNotCharged()
    {
        var ordinary = ExpenseClassification.Apply(CondominiumExpenseNature.Ordinary, 100m, null, null, ExpenseClassificationSource.Manual, null);
        var low = ExpenseClassification.Apply(CondominiumExpenseNature.Ordinary, 100m, null, null, ExpenseClassificationSource.Document, 0.5m);
        var extraordinary = ExpenseClassification.Apply(CondominiumExpenseNature.Extraordinary, 600m, null, null, ExpenseClassificationSource.Document, 0.99m);
        var unknown = ExpenseClassification.Apply(CondominiumExpenseNature.Unknown, 10m, null, null, ExpenseClassificationSource.Rule, null);

        Assert.Equal(TenantRecoverability.Recoverable, ordinary.Recoverability);
        Assert.True(ordinary.ChargesTenantAutomatically);
        Assert.False(low.ChargesTenantAutomatically);
        Assert.Equal(TenantRecoverability.NeedsReview, extraordinary.Recoverability);
        Assert.Equal(0m, extraordinary.RecoverableAmount);
        Assert.False(extraordinary.ChargesTenantAutomatically);
        Assert.False(unknown.ChargesTenantAutomatically);
    }

    [Fact]
    public void StatementFile_BudgetAndFinal_ParseTheSameNumbersAPersonWouldType()
    {
        const string budget = """
            condo-statement-v1
            kind: budget
            label: 2025/2026
            ordinary: 2386.87
            extraordinary: 600.00
            extraordinary_description: Facciata
            """;
        Assert.True(StatementFile.TryParse(budget, out var parsed, out _));
        Assert.Equal(CondominiumStatementKind.Budget, parsed!.Kind);
        Assert.Equal(2386.87m, parsed.Ordinary);
        Assert.Equal(600m, parsed.Extraordinary);

        const string final = "condo-statement-v1 kind: final label: 2024/2025 amount: 490,30 settlement: prossima";
        Assert.True(StatementFile.TryParse(final, out var consuntivo, out _));
        Assert.Equal(490.30m, consuntivo!.FinalAmount);
        Assert.Equal(PriorYearBalanceDisposition.NextInstallment, consuntivo.Settlement);
        Assert.False(StatementFile.TryParse("preventivo 100", out _, out var error));
        Assert.Equal("marker", error);
    }

    [Fact]
    public void Millessimi_AreNotUsedToSplitTheOwnerQuota()
    {
        var rows = OwnerInstallmentPlan.Generate(new DateOnly(2025, 6, 1), CondominiumInstallmentCadence.Single, 490.30m, 0m, 0m);
        Assert.Equal(490.30m, Assert.Single(rows).Total);
    }

    [Fact]
    public void RentLedgerMoney_StillSplitsATenantAdvance_WithoutReadingACondominiumRow()
    {
        var parts = RentLedgerMoney.SplitRemainderOnLast(10m, 12);
        Assert.Equal(10m, parts.Sum());
    }
}
