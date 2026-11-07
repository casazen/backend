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
    public void RentLedgerServices_DoNotQueryCondominiumTables()
    {
        var root = FindRepo();
        foreach (var relative in new[]
        {
            "Casazen.Infrastructure/Services/RentBillingService.cs",
            "Casazen.Infrastructure/Services/RentBillingService.Components.cs",
            "Casazen.Infrastructure/Services/RentBillingService.Term.cs",
            "Casazen.Infrastructure/Services/RentRegisterService.cs",
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
