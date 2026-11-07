using Casazen.Core.Authorization;
using Xunit;
using Casazen.Core.Condominium;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Casazen.Tests.Unit.Condominium;

public class CondominiumAccountingServiceTests
{
    [Fact]
    public async Task BudgetThenFinal_KeepsIssuedRates_AndCarriesTheBalance()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var service = Service(db);
        var condo = await service.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa A", null, null, null, 6), "user");
        var period = await service.CreatePeriodAsync(scope, condo.Id, new CreateCondominiumPeriodCommand("2024/2025"), "user");

        var budget = await service.ConfirmBudgetAsync(
            scope, condo.Id, period.Id, new ConfirmBudgetCommand(490.30m, "Preventivo", null, CondominiumInstallmentCadence.Monthly), "user");
        var issuedOrdinary = budget.Installments.Select(row => row.OrdinaryAmount).ToArray();
        var again = await service.ConfirmBudgetAsync(
            scope, condo.Id, period.Id, new ConfirmBudgetCommand(999m, "Preventivo corretto", null, CondominiumInstallmentCadence.Single), "user");

        Assert.True(again.InstallmentsLeftAsIssued);
        Assert.Equal(issuedOrdinary, again.Installments.Select(row => row.OrdinaryAmount));
        Assert.Equal(2, again.Statement.RevisionNumber);
        var superseded = await db.CondominiumStatements.SingleAsync(statement => statement.RevisionNumber == 1);
        Assert.Equal(CondominiumStatementStatus.Superseded, superseded.Status);
        Assert.Equal(490.30m, superseded.TotalAmount);

        await service.RecordPaymentAsync(
            scope, condo.Id, period.Id, new RecordCondominiumPaymentCommand(null, false, 412.80m, new DateOnly(2025, 5, 1), "Bonifico", null, "casa-a-2025", null), "user");
        var duplicate = await service.RecordPaymentAsync(
            scope, condo.Id, period.Id, new RecordCondominiumPaymentCommand(null, false, 412.80m, new DateOnly(2025, 5, 1), "Bonifico", null, "casa-a-2025", null), "user");
        Assert.True(duplicate.AlreadyRecorded);
        await Assert.ThrowsAsync<DomainConflictException>(() => service.RecordPaymentAsync(
            scope, condo.Id, period.Id, new RecordCondominiumPaymentCommand(null, false, 10m, new DateOnly(2025, 5, 2), "Bonifico", null, "casa-a-2025", null), "user"));

        var final = await service.ConfirmFinalAsync(
            scope, condo.Id, period.Id, new ConfirmFinalCommand(490.30m, PriorYearBalanceDisposition.CarryForward), "user");
        Assert.NotNull(final.NextYearBalance);
        Assert.Equal(77.50m, final.NextYearBalance.Amount);
        Assert.Equal(PriorYearBalanceDisposition.CarryForward, final.NextYearBalance.Disposition);
        var nextRates = await db.CondominiumInstallments.Where(row => row.CondominiumPeriodId == final.NextYearBalance.PeriodId).ToListAsync();
        Assert.Empty(nextRates);

        var report = await service.GetReportAsync(scope, condo.Id, period.Id);
        Assert.Equal(0m, report.Extraordinary);
        Assert.Equal(412.80m, report.Paid);
        Assert.Equal(77.50m, report.Settlement!.Amount);
        var paidPdf = await service.RenderPaidPdfAsync(scope, condo.Id, period.Id);
        var unpaidPdf = await service.RenderUnpaidPdfAsync(scope, condo.Id, period.Id);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(paidPdf));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(unpaidPdf));
    }

    [Fact]
    public async Task Import_PostsTheFileIntoTheNamedYear_AndManualEntryUsesTheSamePath()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var service = Service(db);
        var condo = await service.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa B", null, null, null, 6), "user");
        var text = """
            condo-statement-v1
            kind: budget
            label: 2024/2025
            ordinary: 2400.00
            extraordinary: 600.00
            extraordinary_description: Facciata
            """u8.ToArray();
        var imported = await service.ImportStatementFileAsync(scope, condo.Id, new StatementFileUpload("preventivo.txt", text), "user");
        Assert.Equal(CondominiumStatementKind.Budget, imported.Kind);
        var report = await service.GetReportAsync(scope, condo.Id, imported.PeriodId);
        Assert.Equal(2400m, report.Ordinary);
        Assert.Equal(600m, report.Extraordinary);
        Assert.Equal(12, report.Installments.Count);
        Assert.Equal(600m, report.Installments.Sum(row => row.Extraordinary));
        var issued = report.Installments.Select(row => row.Ordinary).ToArray();
        await service.RecordPaymentAsync(
            scope, condo.Id, imported.PeriodId, new RecordCondominiumPaymentCommand(null, false, 2400m, new DateOnly(2025, 5, 1), "Bonifico", null, null, null), "user");

        var final = """
            condo-statement-v1
            kind: final
            label: 2024/2025
            amount: 2400.00
            settlement: carry
            """u8.ToArray();
        await service.ImportStatementFileAsync(scope, condo.Id, new StatementFileUpload("consuntivo.txt", final), "user");
        report = await service.GetReportAsync(scope, condo.Id, imported.PeriodId);
        Assert.Equal(600m, report.Settlement!.Amount);
        Assert.Equal(issued, report.Installments.Select(row => row.Ordinary));
    }

    [Fact]
    public async Task NextInstallment_WritesOnlyThePriorColumn()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var service = Service(db);
        var condo = await service.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa", null, null, null, 6), "user");
        var period = await service.CreatePeriodAsync(scope, condo.Id, new CreateCondominiumPeriodCommand("2025/2026"), "user");
        var budget = await service.ConfirmBudgetAsync(
            scope, condo.Id, period.Id, new ConfirmBudgetCommand(120m, null, null, CondominiumInstallmentCadence.Monthly), "user");
        var before = budget.Installments.Select(row => (row.OrdinaryAmount, row.ExtraordinaryAmount)).ToArray();

        await service.RecordPriorYearBalanceAsync(
            scope, condo.Id, period.Id, new RecordPriorYearBalanceCommand(77.50m, PriorYearBalanceDisposition.NextInstallment, null, "Conguaglio"), "user");

        var after = await db.CondominiumInstallments.Where(row => row.CondominiumPeriodId == period.Id).OrderBy(row => row.SlotIndex).ToListAsync();
        Assert.Equal(before, after.Select(row => (row.OrdinaryAmount, row.ExtraordinaryAmount)));
        Assert.Equal(77.50m, after[0].PriorSettlementAmount);
        Assert.All(after.Skip(1), row => Assert.Equal(0m, row.PriorSettlementAmount));
    }

    [Fact]
    public async Task UnlinkedCondominium_IsOnlyForAnOrgWideScope()
    {
        await using var db = NewDb();
        var orgWide = await SeedAsync(db);
        var restricted = orgWide with { OwnerId = "user" };
        var service = Service(db);

        var denied = await Assert.ThrowsAsync<DomainForbiddenException>(() =>
            service.CreateCondominiumAsync(restricted, new CreateCondominiumCommand("Casa", null, null, null, 6), "user"));
        Assert.Equal(CondominiumErrorCodes.NeedsWholeOrg, denied.Code);

        var condo = await service.CreateCondominiumAsync(orgWide, new CreateCondominiumCommand("Casa", null, null, null, 6), "user");
        var period = await service.CreatePeriodAsync(orgWide, condo.Id, new CreateCondominiumPeriodCommand("2024/2025"), "user");
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetReportAsync(restricted, condo.Id, period.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ConfirmBudgetAsync(
            restricted, condo.Id, period.Id, new ConfirmBudgetCommand(100m, null, null, CondominiumInstallmentCadence.Monthly), "user"));

        var propertyId = await db.Properties.Select(property => property.Id).SingleAsync();
        await service.LinkPropertyAsync(orgWide, condo.Id, new LinkPropertyCommand(propertyId, null, null, null, null), "user");
        var report = await service.GetReportAsync(restricted, condo.Id, period.Id);
        Assert.Equal("Casa", report.CondominiumName);

        var outsider = orgWide with { OwnerId = "someone-else" };
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetReportAsync(outsider, condo.Id, period.Id));
    }

    [Fact]
    public async Task Recovery_ClearsThePreviousPrior_AndAnEvenFinalRemovesTheCarry()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var service = Service(db);
        var condo = await service.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa", null, null, null, 6), "user");
        var first = await service.CreatePeriodAsync(scope, condo.Id, new CreateCondominiumPeriodCommand("2024/2025"), "user");
        var second = await service.CreatePeriodAsync(scope, condo.Id, new CreateCondominiumPeriodCommand("2025/2026"), "user");
        await service.ConfirmBudgetAsync(scope, condo.Id, first.Id, new ConfirmBudgetCommand(100m, null, null, CondominiumInstallmentCadence.Single), "user");
        var nextPlan = await service.ConfirmBudgetAsync(
            scope, condo.Id, second.Id, new ConfirmBudgetCommand(120m, null, null, CondominiumInstallmentCadence.Monthly), "user");
        var ordinary = nextPlan.Installments.Select(row => row.OrdinaryAmount).ToArray();
        var extraordinary = nextPlan.Installments.Select(row => row.ExtraordinaryAmount).ToArray();

        await service.ConfirmFinalAsync(scope, condo.Id, first.Id, new ConfirmFinalCommand(100m, PriorYearBalanceDisposition.NextInstallment), "user");
        var withPrior = await db.CondominiumInstallments.Where(row => row.CondominiumPeriodId == second.Id).OrderBy(row => row.SlotIndex).ToListAsync();
        Assert.Equal(100m, withPrior[0].PriorSettlementAmount);
        Assert.Equal(CondominiumInstallmentStatus.Scheduled, withPrior[0].Status);

        await service.RecordPriorYearBalanceAsync(
            scope, condo.Id, second.Id, new RecordPriorYearBalanceCommand(100m, PriorYearBalanceDisposition.SeparateInstallment, first.Id, "Conguaglio"), "user");
        var separate = await db.CondominiumInstallments.Where(row => row.CondominiumPeriodId == second.Id && row.Status != CondominiumInstallmentStatus.Cancelled).OrderBy(row => row.SlotIndex).ToListAsync();
        Assert.Equal(100m, separate.Sum(row => row.PriorSettlementAmount));
        Assert.Equal(0m, separate.Where(row => row.OrdinaryAmount != 0m).Sum(row => row.PriorSettlementAmount));
        Assert.Equal(new DateOnly(2025, 8, 31), separate.Single(row => row.OrdinaryAmount == 0m && row.ExtraordinaryAmount == 0m).DueDate);

        await service.RecordPaymentAsync(
            scope, condo.Id, first.Id, new RecordCondominiumPaymentCommand(null, false, 100m, new DateOnly(2025, 5, 1), "Bonifico", null, null, null), "user");
        var even = await service.ConfirmFinalAsync(scope, condo.Id, first.Id, new ConfirmFinalCommand(100m, PriorYearBalanceDisposition.CarryForward), "user");
        Assert.Null(even.NextYearBalance);
        Assert.False(await db.CondominiumPriorBalances.AnyAsync(balance => balance.CondominiumPeriodId == second.Id));
        var cleared = await db.CondominiumInstallments.Where(row => row.CondominiumPeriodId == second.Id && row.Status != CondominiumInstallmentStatus.Cancelled).OrderBy(row => row.SlotIndex).ToListAsync();
        Assert.All(cleared, row => Assert.Equal(0m, row.PriorSettlementAmount));
        Assert.Equal(ordinary, cleared.Where(row => row.OrdinaryAmount != 0m).Select(row => row.OrdinaryAmount));
        Assert.Equal(extraordinary, cleared.Where(row => row.OrdinaryAmount != 0m).Select(row => row.ExtraordinaryAmount));

        await service.RecordPriorYearBalanceAsync(
            scope, condo.Id, second.Id, new RecordPriorYearBalanceCommand(40m, PriorYearBalanceDisposition.SeparateInstallment, first.Id, "Conguaglio"), "user");
        var reopened = await db.CondominiumInstallments.Where(row => row.CondominiumPeriodId == second.Id).ToListAsync();
        var priorRow = reopened.Single(row => row.Status != CondominiumInstallmentStatus.Cancelled && row.OrdinaryAmount == 0m && row.ExtraordinaryAmount == 0m);
        Assert.Equal(40m, priorRow.PriorSettlementAmount);
        Assert.Equal(new DateOnly(2025, 8, 31), priorRow.DueDate);
        Assert.Single(reopened, row => row.SlotIndex == priorRow.SlotIndex);
    }

    [Fact]
    public async Task Prior_GoesOnTheNextUnpaidRate_AndAYearPaymentReducesTheYear()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var service = Service(db);
        var condo = await service.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa", null, null, null, 6), "user");
        var period = await service.CreatePeriodAsync(scope, condo.Id, new CreateCondominiumPeriodCommand("2025/2026"), "user");
        var budget = await service.ConfirmBudgetAsync(
            scope, condo.Id, period.Id, new ConfirmBudgetCommand(120m, null, null, CondominiumInstallmentCadence.Monthly), "user");
        var first = budget.Installments[0];
        await service.RecordPaymentAsync(
            scope, condo.Id, period.Id, new RecordCondominiumPaymentCommand(first.Id, false, first.TotalAmount, new DateOnly(2025, 6, 15), "Bonifico", null, null, null), "user");

        await service.RecordPriorYearBalanceAsync(
            scope, condo.Id, period.Id, new RecordPriorYearBalanceCommand(40m, PriorYearBalanceDisposition.NextInstallment, null, "Conguaglio"), "user");
        var rates = await db.CondominiumInstallments.Where(row => row.CondominiumPeriodId == period.Id).OrderBy(row => row.SlotIndex).ToListAsync();
        Assert.Equal(CondominiumInstallmentStatus.Paid, rates[0].Status);
        Assert.Equal(0m, rates[0].PriorSettlementAmount);
        Assert.Equal(40m, rates[1].PriorSettlementAmount);
        Assert.Equal(budget.Installments.Select(row => row.OrdinaryAmount), rates.Select(row => row.OrdinaryAmount));

        await service.RecordPaymentAsync(
            scope, condo.Id, period.Id, new RecordCondominiumPaymentCommand(null, false, 25m, new DateOnly(2025, 6, 20), "Bonifico", null, null, null), "user");
        var report = await service.GetReportAsync(scope, condo.Id, period.Id);
        var open = report.Installments.Where(row => row.IsOpen).Sum(row => row.Remaining);
        Assert.Equal(report.StillToPay, open);
        Assert.Equal(10m, report.Installments[0].Paid);
        Assert.True(report.Installments.Count(row => row.Paid > 0m) > 1);
        var statuses = await db.CondominiumInstallments.Where(row => row.CondominiumPeriodId == period.Id).OrderBy(row => row.SlotIndex).ToListAsync();
        Assert.Equal(CondominiumInstallmentStatus.Paid, statuses[0].Status);
        Assert.Equal(CondominiumInstallmentStatus.PartiallyPaid, statuses[1].Status);
        Assert.Equal(CondominiumInstallmentStatus.Scheduled, statuses[2].Status);
    }

    [Fact]
    public async Task YearPayment_MarksTheRatesItCovers_AndThePriorSkipsThem()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var service = Service(db);
        var condo = await service.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa A", null, null, null, 6), "user");
        var period = await service.CreatePeriodAsync(scope, condo.Id, new CreateCondominiumPeriodCommand("2025/2026"), "user");
        await service.ConfirmBudgetAsync(
            scope, condo.Id, period.Id, new ConfirmBudgetCommand(120m, null, null, CondominiumInstallmentCadence.Monthly), "user");
        await service.RecordPaymentAsync(
            scope, condo.Id, period.Id, new RecordCondominiumPaymentCommand(null, false, 25m, new DateOnly(2025, 8, 1), "Bonifico", null, null, null), "user");

        var covered = await db.CondominiumInstallments.Where(row => row.CondominiumPeriodId == period.Id).OrderBy(row => row.SlotIndex).ToListAsync();
        Assert.Equal(CondominiumInstallmentStatus.Paid, covered[0].Status);
        Assert.Equal(CondominiumInstallmentStatus.Paid, covered[1].Status);
        Assert.Equal(CondominiumInstallmentStatus.PartiallyPaid, covered[2].Status);

        await service.RecordPriorYearBalanceAsync(
            scope, condo.Id, period.Id, new RecordPriorYearBalanceCommand(40m, PriorYearBalanceDisposition.NextInstallment, null, "Conguaglio"), "user");
        var after = await db.CondominiumInstallments.Where(row => row.CondominiumPeriodId == period.Id).OrderBy(row => row.SlotIndex).ToListAsync();
        Assert.Equal(0m, after[0].PriorSettlementAmount);
        Assert.Equal(0m, after[1].PriorSettlementAmount);
        Assert.Equal(40m, after[2].PriorSettlementAmount);
        Assert.Equal(CondominiumInstallmentStatus.Paid, after[0].Status);
        Assert.Equal(40m, after.Sum(row => row.PriorSettlementAmount));
    }

    [Fact]
    public void RentLedgerServices_DoNotQueryCondominiumTables()
    {
        var root = FindRepo();
        foreach (var relative in new[]
        {
            "Casazen.Infrastructure/Services/RentBillingService.cs",
            "Casazen.Infrastructure/Services/RentBillingService.Components.cs",
            "Casazen.Infrastructure/Services/RentBillingService.Term.cs",
            "Casazen.Infrastructure/Services/RentRegisterService.cs",
            "Casazen.Infrastructure/Services/TenantCondominiumSettlementService.cs",
            "Casazen.Infrastructure/Services/TenantExpenseProposalService.cs",
        })
        {
            var text = File.ReadAllText(Path.Combine(root, relative));
            Assert.DoesNotContain("CondominiumPeriods", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CondominiumStatements", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CondominiumPayments", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Condominiums", text, StringComparison.Ordinal);
        }
    }

    private static CondominiumAccountingService Service(AppDbContext db) =>
        new(db, new MigraDocPdfDocumentRenderer(), NullLogger<CondominiumAccountingService>.Instance);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<HostScope> SeedAsync(AppDbContext db)
    {
        var org = new Casazen.Core.Entities.Org { Name = "Demo", Slug = "demo-" + Guid.NewGuid().ToString("N")[..8] };
        db.Orgs.Add(org);
        db.Properties.Add(new Property
        {
            OrgId = org.Id,
            OwnerId = "user",
            Name = "Casa",
            Address = "Via Test 1",
            City = "Milano",
        });
        await db.SaveChangesAsync();
        return new HostScope(org.Id);
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Casazen.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
