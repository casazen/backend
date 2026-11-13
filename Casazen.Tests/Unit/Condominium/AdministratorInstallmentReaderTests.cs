using Casazen.Core.Condominium;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Condominium;

public class AdministratorInstallmentReaderTests
{
    [Fact]
    public void ReadsApartmentDatesAndAmountsWithoutPostingOrInventingSplits()
    {
        var result = AdministratorInstallmentReader.Read("Rata 2 30/06/2027 € 1.250,35\nRata 1 31/01/2027 350,00\nTotale 1.600,35");
        Assert.Equal(2, result.Installments.Count);
        Assert.Equal(new DateOnly(2027, 1, 31), result.Installments[0].DueDate);
        Assert.Equal(1250.35m, result.Installments[1].Ordinary);
        Assert.All(result.Installments, row => Assert.Equal(0m, row.Extraordinary));
    }

    [Fact]
    public void DoesNotGuessAmbiguousColumnsOrDuplicateDates()
    {
        var result = AdministratorInstallmentReader.Read("31/01/2027 100,00 200,00\n31/03/2027 150,00\n31/03/2027 250,00\n31/02/2027 50,00");
        Assert.Single(result.Installments);
        Assert.Equal(2, result.NotExtracted.Count);
    }

    [Fact]
    public void NonUniformAmountsArePreservedAndSortedWithPriorOnFirstOnly()
    {
        var rows = OwnerInstallmentPlan.GenerateCustom(new DateOnly(2027, 1, 1), 1800m, 300m, 70m,
            [new(new DateOnly(2027, 6, 30), 1000m, 200m), new(new DateOnly(2027, 1, 31), 800m, 100m)]);
        Assert.Equal(970m, rows[0].Total);
        Assert.Equal(1200m, rows[1].Total);
        Assert.Equal(0m, rows[1].Prior);
        Assert.Equal(2170m, rows.Sum(row => row.Total));
    }

    [Fact]
    public void RejectsPlansThatDoNotMatchBudgetOrFiscalYear()
    {
        Assert.Throws<ArgumentException>(() => OwnerInstallmentPlan.GenerateCustom(new DateOnly(2027, 1, 1), 1800m, 0m, 0m,
            [new CustomOwnerInstallment(new DateOnly(2027, 2, 1), 1700m, 0m)]));
        Assert.Throws<ArgumentException>(() => OwnerInstallmentPlan.GenerateCustom(new DateOnly(2027, 1, 1), 1800m, 0m, 0m,
            [new CustomOwnerInstallment(new DateOnly(2028, 2, 1), 1800m, 0m)]));
    }
}
