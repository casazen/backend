using Casazen.Core.Authorization;
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
        Assert.Equal(PriorYearBalanceDisposition.CarryForward, proposal.Settlement);
    }

    [Fact]
    public void ImpossibleDates_AreNotAYear()
    {
        const string text = """
            Consuntivo
            dal 31/02/2024 al 00/06/2024
            Totale consuntivo 490,30
            """;
        Assert.False(AdministratorStatementReader.TryRead(text, 6, out _, out var error));
        Assert.Equal("label", error);
    }

    [Fact]
    public void AnImpossibleDate_DoesNotHideTheFiscalYear()
    {
        const string text = """
            Consuntivo
            31/02/2024
            dal 01/06/2024 al 31/05/2025
            Totale consuntivo 490,30
            """;
        Assert.True(AdministratorStatementReader.TryRead(text, 6, out var proposal, out _));
        Assert.Equal("2024/2025", proposal!.Label);
    }

    [Fact]
    public void TwoSettlementWords_StoreNoDisposition()
    {
        const string text = """
            Consuntivo
            Esercizio 2024/2025
            Totale consuntivo 490,30
            Il saldo va in prossima rata oppure a rimborso
            """;
        Assert.True(AdministratorStatementReader.TryRead(text, 6, out var proposal, out _));
        Assert.Null(proposal!.Settlement);
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
    public async Task ConfirmingAConsuntivo_DoesNotChargeTheLumpSum()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var (reader, _) = Reader(db);
        var condo = await NewCondo(db, scope);
        var pdf = Pdf("""
            Consuntivo
            Esercizio 2024/2025
            Spese straordinarie 200,00
            Totale consuntivo 490,30
            Riporto
            """);

        var proposal = await reader.ProposeAsync(scope, condo, new StatementFileUpload("consuntivo.pdf", pdf), "user");
        Assert.Equal(490.30m, proposal.FinalAmount);
        Assert.Null(proposal.Extraordinary);
        var confirmed = await reader.ConfirmAsync(scope, condo, proposal.Id, "user");

        var line = await db.CondominiumExpenseLines.SingleAsync(item => item.StatementId == confirmed.StatementId);
        Assert.Equal(CondominiumExpenseNature.Ordinary, line.ExpenseNature);
        Assert.Equal(TenantRecoverability.OwnerOnly, line.TenantRecoverability);
        Assert.Equal(0m, line.RecoverableAmount);
        Assert.False(ExpenseClassification.ChargesStored(
            line.ExpenseNature, line.TenantRecoverability, line.ReviewStatus, line.RecoverableAmount, line.Confidence));
    }

    [Fact]
    public async Task TheSameFile_StaysOneProposal_AfterItIsConfirmed()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var (reader, _) = Reader(db);
        var condo = await NewCondo(db, scope);
        var pdf = Pdf("Preventivo esercizio 2024/2025\nQuota ordinaria 490,30");

        var first = await reader.ProposeAsync(scope, condo, new StatementFileUpload("amministratore.pdf", pdf), "user");
        var again = await reader.ProposeAsync(scope, condo, new StatementFileUpload("copia.pdf", pdf), "user");
        Assert.Equal(first.Id, again.Id);

        await reader.ConfirmAsync(scope, condo, first.Id, "user");
        var after = await reader.ProposeAsync(scope, condo, new StatementFileUpload("amministratore.pdf", pdf), "user");
        Assert.Equal(first.Id, after.Id);
        Assert.True(after.Posted);
        await reader.ConfirmAsync(scope, condo, after.Id, "user");
        Assert.Equal(1, await db.CondominiumStatements.CountAsync());
    }

    [Fact]
    public async Task AConsuntivoWithNoDisposition_IsNotPosted()
    {
        await using var db = NewDb();
        var scope = await SeedAsync(db);
        var (reader, _) = Reader(db);
        var condo = await NewCondo(db, scope);
        var proposal = new CondominiumStatementProposal
        {
            OrgId = scope.OrgId,
            CondominiumId = condo,
            ContentHash = new string('a', 64),
            Kind = CondominiumStatementKind.Final,
            Label = "2024/2025",
            FinalAmount = 490.30m,
            Settlement = null,
            Confidence = 0.92m,
            ChargesTenant = true,
            Excerpt = "prossima rata · rimborso",
        };
        db.CondominiumStatementProposals.Add(proposal);
        await db.SaveChangesAsync();

        var denied = await Assert.ThrowsAsync<DomainRuleException>(() => reader.ConfirmAsync(scope, condo, proposal.Id, "user"));
        Assert.Equal(CondominiumErrorCodes.FileInvalid, denied.Code);
        Assert.Equal(0, await db.CondominiumStatements.CountAsync());
        Assert.Equal(0, await db.CondominiumPriorBalances.CountAsync());
        Assert.Equal(0, await db.CondominiumPeriods.CountAsync());
        Assert.Equal(CondominiumStatementProposalStatus.Proposed, (await db.CondominiumStatementProposals.SingleAsync()).Status);
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (AdministratorStatementReaderService Reader, CondominiumAccountingService Books) Reader(AppDbContext db)
    {
        var books = new CondominiumAccountingService(db, new MigraDocPdfDocumentRenderer(), NullLogger<CondominiumAccountingService>.Instance);
        return (new AdministratorStatementReaderService(db, books, NullLogger<AdministratorStatementReaderService>.Instance), books);
    }

    private static async Task<Guid> NewCondo(AppDbContext db, HostScope scope)
    {
        var (_, books) = Reader(db);
        var condo = await books.CreateCondominiumAsync(scope, new CreateCondominiumCommand("Casa A", null, null, null, 6), "user");
        return condo.Id;
    }

    private static byte[] Pdf(string text) =>
        new MigraDocPdfDocumentRenderer().Render(PdfDocumentContent.FromPlainText("Amministratore", text));

    private static async Task<HostScope> SeedAsync(AppDbContext db)
    {
        var org = new Casazen.Core.Entities.Org { Name = "Demo", Slug = "demo-" + Guid.NewGuid().ToString("N")[..8] };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return new HostScope(org.Id);
    }
}
