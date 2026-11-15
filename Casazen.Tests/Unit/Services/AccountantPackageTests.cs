using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FiscalDocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E15/F01/US01/T01 (NF-101, NF-87, NF-115, D11): the ZIP of the accountant has a fixed structure with an index that tells the truth about every file, the owner accounting is left out
/// (and said so) for a caller who may not read it, and the agency summary adds up what the ledger and the fiscal documents store, in the ZIP and on the screen alike.
/// </summary>
public class AccountantPackageTests
{
    /// <summary>
    /// PROOF TEST: data rule. The index lists every file of the ZIP and no other: the file exists, its SHA-256 is the one of its bytes and, for a CSV, its rows are the rows of its
    /// tables; the fixed folders are all there, and a full caller has no folder omitted.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_IndexMatchesFilesAndHashes_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();

        var files = Unzip(await rig.BuildAsync(AccountantPackageAccess.Full, AccountantPackagePeriod.OfYear(2026)));

        var index = ParseIndex(files[AccountantPackageStructure.IndexFile]);
        Assert.NotEmpty(index.Entries);

        // The two words of the header that read the same in both languages are written by the code.
        var header = ParseCsv(files[AccountantPackageStructure.IndexFile])[1];
        Assert.Equal(("File", "SHA-256"), (header[0], header[3]));
        foreach (var entry in index.Entries)
        {
            Assert.True(files.ContainsKey(entry.Path), $"{entry.Path} is in the index and not in the ZIP");
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(files[entry.Path])), entry.Sha256);
            if (entry.Path.EndsWith(".pdf", StringComparison.Ordinal) || entry.Path.EndsWith(".xml", StringComparison.Ordinal))
                Assert.Null(entry.Rows);
            else if (entry.Path.EndsWith(".xlsx", StringComparison.Ordinal))
                Assert.NotNull(entry.Rows);
            else if (entry.Rows is { } rows)
                Assert.Equal(RowsOf(files[entry.Path]), rows);
        }

        Assert.Equal(
            files.Keys.Where(k => k != AccountantPackageStructure.IndexFile).Order(StringComparer.Ordinal),
            index.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Empty(index.Omitted);

        // The structure that is always there, and the documents of the period with their files.
        foreach (var fixedFile in new[]
                 {
                     "riepilogo-agenzia/riepilogo-agenzia.csv", "riepilogo-agenzia/riepilogo-agenzia.xlsx", "riepilogo-agenzia/riepilogo-agenzia.pdf",
                     "incassi/incassato.csv", "incassi/saldi-da-incassare.csv", "incassi/costi-dei-canali.csv",
                     "tassa-soggiorno/riepilogo-tassa-soggiorno.csv", "tassa-soggiorno/tassa-soggiorno-svizzera.csv",
                     "rendiconti/elenco-rendiconti.csv", "fatture-proprietari/registro.csv", "ritenute/f24.csv", "ritenute/ritenute-proprietari.csv", "ritenute/cu-2026.csv",
                 })
        {
            Assert.Contains(fixedFile, files.Keys);
        }

        var register = Assert.Single(files.Keys, k => k.StartsWith("documenti-ospiti/registro-", StringComparison.Ordinal) && k.EndsWith(".csv", StringComparison.Ordinal));
        Assert.Equal(3, RowsOfFirstTable(files[register]));
        Assert.Equal(3, files.Keys.Count(k => k.StartsWith("documenti-ospiti/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal)));
        Assert.Single(files.Keys, k => k.StartsWith("fatture-proprietari/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Single(files.Keys, k => k.StartsWith("rendiconti/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal));
    }

    /// <summary>
    /// PROOF TEST: permission and privacy rule. A caller without the permission to read the owner accounting, and a caller that has it but reaches only some properties, get no
    /// owner folder, nothing of the owner accounting is even read, the index lists the omitted folders, the VAT carries no invoice to an owner and the summary has no owner figure.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_WithoutStatementPermission_OmitsOwnerFolders_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        var period = AccountantPackagePeriod.OfYear(2026);

        var withoutPermission = Unzip(await rig.BuildAsync(AccountantPackageAccess.WithoutOwnerAccounting, period));
        var restricted = Unzip(await rig.BuildAsync(AccountantPackageAccess.Full, period, new HostScope(rig.OrgId, GrantedToUserId: "auth0|collaboratore")));

        foreach (var files in new[] { withoutPermission, restricted })
        {
            foreach (var folder in AccountantPackageStructure.OwnerAccountingFolders)
                Assert.DoesNotContain(files.Keys, k => k.StartsWith(folder + "/", StringComparison.Ordinal));

            var index = ParseIndex(files[AccountantPackageStructure.IndexFile]);
            Assert.Equal(AccountantPackageStructure.OwnerAccountingFolders.Order(StringComparer.Ordinal), index.Omitted.Select(r => r.TrimEnd('/')).Order(StringComparer.Ordinal));
            Assert.Equal(files.Keys.Where(k => k != AccountantPackageStructure.IndexFile).Order(StringComparer.Ordinal), index.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        }

        // Nothing of the owner accounting was read for them: no statement file, no owner report.
        rig.StatementFiles.Verify(f => f.GetPdfAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.OwnerReports.Verify(r => r.GetByOwnerAsync(It.IsAny<HostScope>(), It.IsAny<HostPerformanceQuery>(), It.IsAny<CancellationToken>()), Times.Never);

        // The caller of the permission still has the documents of the guests and their VAT. The VAT of the issuer is made of everything it issued, so a caller that reaches the whole org has the
        // invoice to the owner in it too, as figures for each rate (8.1% is the guest's CHF 100.00 and the invoice's CHF 1'310.00) and never its owner or its register; only the portal
        // commissions, which come from the owner ledger, are left out, and the file says so. A caller that reaches some properties has no invoice to an owner (it belongs to no property) and is told.
        Assert.Equal(3, withoutPermission.Keys.Count(k => k.StartsWith("documenti-ospiti/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal)));
        Assert.DoesNotContain(restricted.Keys, k => k.StartsWith("documenti-ospiti/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal));
        var vat = VatCsv(withoutPermission);
        var fullVat = VatCsv(Unzip(await rig.BuildAsync(AccountantPackageAccess.Full, period)));
        var rateRow = vat.Split("\r\n").Single(line => line.StartsWith("2026-Q4;8,10;", StringComparison.Ordinal));
        Assert.Equal("2026-Q4;8,10;1410,00;114,21;CHF;2", rateRow);
        Assert.Contains(rateRow, fullVat, StringComparison.Ordinal);
        Assert.DoesNotContain("Proprietario", vat, StringComparison.Ordinal);
        Assert.Contains("agency.statement.view", vat, StringComparison.Ordinal);
        Assert.DoesNotContain("agency.statement.view", fullVat, StringComparison.Ordinal);
        Assert.DoesNotContain(OwnerInvoicesNotInVat, vat, StringComparison.Ordinal);
        Assert.DoesNotContain(OwnerInvoicesNotInVat, fullVat, StringComparison.Ordinal);
        Assert.Contains(OwnerInvoicesNotInVat, VatCsv(restricted), StringComparison.Ordinal);

        // A collaborator given the lake house gets the documents of the lake house, and still no owner folder: an owner's accounts span properties it does not reach.
        var lake = Unzip(await rig.BuildAsync(AccountantPackageAccess.Full, period, new HostScope(rig.OrgId, GrantedToUserId: "auth0|lago")));
        Assert.Equal(3, lake.Keys.Count(k => k.StartsWith("documenti-ospiti/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal)));
        foreach (var folder in AccountantPackageStructure.OwnerAccountingFolders)
            Assert.DoesNotContain(lake.Keys, k => k.StartsWith(folder + "/", StringComparison.Ordinal));

        var summary = await rig.Summaries.GetAsync(rig.Scope, AccountantPackageAccess.WithoutOwnerAccounting, period);
        Assert.False(summary.OwnerAccountingIncluded);
        Assert.Empty(summary.Commissions);
        Assert.Empty(summary.Payouts);
        Assert.Empty(summary.Wires);
        Assert.Empty(summary.Margin);
        Assert.Empty(summary.OwnerInvoices.Counts);
        Assert.NotEmpty(summary.GuestDocuments);
        Assert.False((await rig.Summaries.GetAsync(new HostScope(rig.OrgId, GrantedToUserId: "auth0|collaboratore"), AccountantPackageAccess.Full, period)).OwnerAccountingIncluded);
    }

    /// <summary>
    /// PROOF TEST: money rule. The summary adds up what is stored: the commission of an owner is the commission lines of its invoice and the ledger commission to the cent, the VAT the
    /// stored VAT of the lines, the documents of the guests are the stored documents (a credit note taken away), the payouts and the wires the rows of the payouts and the batches, the
    /// margin the commissions and recharges less the portal commissions of the account the agency holds, and the files of the ZIP and the JSON are the same figures.
    /// </summary>
    [Fact]
    public async Task AgencySummary_EqualsLedgerAndDocumentTotals_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        var period = AccountantPackagePeriod.OfYear(2026);

        var summary = await rig.Summaries.GetAsync(rig.Scope, AccountantPackageAccess.Full, period);

        // The commission: the ledger of October (-1'000.00 and -200.00), the lines of the invoice and the summary are the same CHF 1'200.00, with the VAT of the invoice (8.1%).
        var ledgerCommission = -await SumAsync(rig, OwnerLedgerEntryType.AgencyCommission);
        var invoice = await rig.Db.FiscalDocuments.Include(d => d.Lines).AsNoTracking().SingleAsync(d => d.Purpose == FiscalDocumentPurpose.OwnerInvoice);
        var invoiceCommission = invoice.Lines.Where(l => l.LineType == FiscalLineType.Commission).ToList();
        var row = Assert.Single(summary.Commissions);
        Assert.Equal((rig.OwnerId, "CHF", 1), (row.OwnerId, row.Currency, row.Documents));
        Assert.Equal(1200.00m, ledgerCommission);
        Assert.Equal(ledgerCommission, invoiceCommission.Sum(l => l.TotalPrice));
        Assert.Equal(ledgerCommission, row.Commission.Taxable);
        Assert.Equal(invoiceCommission.Sum(l => l.VatAmount), row.Commission.Vat);
        Assert.Equal((97.20m, 1297.20m), (row.Commission.Vat, row.Commission.Total));
        Assert.Equal((110.00m, 8.91m, 118.91m), (row.Recharges.Taxable, row.Recharges.Vat, row.Recharges.Total));
        Assert.Equal(40.00m, row.AdvancedExpenses);
        Assert.Equal(invoice.Total, row.InvoicedTotal);

        // The invoices to the owners: one invoice, the stored VAT for each rate.
        Assert.Equal(new AgencyOwnerInvoiceCount("CHF", 1, 0), Assert.Single(summary.OwnerInvoices.Counts));
        Assert.Equal(
            new[] { (8.1m, 1310.00m, 106.11m), (0m, 40.00m, 0m) },
            summary.OwnerInvoices.Rates.Select(r => (r.RatePercent!.Value, r.Amounts.Taxable, r.Amounts.Vat)));

        // The documents of the guests are the stored ones, a credit note negative: two invoices and a credit note of part of the first.
        var stored = await rig.Db.FiscalDocuments.AsNoTracking().Where(d => d.Purpose == FiscalDocumentPurpose.GuestStay).ToListAsync();
        var invoices = summary.GuestDocuments.Single(g => g.Kind == FiscalDocumentKind.Invoice);
        var creditNotes = summary.GuestDocuments.Single(g => g.Kind == FiscalDocumentKind.CreditNote);
        Assert.Equal(2, invoices.Documents);
        Assert.Equal(stored.Where(d => d.Kind == FiscalDocumentKind.Invoice).Sum(d => d.Total), invoices.Amounts.Total);
        Assert.Equal((1240.00m, 49.90m, 1289.90m), (invoices.Amounts.Taxable, invoices.Amounts.Vat, invoices.Amounts.Total));
        Assert.Equal((-200.00m, -7.60m, -207.60m), (creditNotes.Amounts.Taxable, creditNotes.Amounts.Vat, creditNotes.Amounts.Total));
        Assert.Equal(1, creditNotes.Documents);
        Assert.Equal("CHE-109.322.551", invoices.Issuer.TaxId);

        // The Swiss tourist tax is the lines of the documents (CHF 40.00 of the invoice of October), in the canton of the municipality of the property.
        var tax = Assert.Single(summary.TouristTax.Switzerland);
        Assert.Equal(("TI", "CHF", 40.00m), (tax.Canton, tax.Currency, tax.Collected));

        // The payouts and the wires are the rows (the reversed payout is out), the margin the commissions and the recharges less the portal commissions of the account the agency holds.
        Assert.Equal(new AgencyPayoutRow("CHF", 1, 1000.00m, 0m, 1000.00m), Assert.Single(summary.Payouts));
        Assert.Equal(new AgencyWireRow("CHF", WireBatchStatus.Paid, 1, 1, 1000.00m), Assert.Single(summary.Wires));
        Assert.Equal(new AgencyPortalCommissionBorneRow(BookingSource.BookingCom, "CHF", 120.00m), Assert.Single(summary.PortalCommissionsBorne));
        Assert.Equal(new AgencyMarginRow("CHF", 1200.00m, 110.00m, 120.00m, 1190.00m), Assert.Single(summary.Margin));

        // What the services of the reports say is carried as it is, never worked out again.
        Assert.Equal((5000.00m, 3, 10), (summary.Stays.Revenue, summary.Stays.Stays, summary.Stays.OccupiedNights));
        Assert.Equal(1234.50m, summary.Collections.Collected);
        Assert.Equal(300.00m, summary.TouristTax.Italy.Owed);

        // The ZIP carries the very figures of the screen: its summary file is the one made of this summary, and the JSON maps the same record.
        var files = Unzip(await rig.BuildAsync(AccountantPackageAccess.Full, period));
        var texts = new PackageTexts(new ResxDocumentTexts(), PackageTexts.PackageLanguage);
        var expected = CsvWriter.Write(AgencySummaryDocuments.Tables(summary, texts));
        Assert.Equal(expected, files["riepilogo-agenzia/riepilogo-agenzia.csv"]);
        var dto = AgencySummaryDto.Of(summary);
        Assert.Equal((row.Commission.Taxable, row.Commission.Vat, row.InvoicedTotal), (dto.Commissions[0].Commission.Taxable, dto.Commissions[0].Commission.Vat, dto.Commissions[0].InvoicedTotal));
        Assert.Equal((summary.Margin[0].Margin, "creditNote"), (dto.Margin[0].Margin, dto.GuestDocuments.Single(g => g.Amounts.Total < 0m).Kind));
    }

    /// <summary>
    /// PROOF TEST: money rule. A credit note of the invoice to the owner takes its lines away from the commission, the recharges, the invoiced total and the totals for each rate, and is counted
    /// apart from the invoices.
    /// </summary>
    [Fact]
    public async Task AgencySummary_OwnerCreditNote_TakesAwayFromTheCommission_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        await rig.CreditTheOwnerInvoiceAsync();

        var summary = await rig.Summaries.GetAsync(rig.Scope, AccountantPackageAccess.Full, AccountantPackagePeriod.OfYear(2026));

        var row = Assert.Single(summary.Commissions);
        Assert.Equal(2, row.Documents);
        Assert.Equal((AgencyAmounts.Zero, AgencyAmounts.Zero, 0m, 0m), (row.Commission, row.Recharges, row.AdvancedExpenses, row.InvoicedTotal));
        Assert.Equal(new AgencyOwnerInvoiceCount("CHF", 1, 1), Assert.Single(summary.OwnerInvoices.Counts));
        Assert.All(summary.OwnerInvoices.Rates, r => Assert.Equal(AgencyAmounts.Zero, r.Amounts));
        Assert.Equal(new AgencyMarginRow("CHF", 0m, 0m, 120.00m, -120.00m), Assert.Single(summary.Margin));
    }

    /// <summary>
    /// PROOF TEST: money rule. The sales register of an Italian issuer is made of the stored VAT of the documents by month, class and rate: an ordinary invoice, a receipt and, in a flat-rate
    /// regime, the invoices of the regime; a credit note takes away from the class of the document it credits.
    /// </summary>
    [Fact]
    public void AccountantPackage_ItalianSalesRegister_ClassifiesAndNetsTheDocuments_Test()
    {
        var texts = new PackageTexts(new ResxDocumentTexts(), PackageTexts.PackageLanguage);
        var ordinary = new AgencyIssuer("IT|01234567890|TEST", "TEST Agenzia Srl", "IT", "01234567890", DocumentRegime.VatOrdinary);
        var documents = new[]
        {
            ItalianDocument(FiscalDocumentKind.Invoice, new DateOnly(2026, 10, 5), 1000.00m, 22m, 220.00m),
            ItalianDocument(FiscalDocumentKind.Receipt, new DateOnly(2026, 10, 6), 200.00m, 0m, 0m),
            ItalianDocument(FiscalDocumentKind.CreditNote, new DateOnly(2026, 10, 20), 50.00m, 0m, 0m, FiscalDocumentKind.Receipt),
            ItalianDocument(FiscalDocumentKind.CreditNote, new DateOnly(2026, 11, 3), 100.00m, 22m, 22.00m, FiscalDocumentKind.Invoice),
        };

        var register = Assert.Single(AccountantPackageDocuments.Vat(ordinary, documents, [], texts));

        Assert.Equal(
            new[]
            {
                ("2026-10", "Fatture ordinarie", 1000.00m, 220.00m, 1),
                ("2026-10", "Ricevute", 150.00m, 0m, 2),
                ("2026-11", "Fatture ordinarie", -100.00m, -22.00m, 1),
            },
            register.Rows.Select(r => (r[0].TextValue ?? string.Empty, r[1].TextValue ?? string.Empty, r[5].DecimalValue, r[6].DecimalValue, (int)r[8].WholeValue)));

        // In the flat-rate regime the invoices are a class of their own, whatever the nature code.
        var flatRate = new AgencyIssuer("IT|09876543210|TEST", "TEST Ditta Individuale", "IT", "09876543210", DocumentRegime.VatFlatRate);
        var flat = Assert.Single(AccountantPackageDocuments.Vat(flatRate, [ItalianDocument(FiscalDocumentKind.Invoice, new DateOnly(2026, 10, 5), 500.00m, 0m, 0m)], [], texts));
        Assert.Equal(("Fatture in regime forfettario", 500.00m), (flat.Rows[0][1].TextValue, flat.Rows[0][5].DecimalValue));
    }

    /// <summary>
    /// PROOF TEST: money rule. The class of a document in the sales register is the regime it was issued under (its own issuer snapshot): an issuer is one issuer by its country, tax number and
    /// name, so an issuer that moves to the flat-rate regime inside a period has each invoice in the class of its own regime, never all in the class of the first.
    /// </summary>
    [Fact]
    public void AccountantPackage_ItalianSalesRegister_ClassifiesEachInvoiceByTheRegimeItWasIssuedUnder_Test()
    {
        var texts = new PackageTexts(new ResxDocumentTexts(), PackageTexts.PackageLanguage);
        var issuer = new AgencyIssuer("IT|01234567890|TEST Agenzia Srl", "TEST Agenzia Srl", "IT", "01234567890", DocumentRegime.VatOrdinary);
        var documents = new[]
        {
            ItalianDocument(FiscalDocumentKind.Invoice, new DateOnly(2026, 3, 5), 1000.00m, 22m, 220.00m, issuerSnapshot: SnapshotOf(DocumentRegime.VatOrdinary)),
            ItalianDocument(FiscalDocumentKind.Invoice, new DateOnly(2026, 9, 5), 500.00m, 0m, 0m, issuerSnapshot: SnapshotOf(DocumentRegime.VatFlatRate)),
            ItalianDocument(FiscalDocumentKind.Invoice, new DateOnly(2026, 9, 6), 100.00m, 22m, 22.00m, issuerSnapshot: SnapshotOf(DocumentRegime.VatOrdinary)),
        };

        var register = Assert.Single(AccountantPackageDocuments.Vat(issuer, documents, [], texts));

        Assert.Equal(
            new[]
            {
                ("2026-03", "Fatture ordinarie", 1000.00m),
                ("2026-09", "Fatture ordinarie", 100.00m),
                ("2026-09", "Fatture in regime forfettario", 500.00m),
            },
            register.Rows.Select(r => (r[0].TextValue ?? string.Empty, r[1].TextValue ?? string.Empty, r[5].DecimalValue)));
    }

    private static string SnapshotOf(DocumentRegime regime) =>
        new FiscalIssuerSnapshot { LegalName = "TEST Agenzia Srl", CountryCode = "IT", VatNumber = "01234567890", Regime = regime }.ToJson();

    /// <summary>
    /// PROOF TEST: money rule. A document with no lines (one imported from a channel manager) states its own totals and enters the base of the Swiss return as it states them: in its quarter and
    /// rate with the documents that have lines, with the rate «not stated» when it gives none, and outside the VAT when it is at rate zero; it is never dropped from the return while the register
    /// counts it.
    /// </summary>
    [Fact]
    public void AccountantPackage_SwissVat_CountsADocumentWithNoLinesAsItStatesItself_Test()
    {
        var texts = new PackageTexts(new ResxDocumentTexts(), PackageTexts.PackageLanguage);
        var swiss = new AgencyIssuer("CH|CHE-109.322.551|TEST", "TEST Gestioni Lugano Sagl", "CH", "CHE-109.322.551", DocumentRegime.SwissVatRegistered);
        var withLines = ItalianDocument(FiscalDocumentKind.Invoice, new DateOnly(2026, 10, 5), 1000.00m, 8.1m, 81.00m);
        var noLines = ItalianDocument(FiscalDocumentKind.Invoice, new DateOnly(2026, 10, 20), 200.00m, 8.1m, 16.20m) with { Lines = [], VatRatePercent = 8.1m };
        var noRate = ItalianDocument(FiscalDocumentKind.Invoice, new DateOnly(2026, 11, 2), 50.00m, 0m, 0m) with { Lines = [] };
        var exempt = ItalianDocument(FiscalDocumentKind.Invoice, new DateOnly(2026, 11, 3), 300.00m, 0m, 0m) with { Lines = [], VatRatePercent = 0m, VatNatureCode = "N2.2" };

        var tables = AccountantPackageDocuments.Vat(swiss, [withLines, noLines, noRate, exempt], [], texts);

        Assert.Equal(
            new (string Quarter, decimal? Rate, decimal Turnover, decimal Vat, int Documents)[]
            {
                ("2026-Q4", 0.081m, 1200.00m, 97.20m, 2),
                ("2026-Q4", null, 50.00m, 0.00m, 1),
            },
            tables[0].Rows.Select(r => (r[0].TextValue ?? string.Empty, r[1].Kind == ReportCellKind.Percent ? r[1].DecimalValue : (decimal?)null, r[2].DecimalValue, r[3].DecimalValue, (int)r[5].WholeValue)));
        Assert.Equal(texts[PackageLabels.RateUnknown], tables[0].Rows[1][1].TextValue);
        var outside = Assert.Single(tables[1].Rows);
        Assert.Equal(("2026-Q4", 300.00m, "EUR"), (outside[0].TextValue, outside[2].DecimalValue, outside[3].TextValue));
    }

    /// <summary>
    /// PROOF TEST: data rule. The withholding of the package is the Italian evidence that the F24 and the Certificazione Unica of the same folder are built from (AC-NF110-7): a decision of
    /// another jurisdiction, even an applicable one, is never an Italian withholding, and its payout is not counted in the row of the Italian one.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_Withholding_IsTheItalianEvidenceOnly_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        rig.Db.TaxObligationEvidence.AddRange(rig.Evidence("IT", 1000.00m, 210.00m), rig.Evidence("CH", 500.00m, 105.00m));
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();

        var files = Unzip(await rig.BuildAsync(AccountantPackageAccess.Full, AccountantPackagePeriod.OfMonth(2026, 10)));

        var withholding = Text(files["ritenute/ritenute-proprietari.csv"]);
        Assert.Contains("TEST Proprietario;2026-10;1919;1;1000,00;210,00", withholding, StringComparison.Ordinal);
        Assert.DoesNotContain("1500,00", withholding, StringComparison.Ordinal);
        Assert.DoesNotContain("315,00", withholding, StringComparison.Ordinal);
    }

    private static PackageDocument ItalianDocument(
        FiscalDocumentKind kind, DateOnly issued, decimal net, decimal rate, decimal vat, FiscalDocumentKind? credited = null, string? issuerSnapshot = null) =>
        new(
            Guid.NewGuid(),
            FiscalDocumentPurpose.GuestStay,
            kind,
            FiscalDocumentSource.CasaZen,
            "TEST",
            issued,
            "EUR",
            issuerSnapshot,
            "TEST Ospite",
            null,
            null,
            FiscalPaymentChannel.CasaZen,
            net,
            vat,
            0m,
            net + vat,
            null,
            null,
            null,
            null,
            credited,
            false,
            [new PackageDocumentLine(1, FiscalLineType.Accommodation, net, rate, rate == 0m ? "N2.2" : null, vat)]);

    /// <summary>The period is a month <c>yyyy-MM</c> or a year <c>yyyy</c> of 2000 to 2100, never longer than twelve months; anything else is not a period.</summary>
    [Theory]
    [InlineData("2026-10", "2026-10-01", "2026-10-31", "casazen-commercialista-2026-10.zip")]
    [InlineData("2026-02", "2026-02-01", "2026-02-28", "casazen-commercialista-2026-02.zip")]
    [InlineData("2028-02", "2028-02-01", "2028-02-29", "casazen-commercialista-2028-02.zip")]
    [InlineData("2026", "2026-01-01", "2026-12-31", "casazen-commercialista-2026.zip")]
    [InlineData(" 2026-12 ", "2026-12-01", "2026-12-31", "casazen-commercialista-2026-12.zip")]
    public void Period_MonthOrYear_IsTheFirstAndLastDay(string text, string first, string last, string fileName)
    {
        Assert.True(AccountantPackagePeriod.TryParse(text, out var period));

        Assert.Equal((DateOnly.Parse(first), DateOnly.Parse(last), fileName), (period.From, period.To, period.FileName));
        Assert.InRange(period.Months.Count, 1, 12);
        Assert.Equal(period.To.AddDays(1), period.From.AddMonths(period.Months.Count));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("2026-1")]
    [InlineData("26-10")]
    [InlineData("1999")]
    [InlineData("2101")]
    [InlineData("2026-10-01")]
    [InlineData("2026/10")]
    [InlineData("12 months")]
    public void Period_AnythingElse_IsNotAPeriod(string? text)
    {
        Assert.False(AccountantPackagePeriod.TryParse(text, out _));
    }

    /// <summary>
    /// PROOF TEST: data rule. A period with more files of documents than the caps of the archive allow is refused with 422 <c>accountant_package_too_large</c> before the first byte is
    /// written, and a period within them is not.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_BeyondTheCaps_Is422BeforeTheFirstByte_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        using var output = new MemoryStream();

        var tooMany = await Assert.ThrowsAsync<DomainRuleException>(
            () => rig.Package(new DocumentArchiveOptions { MaxExportFiles = 2 }).BuildAsync(rig.Scope, AccountantPackageAccess.Full, AccountantPackagePeriod.OfYear(2026), output));
        Assert.Equal((AccountantPackageErrors.TooLarge, 0L), (tooMany.Code, output.Length));
        Assert.Equal(4, tooMany.Extensions["fileCount"]);

        var tooBig = await Assert.ThrowsAsync<DomainRuleException>(
            () => rig.Package(new DocumentArchiveOptions { MaxExportBytes = DocumentArchiveOptions.LowestMaxExportBytes })
                .BuildAsync(rig.Scope, AccountantPackageAccess.Full, AccountantPackagePeriod.OfYear(2026), output));
        Assert.Equal((AccountantPackageErrors.TooLarge, 0L), (tooBig.Code, output.Length));

        // October holds two documents of the guests (an invoice and a credit note) with their PDF: well within the caps.
        var result = await rig.Package().BuildAsync(rig.Scope, AccountantPackageAccess.Full, AccountantPackagePeriod.OfMonth(2026, 10), output);
        Assert.True(output.Length > 0 && result.Files > 1);
    }

    /// <summary>Every word of the files of the package is in both resource files (Italian and English): a missing key would be printed as its own name in a file the accountant opens.</summary>
    [Fact]
    public void Labels_EveryKey_IsInBothResourceFiles()
    {
        var root = FindRepositoryRoot();
        var texts = new List<Dictionary<string, string?>>();
        foreach (var file in new[] { "SharedResources.resx", "SharedResources.en.resx" })
        {
            var keys = XDocument.Load(Path.Combine(root, "Casazen.Web", "Resources", file)).Root!
                .Elements("data")
                .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value"));
            var missing = PackageLabels.All.Where(k => !keys.TryGetValue(k, out var value) || string.IsNullOrWhiteSpace(value)).ToList();

            Assert.True(missing.Count == 0, $"{file} has no text for: {string.Join(", ", missing)}");
            texts.Add(keys);
        }

        // The localization test of the resource files (SharedResourcesLocalizationTests, run by the CI) refuses a key whose English text is the Italian one: a word that reads the same in both
        // languages is a constant of the code (AccountantPackageDocuments.IndexFileHeader), not a key.
        var same = PackageLabels.All.Where(k => texts[0][k] == texts[1][k]).ToList();
        Assert.True(same.Count == 0, $"The same text in Italian and in English: {string.Join(", ", same)}");

        Assert.True(PackageLabels.All.Count > 100);
        Assert.Equal(PackageLabels.All.Count, PackageLabels.All.Distinct().Count());
    }

    /// <summary>
    /// E15/F01/US02/T02: the package the agency sends every month holds only the folders it chose. A folder that is not chosen is neither read nor written and the index lists it as left out, next to the owner
    /// accounting a caller may not read; what is chosen holds the very files the package with every folder holds, and the owner accounting stays out whatever is chosen when the access does not allow it.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_WithChosenFolders_WritesOnlyThose_AndTheIndexListsTheOthersAsLeftOut()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        var period = AccountantPackagePeriod.OfYear(2026);
        var everything = Unzip(await rig.BuildAsync(AccountantPackageAccess.Full, period));
        var chosen = AccountantPackageFolders.Vat | AccountantPackageFolders.Collections | AccountantPackageFolders.OwnerInvoices;
        rig.StatementFiles.Invocations.Clear();
        rig.OwnerReports.Invocations.Clear();

        var some = Unzip(await rig.BuildAsync(AccountantPackageAccess.Full.Narrowed(chosen), period));

        // Only the chosen folders are there, with the files the package with every folder has in them.
        string FolderOfPath(string path) => path[..path.IndexOf('/', StringComparison.Ordinal)];
        var written = some.Keys.Where(k => k != AccountantPackageStructure.IndexFile).ToList();
        Assert.Equal(["fatture-proprietari", "incassi", "iva"], written.Select(FolderOfPath).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(
            everything.Keys.Where(k => k != AccountantPackageStructure.IndexFile && written.Select(FolderOfPath).Contains(FolderOfPath(k))).Order(StringComparer.Ordinal),
            written.Order(StringComparer.Ordinal));

        // The index tells the truth about them and lists every folder that is not there.
        var index = ParseIndex(some[AccountantPackageStructure.IndexFile]);
        Assert.Equal(written.Order(StringComparer.Ordinal), index.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(
            AccountantPackageFolderRules.Singles.Where(f => (chosen & f) == 0).Select(AccountantPackageFolderRules.FolderOf).Order(StringComparer.Ordinal),
            index.Omitted.Select(r => r.TrimEnd('/')).Order(StringComparer.Ordinal));

        // What was not chosen was not even read: no statement, no owner report (the summary and the statements are not chosen).
        rig.StatementFiles.Verify(f => f.GetPdfAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.OwnerReports.Verify(r => r.GetByOwnerAsync(It.IsAny<HostScope>(), It.IsAny<HostPerformanceQuery>(), It.IsAny<CancellationToken>()), Times.Never);

        // The invoices to the owners are chosen, but the access does not allow the owner accounting: the folder is left out for that, and no folder is listed twice.
        var withoutPermission = Unzip(await rig.BuildAsync(AccountantPackageAccess.WithoutOwnerAccounting.Narrowed(chosen), period));
        Assert.DoesNotContain(withoutPermission.Keys, k => k.StartsWith("fatture-proprietari/", StringComparison.Ordinal));
        var omitted = ParseIndex(withoutPermission[AccountantPackageStructure.IndexFile]).Omitted.Select(r => r.TrimEnd('/')).ToList();
        Assert.Equal(omitted.Distinct().Order(StringComparer.Ordinal), omitted.Order(StringComparer.Ordinal));
        Assert.Equal(
            AccountantPackageStructure.OwnerAccountingFolders.Concat(
                    AccountantPackageFolderRules.Singles.Where(f => (chosen & f) == 0 && !AccountantPackageFolderRules.IsOwnerAccounting(f)).Select(AccountantPackageFolderRules.FolderOf))
                .Order(StringComparer.Ordinal),
            omitted.Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- helpers

    private static Task<decimal> SumAsync(AccountantPackageRig rig, OwnerLedgerEntryType type) =>
        rig.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.EntryType == type).SumAsync(e => e.Amount);

    /// <summary>The title of the table that tells a VAT file does not hold the invoices to the owners (a caller that reaches only some properties).</summary>
    private const string OwnerInvoicesNotInVat = "Fatture emesse ai proprietari non incluse in questo file";

    /// <summary>The VAT file (CSV) of the one issuer of the rig.</summary>
    private static string VatCsv(Dictionary<string, byte[]> files) =>
        Text(files.Single(f => f.Key.StartsWith("iva/iva-", StringComparison.Ordinal) && f.Key.EndsWith(".csv", StringComparison.Ordinal)).Value);

    private static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var content = new MemoryStream();
            using (var stream = entry.Open())
                stream.CopyTo(content);

            files[entry.FullName] = content.ToArray();
        }

        return files;
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF);

    /// <summary>The rows of a CSV of <c>CsvWriter</c>: <c>;</c> between the cells, quoted cells with the quotes doubled, <c>CRLF</c> after each row.</summary>
    private static List<List<string>> ParseCsv(byte[] bytes)
    {
        var text = Text(bytes);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ';':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    cell.Append(c);
                    break;
            }
        }

        return rows;
    }

    /// <summary>The data rows of the tables of a CSV: what is under each title row and header, the empty rows between the tables left out.</summary>
    private static int RowsOf(byte[] bytes) => Blocks(bytes).Sum(block => block.Count - 2);

    private static int RowsOfFirstTable(byte[] bytes) => Blocks(bytes)[0].Count - 2;

    private static List<List<List<string>>> Blocks(byte[] bytes)
    {
        var blocks = new List<List<List<string>>>();
        var current = new List<List<string>>();
        foreach (var row in ParseCsv(bytes))
        {
            if (row.Count == 1 && row[0].Length == 0)
            {
                blocks.Add(current);
                current = [];
            }
            else
            {
                current.Add(row);
            }
        }

        if (current.Count > 0)
            blocks.Add(current);

        return blocks;
    }

    private sealed record IndexEntry(string Path, string Description, int? Rows, string Sha256);

    private sealed record Index(List<IndexEntry> Entries, List<string> Omitted);

    /// <summary>The index of the package: the files of its first table and the folders of its second.</summary>
    private static Index ParseIndex(byte[] bytes)
    {
        var blocks = Blocks(bytes);
        var entries = blocks[0].Skip(2).Select(r => new IndexEntry(r[0], r[1], r[2].Length == 0 ? null : int.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture), r[3])).ToList();
        var omitted = blocks.Count > 1 ? blocks[1].Skip(2).Select(r => r[0]).ToList() : [];
        return new Index(entries, omitted);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}

/// <summary>
/// A Swiss agency of Lugano (the rig of the owner invoices) with, for 2026: an owner invoice issued in November, a statement published in September, two invoices and a credit note
/// to guests (one with CHF 40.00 of tourist tax in the canton of Ticino), a confirmed payout and a reversed one, a paid wire batch, and the portal account of one listing held by the
/// agency. The services of the reports are stand-ins that answer fixed figures; everything else is the real code over one in-memory database.
/// </summary>
internal sealed class AccountantPackageRig : IAsyncDisposable
{
    private readonly OwnerInvoiceRig _owners;
    private readonly Mock<IHostDashboardService> _dashboard = new();
    private readonly Mock<IHostCollectionsReportService> _collections = new();
    private readonly Mock<IChannelCostsReportService> _channelCosts = new();
    private readonly Mock<ITouristTaxSummaryService> _touristTax = new();
    private readonly Mock<IStatementExportService> _f24 = new();

    private AccountantPackageRig(OwnerInvoiceRig owners)
    {
        _owners = owners;
        var period = HostReportRules.PeriodOf(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        _dashboard
            .Setup(d => d.GetPerformanceAsync(It.IsAny<HostScope>(), It.IsAny<HostPerformanceQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostPerformanceReport(period, null, [], new HostPerformanceRow("total", null, "total", new HostPerformanceFigures(10, 100, 0, 5000.00m, 3, 12), null)));
        _collections
            .Setup(c => c.GetCollectionsAsync(It.IsAny<HostScope>(), It.IsAny<HostReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostCollectionsReport(
                period,
                new HostDashboardCollected(1234.50m, 4),
                [],
                [],
                [],
                new HostCredits(new HostStripeCredits(false, [], 0, 0), new HostChannelCredits(false, [], 0))));
        _collections
            .Setup(c => c.GetBalancesToCollectAsync(It.IsAny<HostScope>(), It.IsAny<HostBalancesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostBalancesToCollect(new DateOnly(2026, 11, 12), [], [new HostBalanceTotal("CHF", 80.00m, 1)]));
        _channelCosts
            .Setup(c => c.GetChannelCostsAsync(It.IsAny<HostScope>(), It.IsAny<HostReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChannelCostsReport(period, [], new ChannelCostRow(null, null, 0, 0m, null, null, null, null, 0, false)));
        _touristTax
            .Setup(t => t.GetSummaryAsync(It.IsAny<HostScope>(), It.IsAny<FiscalReportPeriod>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TouristTaxSummary(new FiscalReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)), [], new TouristTaxSummaryTotals(1, 3, 2, 300.00m, 0, 0m, 300.00m)));
        OwnerReports
            .Setup(r => r.GetByOwnerAsync(It.IsAny<HostScope>(), It.IsAny<HostPerformanceQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OwnerPerformanceReport(period, [], new OwnerPerformanceRow(null, null, false, 0, OwnerPerformanceFigures.Zero), OwnerPerformanceFigures.Zero));
        StatementFiles
            .Setup(f => f.GetPdfAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OwnerStatementFile(Encoding.ASCII.GetBytes("%PDF-1.4 statement"), "rendiconto.pdf", "application/pdf"));
        StatementFiles
            .Setup(f => f.GetCsvAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OwnerStatementFile(Encoding.UTF8.GetBytes("a;b\r\n"), "rendiconto.csv", "text/csv"));
        Summaries = new AgencySummaryService(
            Db, _dashboard.Object, _collections.Object, _channelCosts.Object, _touristTax.Object, OwnerReports.Object, new PortalAccountHolderResolver(Db), owners.Clock);
    }

    public AppDbContext Db => _owners.Db;

    public Guid OrgId => _owners.OrgId;

    public Guid OwnerId => _owners.OwnerId;

    public HostScope Scope => new(OrgId);

    public AgencySummaryService Summaries { get; }

    public Mock<IOwnerStatementDocuments> StatementFiles { get; } = new();

    public Mock<IOwnerReportService> OwnerReports { get; } = new();

    public static async Task<AccountantPackageRig> CreateAsync()
    {
        var rig = new AccountantPackageRig(await OwnerInvoiceRig.SwissAsync());
        await rig.SeedAsync();
        return rig;
    }

    /// <summary>The package service over the rig, with the caps given (the defaults otherwise).</summary>
    public AccountantPackageService Package(DocumentArchiveOptions? limits = null) =>
        new(
            Db,
            _owners.Documents,
            StatementFiles.Object,
            _f24.Object,
            Summaries,
            _collections.Object,
            _channelCosts.Object,
            _touristTax.Object,
            new PortalAccountHolderResolver(Db),
            new MigraDocPdfDocumentRenderer(),
            new ResxDocumentTexts(),
            Options.Create(limits ?? new DocumentArchiveOptions()),
            NullLogger<AccountantPackageService>.Instance);

    /// <summary>The ZIP of <paramref name="period"/> for the caller of <paramref name="scope"/> (the whole org by default).</summary>
    public async Task<byte[]> BuildAsync(AccountantPackageAccess access, AccountantPackagePeriod period, HostScope? scope = null)
    {
        using var output = new MemoryStream();
        await Package().BuildAsync(scope ?? Scope, access, period, output);
        return output.ToArray();
    }

    /// <summary>A credit note of the whole invoice of the agency to the owner, issued at once in the series of the invoice.</summary>
    public async Task CreditTheOwnerInvoiceAsync()
    {
        var invoice = await Db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Purpose == FiscalDocumentPurpose.OwnerInvoice && d.Kind == FiscalDocumentKind.Invoice);
        await _owners.Invoices.CreateCreditNoteAsync(OrgId, invoice.Id, null, "TEST storno", confirm: true, null);
    }

    public ValueTask DisposeAsync() => _owners.DisposeAsync();

    private async Task SeedAsync()
    {
        var db = Db;

        // November: the owner invoice of October (the commissions 1'000.00 and 200.00, the recharges, an expense) is issued; its VAT is 8.1%.
        var version = await _owners.VersionAsync(_owners.StandardMonth());
        var draft = await _owners.Invoices.CreateDraftAsync(OrgId, version.Id, null);
        await _owners.Invoices.IssueAsync(OrgId, draft.Id, confirm: true, null);

        // September: a statement published to the owner.
        db.OwnerStatementVersions.Add(new OwnerStatementVersion
        {
            OrgId = OrgId,
            PropertyOwnerId = OwnerId,
            PeriodYear = 2026,
            PeriodMonth = 9,
            VersionNumber = 1,
            Status = OwnerStatementVersionStatus.Published,
            Currency = "CHF",
            CutoffAt = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc),
            Revenue = 4000.00m,
            Commission = -800.00m,
            ClosingBalance = 3200.00m,
            EntriesSha256 = new string('a', 64),
            PublishedAt = new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc),
            PublishedSha256 = new string('b', 64),
            CreatedAt = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc),
        });

        // The municipality of the lake house is in Ticino.
        db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = "5192", CantonCode = "TI", OfficialName = "TEST Lugano" });
        var lake = await db.Properties.SingleAsync(p => p.Id == _owners.LakeHouse);
        lake.Country = "CH";
        lake.SwissMunicipalityCode = "5192";

        // The documents to the guests: two invoices (the first with CHF 40.00 of tourist tax outside the VAT) and a credit note of part of the first.
        var snapshot = FiscalIssuerSnapshot.From(
            await db.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgId),
            await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == OrgId)).ToJson();
        var first = GuestDocument(FiscalDocumentKind.Invoice, 1, new DateOnly(2026, 10, 20), snapshot, Line(1, FiscalLineType.Accommodation, 1000.00m, 3.8m, 38.00m), Line(2, FiscalLineType.Cleaning, 100.00m, 3.8m, 3.80m), Line(3, FiscalLineType.TouristTax, 40.00m, 0m, 0m));
        var credit = GuestDocument(FiscalDocumentKind.CreditNote, 2, new DateOnly(2026, 10, 25), snapshot, Line(1, FiscalLineType.Accommodation, 200.00m, 3.8m, 7.60m));
        credit.CreditedDocumentId = first.Id;
        var second = GuestDocument(FiscalDocumentKind.Invoice, 3, new DateOnly(2026, 11, 2), snapshot, Line(1, FiscalLineType.ExtraService, 100.00m, 8.1m, 8.10m));
        db.FiscalDocuments.AddRange(first, credit, second);

        // A confirmed payout and a reversed one, a paid batch of the confirmed one; the account of Booking.com on the lake house is held by the agency (V2).
        var payout = Payout("A", OwnerPayoutStatus.Confirmed);
        db.OwnerPayouts.AddRange(payout, Payout("B", OwnerPayoutStatus.Reversed));
        var batch = new WireBatch
        {
            OrgId = OrgId,
            Currency = "CHF",
            DebitAccount = "CH5800791123000889012",
            TotalAmount = 1000.00m,
            TransferCount = 1,
            Status = WireBatchStatus.Paid,
            CreatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
        };
        db.WireBatches.Add(batch);

        // A collaborator «Solo alcuni» was given the mountain house only, another the lake house only.
        db.PropertyMemberAccesses.AddRange(
            new PropertyMemberAccess { OrgId = OrgId, UserId = "auth0|collaboratore", PropertyId = _owners.MountainHouse },
            new PropertyMemberAccess { OrgId = OrgId, UserId = "auth0|lago", PropertyId = _owners.LakeHouse });
        db.PropertyChannelAccountHolders.Add(new PropertyChannelAccountHolder
        {
            OrgId = OrgId,
            PropertyId = _owners.LakeHouse,
            Channel = BookingSource.BookingCom.ToString(),
            Holder = PortalAccountHolder.Agency,
            UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private OwnerPayout Payout(string key, OwnerPayoutStatus status) => new()
    {
        OrgId = OrgId,
        PropertyOwnerId = OwnerId,
        OwnerStatementVersionId = Guid.NewGuid(),
        IdempotencyKey = $"payout-{key}-{Guid.NewGuid():N}",
        PaidOn = new DateOnly(2026, 10, 31),
        Currency = "CHF",
        GrossAmount = 1000.00m,
        WithholdingAmount = 0m,
        NetAmount = 1000.00m,
        Status = status,
        ConfirmedAt = new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>The withholding decision of a payout of October, applicable, under the rule of <paramref name="jurisdiction"/> (<c>IT</c>, <c>CH</c>): <paramref name="amount"/> of <paramref name="gross"/>, tax code 1919.</summary>
    public TaxObligationEvidence Evidence(string jurisdiction, decimal gross, decimal amount) => new()
    {
        OrgId = OrgId,
        OwnerPayoutId = Guid.NewGuid(),
        PropertyOwnerId = OwnerId,
        OwnerStatementVersionId = Guid.NewGuid(),
        IdempotencyKey = $"payout:{Guid.NewGuid():N}",
        Decision = TaxDecision.Applicable,
        ReasonCode = "applicable",
        Jurisdiction = jurisdiction,
        RuleCode = $"TEST-{jurisdiction}",
        RuleVersion = "1",
        GrossBase = gross,
        RatePercent = 21m,
        Rounding = "half_up",
        Amount = amount,
        TaxCode = "1919",
        PaidOn = new DateOnly(2026, 10, 31),
        PeriodYear = 2026,
        PeriodMonth = 10,
        CreatedAt = new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc),
    };

    private static FiscalDocumentLine Line(int number, FiscalLineType type, decimal net, decimal rate, decimal vat) => new()
    {
        LineNumber = number,
        LineType = type,
        Description = $"TEST {type}",
        Quantity = 1m,
        UnitPrice = net,
        TotalPrice = net,
        VatRatePercent = rate,
        VatAmount = vat,
    };

    /// <summary>An issued Swiss document of the lake house to a guest: the amounts are the sums of its lines.</summary>
    private FiscalDocument GuestDocument(FiscalDocumentKind kind, int sequence, DateOnly issued, string issuerSnapshot, params FiscalDocumentLine[] lines)
    {
        var document = new FiscalDocument
        {
            OrgId = OrgId,
            BookingId = _owners.DirectStay,
            PropertyId = _owners.LakeHouse,
            Purpose = FiscalDocumentPurpose.GuestStay,
            Source = FiscalDocumentSource.CasaZen,
            Kind = kind,
            Status = FiscalDocumentStatus.Issued,
            Series = "FT2026",
            SequenceNumber = sequence,
            Number = $"FT2026/{sequence}",
            IssueDate = issued,
            Currency = "CHF",
            IssuerSnapshot = issuerSnapshot,
            RuleVersion = "ch-vat-2024.1",
            RecipientKind = InvoiceRecipientKind.Guest,
            RecipientName = "TEST Ospite",
            RecipientAddressStreet = "Via dei Test",
            RecipientAddressCivic = "3",
            RecipientPostalCode = "6900",
            RecipientCity = "Lugano",
            RecipientCountryCode = "CH",
            PaymentChannel = FiscalPaymentChannel.CasaZen,
            PaymentState = FiscalDocumentPaymentState.Paid,
            SdiStatus = FiscalDocumentSdiStatuses.NotApplicable,
            ServicePeriodFrom = new DateOnly(2026, 10, 17),
            ServicePeriodTo = new DateOnly(2026, 10, 20),
            CreatedAt = issued.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            UpdatedAt = issued.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        };
        foreach (var line in lines)
        {
            line.OrgId = OrgId;
            line.DocumentId = document.Id;
            document.Lines.Add(line);
        }

        document.TaxableBase = lines.Sum(l => l.TotalPrice);
        document.VatAmount = lines.Sum(l => l.VatAmount ?? 0m);
        document.Total = document.TaxableBase + document.VatAmount;
        return document;
    }
}

/// <summary>
/// What <c>AddCasazenServices</c> registers for the accountant package (E15/F01/US01/T01): the package and the agency summary, each once and scoped (they read the org's database for
/// one request), and nothing else of the package: the services have no flag of their own, the endpoints are behind <c>Features:AccountantPackage</c>.
/// </summary>
public class AccountantPackageRegistrationTests
{
    [Fact]
    public void AddCasazenServices_RegistersTheAccountantPackageAndTheAgencySummary_OnceAndScoped()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        Casazen.Web.Extensions.ServiceCollectionExtensions.AddCasazenServices(services);

        foreach (var (service, implementation) in new[]
                 {
                     (typeof(IAccountantPackageService), typeof(AccountantPackageService)),
                     (typeof(IAgencySummaryService), typeof(AgencySummaryService)),
                 })
        {
            var registered = Assert.Single(services, d => d.ServiceType == service);
            Assert.Equal(implementation, registered.ImplementationType);
            Assert.Equal(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped, registered.Lifetime);
        }
    }
}
