using Casazen.Core.Authorization;
using Casazen.Core.Condominium;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Condominium;

public class AdministratorStatementReaderTests
{
    private const string Budget = """
        Preventivo
        Esercizio 2024/2025
        Quota ordinaria 490,30
        Spese straordinarie 600,00
        Riporto
        """;

    [Fact]
    public void ReadsABudgetIntoTheNamedYear_AndDoesNotInventTheRest()
    {
        Assert.True(AdministratorStatementReader.TryRead(Budget, 6, out var proposal, out _));
        Assert.NotNull(proposal);
        Assert.Equal(CondominiumStatementKind.Budget, proposal.Kind);
        Assert.Equal("2024/2025", proposal.Label);
        Assert.Equal(490.30m, proposal.Ordinary);
        Assert.Equal(600m, proposal.Extraordinary);
        Assert.Equal(PriorYearBalanceDisposition.CarryForward, proposal.Settlement);
        Assert.True(proposal.ChargesTenant);
        Assert.Contains("millesimi", proposal.NotExtracted);
        Assert.Contains("pagamenti", proposal.NotExtracted);
        Assert.Contains("piano rate", proposal.NotExtracted);
    }

    [Fact]
    public void TwoAmountsUnderOneLabel_AreNotCharged()
    {
        const string text = """
            Preventivo esercizio 2024/2025
            Quota ordinaria 490,30 1.234,56
            """;
        Assert.True(AdministratorStatementReader.TryRead(text, 6, out var proposal, out _));
        Assert.NotNull(proposal);
        Assert.False(proposal.ChargesTenant);
        Assert.True(proposal.Confidence < AdministratorStatementReader.ChargeConfidence);
    }

    [Fact]
    public void TwoOrdinaryAmounts_AreNotCharged()
    {
        const string text = """
            Preventivo esercizio 2024/2025
            Quota ordinaria 490,30
            Quota ordinaria 1.234,56
            """;
        Assert.True(AdministratorStatementReader.TryRead(text, 6, out var proposal, out _));
        Assert.NotNull(proposal);
        Assert.False(proposal.ChargesTenant);
        Assert.True(proposal.Confidence < AdministratorStatementReader.ChargeConfidence);
    }

    [Fact]
    public void ADateRange_UsesTheCondominiumFiscalYear()
    {
        const string text = """
            Consuntivo
            dal 01/06/2024 al 31/05/2025
            Totale consuntivo 490,30
            """;
        Assert.True(AdministratorStatementReader.TryRead(text, 6, out var proposal, out _));
        Assert.Equal("2024/2025", proposal!.Label);
        Assert.Equal(CondominiumStatementKind.Final, proposal.Kind);
        Assert.Equal(490.30m, proposal.FinalAmount);
    }

    [Fact]
    public void AScanWithNoText_IsNotAProposal()
    {
        Assert.False(AdministratorStatementReader.TryRead("   ", 6, out _, out var error));
        Assert.Equal("no-text", error);
    }

    [Fact]
    public async Task ProposeDoesNotPost_ConfirmChargesOnlyASureOrdinaryLine()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var books = new CondominiumAccountingService(db, new MigraDocPdfDocumentRenderer(), NullLogger<CondominiumAccountingService>.Instance);
        var reader = new AdministratorStatementReaderService(db, books, NullLogger<AdministratorStatementReaderService>.Instance);
        var condo = await books.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa A", null, null, null, 6), "user");
        var pdf = new MigraDocPdfDocumentRenderer().Render(PdfDocumentContent.FromPlainText(
            "Preventivo",
            "Preventivo esercizio 2024/2025\nQuota ordinaria 490,30\nSpese straordinarie 600,00"));

        var proposal = await reader.ProposeAsync(scope, condo.Id, new StatementFileUpload("amministratore.pdf", pdf), "user");
        Assert.False(proposal.Posted);
        Assert.Equal(0, await db.CondominiumStatements.CountAsync());
        Assert.Equal(490.30m, proposal.Ordinary);
        Assert.Equal(600m, proposal.Extraordinary);

        var confirmed = await reader.ConfirmAsync(scope, condo.Id, proposal.Id, "user");
        Assert.True(confirmed.Posted);
        var lines = await db.CondominiumExpenseLines.Where(line => line.StatementId == confirmed.StatementId).ToListAsync();
        var ordinary = lines.Single(line => line.ExpenseNature == CondominiumExpenseNature.Ordinary);
        var extraordinary = lines.Single(line => line.ExpenseNature == CondominiumExpenseNature.Extraordinary);
        Assert.True(ExpenseClassification.ChargesStored(ordinary.ExpenseNature, ordinary.TenantRecoverability, ordinary.ReviewStatus, ordinary.RecoverableAmount, ordinary.Confidence));
        Assert.False(ExpenseClassification.ChargesStored(extraordinary.ExpenseNature, extraordinary.TenantRecoverability, extraordinary.ReviewStatus, extraordinary.RecoverableAmount, extraordinary.Confidence));

        await books.ConfirmBudgetAsync(
            scope, condo.Id, confirmed.PeriodId!.Value, new ConfirmBudgetCommand(100m, "A mano", null, CondominiumInstallmentCadence.Single), "user");
        Assert.Equal(2, await db.CondominiumStatements.CountAsync(statement => statement.Kind == CondominiumStatementKind.Budget));
    }

    [Fact]
    public async Task InstallmentPdfIsOnlyAProposal_AndCustomAmountsPersistExactly()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var books = new CondominiumAccountingService(db, new MigraDocPdfDocumentRenderer(), NullLogger<CondominiumAccountingService>.Instance);
        var reader = new AdministratorStatementReaderService(db, books, NullLogger<AdministratorStatementReaderService>.Instance);
        var condo = await books.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa A", null, null, null, 1), "user");
        var pdf = new MigraDocPdfDocumentRenderer().Render(PdfDocumentContent.FromPlainText("Piano rate", "Rata 1 31/01/2027 800,00\nRata 2 30/06/2027 1.000,00"));
        var proposal = await reader.ReadInstallmentsAsync(scope, condo.Id, new StatementFileUpload("rate.pdf", pdf), "user");
        Assert.Equal(2, proposal.Installments.Count);
        Assert.Empty(await db.CondominiumStatements.ToListAsync());
        var period = await books.CreatePeriodAsync(scope, condo.Id, new CreateCondominiumPeriodCommand("2027"), "user");
        await books.ConfirmBudgetAsync(scope, condo.Id, period.Id,
            new ConfirmBudgetCommand(1800m, null, null, CondominiumInstallmentCadence.Custom,
                OrdinaryNeedsReview: true, CustomInstallments: proposal.Installments), "user");
        var report = await books.GetReportAsync(scope, condo.Id, period.Id);
        Assert.Equal(new[] { 800m, 1000m }, report.Installments.Select(row => row.Ordinary));
        var line = await db.CondominiumExpenseLines.SingleAsync();
        Assert.Equal(ExpenseReviewStatus.Pending, line.ReviewStatus);
    }

    [Fact]
    public async Task InstallmentPdfCannotReadAnotherOrganizationsBook()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var books = new CondominiumAccountingService(db, new MigraDocPdfDocumentRenderer(), NullLogger<CondominiumAccountingService>.Instance);
        var reader = new AdministratorStatementReaderService(db, books, NullLogger<AdministratorStatementReaderService>.Instance);
        var condo = await books.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa A", null, null, null, 1), "user");
        var pdf = new MigraDocPdfDocumentRenderer().Render(PdfDocumentContent.FromPlainText("Piano rate", "Rata 1 31/01/2027 800,00"));
        await Assert.ThrowsAsync<Casazen.Core.Exceptions.NotFoundException>(() => reader.ReadInstallmentsAsync(
            new HostScope(Guid.NewGuid()), condo.Id, new StatementFileUpload("rate.pdf", pdf), "user"));
        Assert.Empty(await db.CondominiumStatements.ToListAsync());
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<HostScope> SeedAsync(AppDbContext db)
    {
        var org = new Casazen.Core.Entities.Org { Name = "Demo", Slug = "demo-" + Guid.NewGuid().ToString("N")[..8] };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return new HostScope(org.Id);
    }
}
