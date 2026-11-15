using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Infrastructure;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.OwnerPortal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FiscalDocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E17/F02/US01/T01 (NF-116, acceptance steps C8 and D2): the documents CasaZen issued in the name of an owner, in the owner portal and in the ZIP for the owner's accountant.
/// Only the issuer profile of the owner, never the agency's own documents nor another owner's; only the owner of the claim, or a contact with <c>accounting.read</c>; the register
/// that adds up to the documents; the annual summary with the invoices of the agency and their recharges; the index with the SHA-256 of every file. The owner portal has no org, so
/// every read here runs on a context whose tenant filter matches nothing.
/// </summary>
public class OwnerIssuedDocumentsTests
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>
    /// Privacy: the owner sees the documents issued on the issuer profile of its own owner profiles and nothing else: not the agency's own documents, not another owner's, not a
    /// draft, a discarded one, a proforma or an imported one, not an invoice of the agency to the owner. A document of another owner, a draft and one that does not exist are the same
    /// 404, and nothing of a guest but the name the document prints (no contact, no fiscal code, no address) reaches the answer.
    /// </summary>
    [Fact]
    public async Task OwnerIssuedDocuments_OnlyOwnIssuerProfile_Test()
    {
        var world = await World.CreateAsync();

        // Both agencies the owner has ownerships with, the newest first; the credit note sits between the invoices it follows.
        var mine = await world.Portal.GetOwnerIssuedDocumentsAsync(world.OwnerUserA);
        Assert.Equal(new[] { world.A4, world.A3, world.A2, world.A1, world.X1, world.A0 }, mine.Select(d => d.DocumentId));
        var first = mine.Single(d => d.DocumentId == world.A1);
        Assert.Equal(
            (world.OrgId, "TEST Agenzia", "SRL2026/1", new DateOnly(2026, 10, 14), FiscalDocumentKind.Invoice, 110m, "EUR", FiscalDocumentSdiStatuses.ToSend, true),
            (first.AgencyId, first.AgencyName, first.Number, first.IssueDate, first.Kind, first.Total, first.Currency, first.SdiStatus, first.HasXml));
        Assert.Equal("TEST Altra Agenzia", mine.Single(d => d.DocumentId == world.X1).AgencyName);
        Assert.Equal(FiscalDocumentKind.CreditNote, mine.Single(d => d.DocumentId == world.A2).Kind);

        // The month narrows the list.
        var october = await world.Portal.GetOwnerIssuedDocumentsAsync(world.OwnerUserA, AccountantPackagePeriod.OfMonth(2026, 10));
        Assert.Equal(new[] { world.A2, world.A1, world.X1 }, october.Select(d => d.DocumentId));

        // The other owner reads its own receipts and none of ours.
        Assert.Equal(new[] { world.B2, world.B1 }, (await world.Portal.GetOwnerIssuedDocumentsAsync(world.OwnerUserB)).Select(d => d.DocumentId));

        // Anything that is not an issued document of the owner is the same 404, for the PDF and for the XML.
        foreach (var id in new[] { world.B1, world.AgencyServices, world.Draft, world.Discarded, world.Proforma, world.Imported, world.AgencyInvoice, Guid.NewGuid() })
        {
            var pdf = await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerIssuedDocumentPdfAsync(world.OwnerUserA, id));
            var xml = await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerIssuedDocumentXmlAsync(world.OwnerUserA, id));
            Assert.Equal((OwnerIssuedDocumentsErrors.NotFound, OwnerIssuedDocumentsErrors.NotFound), (pdf.Code, xml.Code));
        }

        // A person with no ownership in force has nothing to see: an empty list, not an error.
        Assert.Empty(await world.Portal.GetOwnerIssuedDocumentsAsync(Guid.NewGuid()));

        // The file is the one of the document: a PDF, and the XML as it was stored; a receipt has no XML.
        var pdfFile = await world.Portal.OpenOwnerIssuedDocumentPdfAsync(world.OwnerUserA, world.A1);
        await using (pdfFile.Content)
        {
            Assert.Equal(("application/pdf", "fattura-SRL2026-1.pdf"), (pdfFile.ContentType, pdfFile.FileName));
            Assert.Equal("%PDF", Encoding.ASCII.GetString(await ReadAsync(pdfFile.Content), 0, 4));
        }

        var xmlFile = await world.Portal.OpenOwnerIssuedDocumentXmlAsync(world.OwnerUserA, world.A1);
        await using (xmlFile.Content)
        {
            Assert.Equal(("application/xml", "fattura-SRL2026-1.xml"), (xmlFile.ContentType, xmlFile.FileName));
            Assert.Equal(World.XmlOf("A1"), Encoding.UTF8.GetString(await ReadAsync(xmlFile.Content)));
        }

        var noXml = await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerIssuedDocumentXmlAsync(world.OwnerUserB, world.B1));
        Assert.Equal(FiscalDocumentErrors.XmlUnavailable, noXml.Code);

        // Nothing of a guest is in the list: not its name, a contact, a fiscal code, an address or a PEC (the PDF and the XML hold what the document says).
        var json = JsonSerializer.Serialize(mine.Select(OwnerPortalIssuedDocumentDto.From));
        foreach (var secret in new[] { "TEST Ospite", "ospite.riservato@example.com", "+41790000000", "RSSMRA80A01H501U", "Via Privata 1", "ospite@pec.example.com" })
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);

        // The words of the two answers are in the resource files, in both languages (the parity test of the package reads the keys of its labels).
        Assert.Equal(
            (PackageLabels.OwnerIssuedDocumentNotFoundMessage, PackageLabels.OwnerIssuedDocumentsNotAvailableMessage),
            (OwnerIssuedDocumentsErrors.NotFoundMessageKey, OwnerIssuedDocumentsErrors.NotAvailableMessageKey));
    }

    /// <summary>
    /// Permission: a delegated contact the owner did not give <c>accounting.read</c> is refused (403) on the list, the PDF, the XML and the ZIP; with the permission it reads what the
    /// owner it is a contact of reads (not the other owner's, not the owner's other agency); a withdrawn contact has nothing. With <c>Features:AgencyTier</c> or
    /// <c>Features:FiscalDocumentIssuance</c> off the list stays empty and a file is not there, whoever asks.
    /// </summary>
    [Fact]
    public async Task OwnerIssuedDocuments_ContactNeedsAccountingRead_AndTheFlagsDecide_Test()
    {
        var world = await World.CreateAsync();
        await using var zip = new MemoryStream();

        foreach (var ask in new Func<Task>[]
                 {
                     () => world.Portal.GetOwnerIssuedDocumentsAsync(world.ContactWithoutAccounting),
                     () => world.Portal.OpenOwnerIssuedDocumentPdfAsync(world.ContactWithoutAccounting, world.A1),
                     () => world.Portal.OpenOwnerIssuedDocumentXmlAsync(world.ContactWithoutAccounting, world.A1),
                     () => world.Portal.ExportOwnerIssuedDocumentsAsync(world.ContactWithoutAccounting, AccountantPackagePeriod.OfYear(2026), zip),
                 })
        {
            var refusal = await Assert.ThrowsAsync<DomainForbiddenException>(ask);
            Assert.Equal(OwnerPortalErrors.PermissionMissing, refusal.Code);
        }

        Assert.Equal(0, zip.Length);

        // With the permission the contact reads the documents of the owner that gave it, in the agency of the delegation only.
        Assert.Equal(new[] { world.A4, world.A3, world.A2, world.A1, world.A0 }, (await world.Portal.GetOwnerIssuedDocumentsAsync(world.ContactWithAccounting)).Select(d => d.DocumentId));
        await using (var file = (await world.Portal.OpenOwnerIssuedDocumentPdfAsync(world.ContactWithAccounting, world.A1)).Content)
            Assert.Equal("%PDF", Encoding.ASCII.GetString(await ReadAsync(file), 0, 4));

        await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerIssuedDocumentPdfAsync(world.ContactWithAccounting, world.B1));
        await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerIssuedDocumentPdfAsync(world.ContactWithAccounting, world.X1));

        // A withdrawn contact has nothing, and there is nothing to export.
        Assert.Empty(await world.Portal.GetOwnerIssuedDocumentsAsync(world.RevokedContact));
        var nothing = await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.ExportOwnerIssuedDocumentsAsync(world.RevokedContact, AccountantPackagePeriod.OfYear(2026), zip));
        Assert.Equal(OwnerIssuedDocumentsErrors.NotAvailable, nothing.Code);

        // Either flag off: the list is empty (not a 403 for the contact without the permission either: the flag decides first) and no file is there.
        foreach (var flag in new[] { FeatureFlags.AgencyTier, FeatureFlags.FiscalDocumentIssuance })
        {
            world.Flags.Set(flag, false);
            Assert.Empty(await world.Portal.GetOwnerIssuedDocumentsAsync(world.OwnerUserA));
            Assert.Empty(await world.Portal.GetOwnerIssuedDocumentsAsync(world.ContactWithoutAccounting));
            await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerIssuedDocumentPdfAsync(world.OwnerUserA, world.A1));
            await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerIssuedDocumentXmlAsync(world.OwnerUserA, world.A1));
            var off = await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.ExportOwnerIssuedDocumentsAsync(world.OwnerUserA, AccountantPackagePeriod.OfYear(2026), zip));
            Assert.Equal(OwnerIssuedDocumentsErrors.NotAvailable, off.Code);
            world.Flags.Set(flag, true);
        }

        Assert.NotEmpty(await world.Portal.GetOwnerIssuedDocumentsAsync(world.OwnerUserA));
        Assert.Equal(0, zip.Length);
    }

    /// <summary>
    /// Money: the register of the export adds up to the documents it lists. For a year of a company owner (invoices with VAT, a credit note, an operation without VAT) and of a private
    /// owner (receipts with the stamp duty): every row is the document's stored amounts, a credit note is negative, the totals of the period for each currency are the sums of the rows,
    /// the totals for each rate and nature add up to the same total, and the stamp duty is its own column. The month has the documents of the month.
    /// </summary>
    [Fact]
    public async Task OwnerIssuedDocumentsExport_RegisterTotalsEqualDocuments_Test()
    {
        var world = await World.CreateAsync();

        // The company owner, the year 2026: A1 and A3 and A4 are invoices, A2 credits part of A1.
        var company = await world.ExportAsync(world.Owner(world.OwnerA), AccountantPackagePeriod.OfYear(2026));
        var register = Csv(company[RegisterOf(company)]);
        var rows = TableRows(register, "Documenti emessi agli ospiti");
        Assert.Equal(new[] { "SRL2026/1", "SRL2026/2", "SRL2026/3", "SRL2026/4" }, rows.Select(r => r[1]));

        // Every row is what its document stores (taxable, VAT, stamp duty, total, currency); the credit note is negative.
        Assert.Equal(new[] { 100m, 10m, 0m, 110m }, Amounts(rows[0].Skip(5).Take(4)));
        Assert.Equal(new[] { -20m, -2m, 0m, -22m }, Amounts(rows[1].Skip(5).Take(4)));
        Assert.Equal(new[] { 200m, 20m, 0m, 220m }, Amounts(rows[2].Skip(5).Take(4)));
        Assert.Equal(new[] { 108m, 10m, 0m, 118m }, Amounts(rows[3].Skip(5).Take(4)));
        Assert.All(rows, r => Assert.Equal("EUR", r[9]));

        // The totals of the period are the sums of the rows, and so are the totals for each rate and nature.
        var total = Assert.Single(TableRows(register, "Totale del periodo per valuta"));
        Assert.Equal(("EUR", "4"), (total[0], total[1]));
        Assert.Equal(new[] { 388m, 38m, 0m, 426m }, Amounts(total.Skip(2)));
        foreach (var column in new[] { 5, 6, 7, 8 })
            Assert.Equal(rows.Sum(r => Amount(r[column])), Amount(total[column - 3]));

        var rates = TableRows(register, "Totali per aliquota");
        Assert.Equal(2, rates.Count);
        Assert.Equal(("EUR", 10m, string.Empty, "4"), (rates[0][0], Amount(rates[0][1]), rates[0][2], rates[0][6]));
        Assert.Equal(new[] { 380m, 38m, 418m }, Amounts(rates[0].Skip(3).Take(3)));
        Assert.Equal(("EUR", 0m, "N1", "1"), (rates[1][0], Amount(rates[1][1]), rates[1][2], rates[1][6]));
        Assert.Equal(new[] { 8m, 0m, 8m }, Amounts(rates[1].Skip(3).Take(3)));
        Assert.Equal(Amount(total[5]), rates.Sum(r => Amount(r[5])));

        // The private owner: the stamp duty is its own column, and it is not part of the total when the issuer bears it.
        var private1 = await world.ExportAsync(world.Owner(world.OwnerB), AccountantPackagePeriod.OfYear(2026));
        var receipts = Csv(private1[RegisterOf(private1)]);
        Assert.Equal(new[] { "2026/1", "2026/2" }, TableRows(receipts, "Documenti emessi agli ospiti").Select(r => r[1]));
        var receiptTotal = Assert.Single(TableRows(receipts, "Totale del periodo per valuta"));
        Assert.Equal(("EUR", "2"), (receiptTotal[0], receiptTotal[1]));
        Assert.Equal(new[] { 360m, 0m, 2m, 360m }, Amounts(receiptTotal.Skip(2)));

        // The month: only the documents issued in it, and no summary of the year.
        var october = await world.ExportAsync(world.Owner(world.OwnerA), AccountantPackagePeriod.OfMonth(2026, 10));
        var octoberRegister = Csv(october[RegisterOf(october)]);
        Assert.Equal(new[] { "SRL2026/1", "SRL2026/2" }, TableRows(octoberRegister, "Documenti emessi agli ospiti").Select(r => r[1]));
        var octoberTotal = Assert.Single(TableRows(octoberRegister, "Totale del periodo per valuta"));
        Assert.Equal(("EUR", "2"), (octoberTotal[0], octoberTotal[1]));
        Assert.Equal(new[] { 80m, 8m, 0m, 88m }, Amounts(octoberTotal.Skip(2)));

        // The Excel file carries the same rows as the CSV.
        var index = Index(company);
        var csvPath = RegisterOf(company);
        Assert.Equal(index.Single(r => r[0] == csvPath)[2], index.Single(r => r[0] == csvPath[..^4] + ".xlsx")[2]);
    }

    /// <summary>
    /// Data: a year holds the documents, their summary by month and the invoices of the agency to the owner with their recharges (the commission, the recharges with their VAT, the
    /// expenses advanced; credit notes negative), and none of another owner's; a month holds the documents only. The index lists every file and no other, with the SHA-256 of its
    /// bytes, the files are named with the day of the issue in the form of the archive, and the PDF and the XML are the ones of the documents.
    /// </summary>
    [Fact]
    public async Task OwnerIssuedDocumentsExport_YearAddsTheSummaryAndTheAgencyInvoices_AMonthDoesNot_Test()
    {
        var world = await World.CreateAsync();

        var year = await world.ExportAsync(world.Owner(world.OwnerA), AccountantPackagePeriod.OfYear(2026));

        // The index is made of the files of the ZIP and nothing else, each with the SHA-256 of its bytes; the documents have their PDF, and their XML when they have one.
        var index = Index(year);
        Assert.Equal(year.Keys.Where(k => k != "indice.csv").Order(StringComparer.Ordinal), index.Select(r => r[0]).Order(StringComparer.Ordinal));
        foreach (var row in index)
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(year[row[0]])), row[3]);

        foreach (var (stem, xml) in new[] { ("2026-10-14-SRL2026-1", "A1"), ("2026-10-20-SRL2026-2", "A2"), ("2026-11-05-SRL2026-3", "A3"), ("2026-12-01-SRL2026-4", "A4") })
        {
            Assert.Equal("%PDF", Encoding.ASCII.GetString(year[$"documenti-emessi/{stem}.pdf"], 0, 4));
            Assert.Equal(World.XmlOf(xml), Encoding.UTF8.GetString(year[$"documenti-emessi/{stem}.xml"]));
        }

        Assert.DoesNotContain(year.Keys, k => k.Contains("SRL2025", StringComparison.Ordinal));
        Assert.DoesNotContain(year.Keys, k => k.StartsWith("documenti-emessi/2026-11-03", StringComparison.Ordinal));

        // The summary of the documents: for each month and kind, then the VAT of the year for each rate.
        var summary = Csv(year["riepilogo-annuale/documenti-emessi.csv"]);
        var months = TableRows(summary, "Riepilogo annuale dei documenti emessi");
        Assert.Equal(new[] { "2026-10", "2026-10", "2026-11", "2026-12" }, months.Select(r => r[0]));
        Assert.All(months, r => Assert.Equal(("EUR", "1"), (r[2], r[3])));
        Assert.Equal(
            new[] { 100m, 10m, 0m, 110m, -20m, -2m, 0m, -22m, 200m, 20m, 0m, 220m, 108m, 10m, 0m, 118m },
            months.SelectMany(r => Amounts(r.Skip(4))));
        var yearTotal = Assert.Single(TableRows(summary, "Totale del periodo per valuta"));
        Assert.Equal(("EUR", "4"), (yearTotal[0], yearTotal[1]));
        Assert.Equal(new[] { 388m, 38m, 0m, 426m }, Amounts(yearTotal.Skip(2)));

        // The invoices of the agency to the owner: their register and files (the other owner's invoice is not there), and the commission, the recharges and the expenses.
        var invoices = Csv(year["fatture-agenzia/registro.csv"]);
        Assert.Equal(new[] { "PM2026/1", "PM2026/2" }, TableRows(invoices, "Fatture emesse ai proprietari").Select(r => r[1]));
        Assert.Equal(World.AgencyInvoicePdf(world.AgencyInvoice), year["fatture-agenzia/2026-10-31-PM2026-1.pdf"]);
        Assert.Equal(World.XmlOf("PM1"), Encoding.UTF8.GetString(year["fatture-agenzia/2026-10-31-PM2026-1.xml"]));
        Assert.DoesNotContain(year.Keys, k => k.Contains("PM2026-3", StringComparison.Ordinal));

        var recharges = Csv(year["riepilogo-annuale/fatture-agenzia-e-riaddebiti.csv"]);
        var byCurrency = Assert.Single(TableRows(recharges, "Fatture dell'agenzia al proprietario e riaddebiti"));
        Assert.Equal(("EUR", "2"), (byCurrency[0], byCurrency[1]));
        Assert.Equal(new[] { 100m, 22m, 122m, 40m, 8.8m, 48.8m, 30m, 200.8m }, Amounts(byCurrency.Skip(2)));
        var detail = TableRows(recharges, "Fatture dell'agenzia al proprietario: provvigione");
        Assert.Equal(new[] { "PM2026/1", "PM2026/2" }, detail.Select(r => r[0]));
        Assert.Equal(new[] { "31/10/2026", "30/11/2026" }, detail.Select(r => r[1]));
        Assert.Equal(new[] { 100m, 50m, 11m, 30m, 213m }, Amounts(detail[0].Skip(4)));
        Assert.Equal(new[] { 0m, -10m, -2.2m, 0m, -12.2m }, Amounts(detail[1].Skip(4)));

        // A month has the documents of the month and no summary, no invoice of the agency.
        var month = await world.ExportAsync(world.Owner(world.OwnerA), AccountantPackagePeriod.OfMonth(2026, 11));
        Assert.Equal(
            new[] { "documenti-emessi/2026-11-05-SRL2026-3.pdf", "documenti-emessi/2026-11-05-SRL2026-3.xml" },
            month.Keys.Where(k => k.EndsWith(".pdf", StringComparison.Ordinal) || k.EndsWith(".xml", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(month.Keys, k => k.StartsWith("riepilogo-annuale/", StringComparison.Ordinal) || k.StartsWith("fatture-agenzia/", StringComparison.Ordinal));
        Assert.Equal(month.Keys.Where(k => k != "indice.csv").Order(StringComparer.Ordinal), Index(month).Select(r => r[0]).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Data and permission: a person who reads the accounting of more than one owner profile gets one folder for each, with its own register and none of the other's documents; an
    /// empty list of owners is nothing (never every owner's); an owner of another org is the 404 of an owner that does not exist; and a period with more files than the caps allow is
    /// refused before the first byte is written.
    /// </summary>
    [Fact]
    public async Task OwnerIssuedDocumentsExport_SeveralOwnersAreOneFolderEach_AndNeverEveryOwner_Test()
    {
        var world = await World.CreateAsync();
        await using var stream = new MemoryStream();
        var result = await world.Portal.ExportOwnerIssuedDocumentsAsync(world.OwnerUserA, AccountantPackagePeriod.OfYear(2026), stream);
        var files = Unzip(stream);

        // The owner of the agency of the lower id is the first folder; each folder has the register of its owner only.
        var folders = files.Keys.Where(k => k.StartsWith("proprietario-", StringComparison.Ordinal)).Select(k => k[..k.IndexOf('/', StringComparison.Ordinal)]).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, folders.Count);
        Assert.StartsWith("proprietario-1-it-01234567897", folders[0], StringComparison.Ordinal);
        Assert.StartsWith("proprietario-2-it-", folders[1], StringComparison.Ordinal);
        var firstRegister = Csv(files[files.Keys.Single(k => k.StartsWith($"{folders[0]}/documenti-emessi/registro-", StringComparison.Ordinal) && k.EndsWith(".csv", StringComparison.Ordinal))]);
        var secondRegister = Csv(files[files.Keys.Single(k => k.StartsWith($"{folders[1]}/documenti-emessi/registro-", StringComparison.Ordinal) && k.EndsWith(".csv", StringComparison.Ordinal))]);
        Assert.Equal(new[] { "SRL2026/1", "SRL2026/2", "SRL2026/3", "SRL2026/4" }, TableRows(firstRegister, "Documenti emessi agli ospiti").Select(r => r[1]));
        Assert.Equal(new[] { "2026/1" }, TableRows(secondRegister, "Documenti emessi agli ospiti").Select(r => r[1]));
        Assert.Equal(files.Keys.Where(k => k != "indice.csv").Order(StringComparer.Ordinal), Index(files).Select(r => r[0]).Order(StringComparer.Ordinal));
        Assert.Equal(files.Count, result.Files);

        // Nobody: an empty list is not «every owner».
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => world.Export.BuildAsync([], AccountantPackagePeriod.OfYear(2026), new MemoryStream()));
        Assert.Empty(await world.Export.ListAsync([], null, 10));
        Assert.Null(await world.Export.GetPdfAsync([], world.A1, null));

        // An owner of another org.
        var other = await Assert.ThrowsAsync<NotFoundException>(() => world.Export.BuildAsync([new OwnerIssuedDocumentsOwner(world.OrgId, world.OwnerA2)], AccountantPackagePeriod.OfYear(2026), new MemoryStream()));
        Assert.Equal(OwnerFiscalErrors.OwnerNotFound, other.Code);

        // The caps: four documents are more than one file, and nothing is written.
        var tight = World.ExportOf(world, new DocumentArchiveOptions { MaxExportFiles = 1 });
        await using var refused = new MemoryStream();
        var tooLarge = await Assert.ThrowsAsync<DomainRuleException>(() => tight.BuildAsync([world.Owner(world.OwnerA)], AccountantPackagePeriod.OfYear(2026), refused));
        Assert.Equal(AccountantPackageErrors.TooLarge, tooLarge.Code);
        Assert.Equal(0, refused.Length);
    }

    /// <summary>
    /// Money: the PDF an owner gets says what the agency's PDF says, and that includes whether the document was paid. The payment state is read now from the payments of the booking,
    /// naming the org (the portal has no tenant): a document stored as «to collect» that a card payment settled is printed as paid, not as «to collect».
    /// </summary>
    [Fact]
    public async Task OwnerIssuedDocuments_PdfOfAPaidDocumentSaysPaid_AlsoWithoutATenant_Test()
    {
        var world = await World.CreateAsync();

        // What the portal could not see before: with its own tenant filter the payments of the booking are nowhere, naming the org they are found.
        Assert.Equal(0m, (await FiscalDocumentService.CollectedByBookingAsync(world.Reader, [world.BookingOfA4], CancellationToken.None)).GetValueOrDefault(world.BookingOfA4));
        Assert.Equal(118m, (await FiscalDocumentService.CollectedByBookingAsync(world.Reader, [world.BookingOfA4], CancellationToken.None, world.OrgId)).GetValueOrDefault(world.BookingOfA4));

        var file = await world.Portal.OpenOwnerIssuedDocumentPdfAsync(world.OwnerUserA, world.A4);
        await file.Content.DisposeAsync();

        var texts = new ResxDocumentTexts();
        var paid = texts.Get("FiscalDocPdfPayment", "it", texts.Get("FiscalDocPdfPaymentPaid", "it"));
        var toCollect = texts.Get("FiscalDocPdfPayment", "it", texts.Get("FiscalDocPdfPaymentToCollect", "it"));
        var said = world.Renderer.Rendered.Last().Blocks.OfType<PdfParagraph>().Select(p => p.Text).ToList();
        Assert.Contains(paid, said);
        Assert.DoesNotContain(toCollect, said);
    }

    private sealed class World
    {
        private static readonly DateTime Cutoff = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
        private readonly EphemeralDataProtectionProvider _protection = new();
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly Mock<IOwnerStatementDocuments> _statements = new();

        private World()
        {
            _options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerIssuedDocuments_{Guid.NewGuid()}").Options;
            Seed = new AppDbContext(_options, new SeedTenant(), _protection);
            Reader = new AppDbContext(_options, new FailClosedTenant(), _protection);
            Flags = new TestFeatureFlags(FeatureFlags.AgencyTier, FeatureFlags.FiscalDocumentIssuance);
            Renderer = new CapturingPdfRenderer(new MigraDocPdfDocumentRenderer());
            _statements
                .Setup(s => s.GetOwnerInvoicePdfAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((Guid org, Guid id, string? language, CancellationToken token) => Task.FromResult<OwnerStatementFile?>(new OwnerStatementFile(AgencyInvoicePdf(id), "fattura.pdf", "application/pdf")));
            Export = ExportOf(this, new DocumentArchiveOptions());
            var clock = new FakeTimeProvider(new DateTimeOffset(Cutoff));
            Portal = new OwnerPortalService(Reader, clock, null, Flags, new OwnerPortalAccessResolver(Reader, clock), null, Export);
        }

        public AppDbContext Seed { get; }

        /// <summary>A context with the tenant filter of the owner portal: it has no org, so the filter matches nothing.</summary>
        public AppDbContext Reader { get; }

        public TestFeatureFlags Flags { get; }

        public CapturingPdfRenderer Renderer { get; }

        public OwnerIssuedDocumentsExport Export { get; }

        public OwnerPortalService Portal { get; }

        public Guid OrgId { get; } = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

        public Guid OtherOrgId { get; } = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

        public Guid OwnerA { get; } = Guid.NewGuid();

        public Guid OwnerB { get; } = Guid.NewGuid();

        public Guid OwnerA2 { get; } = Guid.NewGuid();

        public Guid OwnerUserA { get; } = Guid.NewGuid();

        public Guid OwnerUserB { get; } = Guid.NewGuid();

        public Guid ContactWithoutAccounting { get; } = Guid.NewGuid();

        public Guid ContactWithAccounting { get; } = Guid.NewGuid();

        public Guid RevokedContact { get; } = Guid.NewGuid();

        public Guid BookingOfA4 { get; } = Guid.NewGuid();

        // The documents of the owner A (a company): the invoices A0 (2025), A1, A3, A4, and the credit note A2 of A1; X1 is the receipt of its other agency.
        public Guid A0 { get; } = Guid.NewGuid();

        public Guid A1 { get; } = Guid.NewGuid();

        public Guid A2 { get; } = Guid.NewGuid();

        public Guid A3 { get; } = Guid.NewGuid();

        public Guid A4 { get; } = Guid.NewGuid();

        public Guid X1 { get; } = Guid.NewGuid();

        // The receipts of the owner B (a private person).
        public Guid B1 { get; } = Guid.NewGuid();

        public Guid B2 { get; } = Guid.NewGuid();

        // What is not a document issued in the name of an owner.
        public Guid AgencyServices { get; } = Guid.NewGuid();

        public Guid Draft { get; } = Guid.NewGuid();

        public Guid Discarded { get; } = Guid.NewGuid();

        public Guid Proforma { get; } = Guid.NewGuid();

        public Guid Imported { get; } = Guid.NewGuid();

        public Guid AgencyInvoice { get; } = Guid.NewGuid();

        public Guid AgencyCreditNote { get; } = Guid.NewGuid();

        public Guid AgencyInvoiceOfB { get; } = Guid.NewGuid();

        private Guid ProfileOrg { get; } = Guid.NewGuid();

        private Guid ProfileA { get; } = Guid.NewGuid();

        private Guid ProfileB { get; } = Guid.NewGuid();

        private Guid ProfileA2 { get; } = Guid.NewGuid();

        public static string XmlOf(string name) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><p:FatturaElettronica>TEST {name}</p:FatturaElettronica>";

        /// <summary>What the agency's invoice PDF is in this world: the files of the statements are not under test here.</summary>
        public static byte[] AgencyInvoicePdf(Guid id) => Encoding.ASCII.GetBytes($"%PDF-1.4 TEST agency invoice {id:N}");

        public static OwnerIssuedDocumentsExport ExportOf(World world, DocumentArchiveOptions limits) =>
            new(world.Reader, world._statements.Object, world.Renderer, new ResxDocumentTexts(), Options.Create(limits), NullLogger<OwnerIssuedDocumentsExport>.Instance);

        public OwnerIssuedDocumentsOwner Owner(Guid owner) => new(owner == OwnerA2 ? OtherOrgId : OrgId, owner);

        public static async Task<World> CreateAsync()
        {
            var world = new World();
            await world.SeedAsync();
            return world;
        }

        /// <summary>The ZIP for one owner, made as the agency asks for it, read back.</summary>
        public async Task<Dictionary<string, byte[]>> ExportAsync(OwnerIssuedDocumentsOwner owner, AccountantPackagePeriod period)
        {
            await using var stream = new MemoryStream();
            await Export.BuildAsync([owner], period, stream);
            return Unzip(stream);
        }

        private async Task SeedAsync()
        {
            Seed.Orgs.Add(new OrgEntity { Id = OrgId, Name = "TEST Agenzia", DisplayName = "TEST Agenzia", Slug = "test-agenzia", PlanTier = PlanTier.Agency, DefaultLanguage = "it" });
            Seed.Orgs.Add(new OrgEntity { Id = OtherOrgId, Name = "TEST Altra Agenzia", DisplayName = "TEST Altra Agenzia", Slug = "test-altra-agenzia", PlanTier = PlanTier.Agency, DefaultLanguage = "it" });
            Seed.PropertyOwners.Add(new PropertyOwner { Id = OwnerA, OrgId = OrgId, FirstName = "TEST", LastName = "Lago", LegalName = "TEST Immobiliare Lago Srl", Email = "lago@example.com" });
            Seed.PropertyOwners.Add(new PropertyOwner { Id = OwnerB, OrgId = OrgId, FirstName = "Mario", LastName = "Rossi", Email = "mario.rossi@example.com" });
            Seed.PropertyOwners.Add(new PropertyOwner { Id = OwnerA2, OrgId = OtherOrgId, FirstName = "TEST", LastName = "Lago", LegalName = "TEST Immobiliare Lago Srl", Email = "lago2@example.com" });

            var propertyA = Guid.NewGuid();
            var propertyB = Guid.NewGuid();
            var propertyA2 = Guid.NewGuid();
            Seed.Properties.Add(new Property { Id = propertyA, OrgId = OrgId, OwnerId = "auth0|agency", Name = "TEST Casa Lago", Address = "Via 1", City = "Como", Currency = "EUR" });
            Seed.Properties.Add(new Property { Id = propertyB, OrgId = OrgId, OwnerId = "auth0|agency", Name = "TEST Casa Rossi", Address = "Via 2", City = "Como", Currency = "EUR" });
            Seed.Properties.Add(new Property { Id = propertyA2, OrgId = OtherOrgId, OwnerId = "auth0|agency2", Name = "TEST Villa", Address = "Via 3", City = "Lecco", Currency = "EUR" });

            foreach (var user in new[] { OwnerUserA, OwnerUserB, ContactWithoutAccounting, ContactWithAccounting, RevokedContact })
                Seed.OwnerUsers.Add(new OwnerUser { Id = user, Auth0Sub = $"auth0|{user:N}", Email = $"{user:N}@example.com", FirstName = "TEST", LastName = "Persona" });

            Seed.PropertyOwnerships.Add(Ownership(OrgId, propertyA, OwnerA, OwnerUserA));
            Seed.PropertyOwnerships.Add(Ownership(OrgId, propertyB, OwnerB, OwnerUserB));
            Seed.PropertyOwnerships.Add(Ownership(OtherOrgId, propertyA2, OwnerA2, OwnerUserA));
            Seed.OwnerPortalContacts.Add(Contact(ContactWithoutAccounting, OwnerPortalPermission.DocumentsRead | OwnerPortalPermission.CalendarRead, OwnerPortalContactStatus.Active));
            Seed.OwnerPortalContacts.Add(Contact(ContactWithAccounting, OwnerPortalPermission.AccountingRead, OwnerPortalContactStatus.Active));
            Seed.OwnerPortalContacts.Add(Contact(RevokedContact, OwnerPortalPermission.AccountingRead, OwnerPortalContactStatus.Revoked));

            Seed.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile { Id = ProfileOrg, OrgId = OrgId, DocumentRegime = DocumentRegime.VatOrdinary, LegalName = "TEST Agenzia Srl" });
            Seed.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
            {
                Id = ProfileA, OrgId = OrgId, PropertyOwnerId = OwnerA, DocumentRegime = DocumentRegime.VatOrdinary, LegalName = "TEST Immobiliare Lago Srl", FiscalCode = "01234567897", VatNumber = "01234567897", GuestDocumentSeries = "SRL{year}",
            });
            Seed.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
            {
                Id = ProfileB, OrgId = OrgId, PropertyOwnerId = OwnerB, DocumentRegime = DocumentRegime.PrivatePerson, LegalName = "TEST Mario Rossi", FiscalCode = "RSSMRA70A01H501S",
            });
            Seed.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
            {
                Id = ProfileA2, OrgId = OtherOrgId, PropertyOwnerId = OwnerA2, DocumentRegime = DocumentRegime.PrivatePerson, LegalName = "TEST Lago Privato", FiscalCode = "RSSMRA70A01H501S",
            });

            // A guest with contacts, and the booking and the payment of A4: stored as «to collect», settled by a card payment.
            var guest = new Guest { OrgId = OrgId, FirstName = "Ospite", LastName = "Riservato", Email = "ospite.riservato@example.com", PhoneNumber = "+41790000000" };
            Seed.Guests.Add(guest);
            Seed.Bookings.Add(new Booking
            {
                Id = BookingOfA4,
                OrgId = OrgId,
                PropertyId = propertyA,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 28, 14, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc),
                Source = BookingSource.Direct,
                TotalPrice = 118m,
                Status = BookingStatus.Confirmed,
            });
            Seed.Payments.Add(new Payment { OrgId = OrgId, BookingId = BookingOfA4, Amount = 118m, Status = PaymentStatus.Completed, TransactionId = "TEST-PAY-1" });

            // The company's invoices and its credit note, in its own series; the receipts of the private owner; a receipt of the other agency.
            var a1 = Document(A1, OrgId, ProfileA, FiscalDocumentKind.Invoice, "SRL2026", 1, new DateOnly(2026, 10, 14), 100m, 10m, 0m, 110m, "A1", "TEST Ospite Uno", Line(FiscalLineType.Accommodation, 100m, 10m, 10m));
            var a2 = Document(A2, OrgId, ProfileA, FiscalDocumentKind.CreditNote, "SRL2026", 2, new DateOnly(2026, 10, 20), 20m, 2m, 0m, 22m, "A2", "TEST Ospite Uno", Line(FiscalLineType.Accommodation, 20m, 10m, 2m));
            a2.CreditedDocumentId = A1;
            var a3 = Document(A3, OrgId, ProfileA, FiscalDocumentKind.Invoice, "SRL2026", 3, new DateOnly(2026, 11, 5), 200m, 20m, 0m, 220m, "A3", "TEST Ospite Due", Line(FiscalLineType.Accommodation, 150m, 10m, 15m), Line(FiscalLineType.Cleaning, 50m, 10m, 5m));
            var a4 = Document(A4, OrgId, ProfileA, FiscalDocumentKind.Invoice, "SRL2026", 4, new DateOnly(2026, 12, 1), 108m, 10m, 0m, 118m, "A4", "TEST Ospite Tre", Line(FiscalLineType.Accommodation, 100m, 10m, 10m), Line(FiscalLineType.TouristTax, 8m, 0m, 0m, "N1"));
            a4.BookingId = BookingOfA4;
            a4.PaymentChannel = FiscalPaymentChannel.CasaZen;
            a4.PaymentState = FiscalDocumentPaymentState.ToCollect;
            var a0 = Document(A0, OrgId, ProfileA, FiscalDocumentKind.Invoice, "SRL2025", 9, new DateOnly(2025, 12, 30), 50m, 5m, 0m, 55m, "A0", "TEST Ospite Uno", Line(FiscalLineType.Accommodation, 50m, 10m, 5m));
            var b1 = Document(B1, OrgId, ProfileB, FiscalDocumentKind.Receipt, "2026", 1, new DateOnly(2026, 11, 3), 300m, 0m, 2m, 300m, null, "TEST Ospite Uno", Line(FiscalLineType.Accommodation, 300m, 0m, 0m));
            var b2 = Document(B2, OrgId, ProfileB, FiscalDocumentKind.Receipt, "2026", 2, new DateOnly(2026, 11, 10), 60m, 0m, 0m, 60m, null, "TEST Ospite Due", Line(FiscalLineType.Accommodation, 60m, 0m, 0m));
            var x1 = Document(X1, OtherOrgId, ProfileA2, FiscalDocumentKind.Receipt, "2026", 1, new DateOnly(2026, 10, 5), 50m, 0m, 0m, 50m, null, "TEST Ospite Uno", Line(FiscalLineType.Accommodation, 50m, 0m, 0m));

            // What is not issued in the name of an owner: the agency's own document of its services, a draft, a discarded one, a proforma, one imported from a channel manager.
            var services = Document(AgencyServices, OrgId, ProfileOrg, FiscalDocumentKind.Invoice, "2026", 9, new DateOnly(2026, 11, 12), 40m, 8.8m, 0m, 48.8m, "AS", "TEST Ospite Uno", Line(FiscalLineType.Cleaning, 40m, 22m, 8.8m));
            services.ForAgencyServices = true;
            var draft = Document(Draft, OrgId, ProfileA, FiscalDocumentKind.Invoice, "SRL2026", 0, new DateOnly(2026, 11, 14), 10m, 1m, 0m, 11m, "D", "TEST Ospite Uno", Line(FiscalLineType.Accommodation, 10m, 10m, 1m));
            draft.Status = FiscalDocumentStatus.Draft;
            draft.Number = null;
            draft.SequenceNumber = null;
            draft.IssueDate = null;
            var discarded = Document(Discarded, OrgId, ProfileA, FiscalDocumentKind.Invoice, "SRL2026", 0, new DateOnly(2026, 11, 14), 10m, 1m, 0m, 11m, "X", "TEST Ospite Uno", Line(FiscalLineType.Accommodation, 10m, 10m, 1m));
            discarded.Status = FiscalDocumentStatus.Discarded;
            discarded.Number = null;
            discarded.SequenceNumber = null;
            discarded.IssueDate = null;
            var proforma = Document(Proforma, OrgId, ProfileA, FiscalDocumentKind.Proforma, "PF", 1, new DateOnly(2026, 11, 14), 10m, 1m, 0m, 11m, "P", "TEST Ospite Uno", Line(FiscalLineType.Accommodation, 10m, 10m, 1m));
            var imported = Document(Imported, OrgId, ProfileA, FiscalDocumentKind.Invoice, "OCT", 1, new DateOnly(2026, 11, 14), 10m, 1m, 0m, 11m, "I", "TEST Ospite Uno", Line(FiscalLineType.Accommodation, 10m, 10m, 1m));
            imported.Source = FiscalDocumentSource.Octorate;

            // The invoices of the agency to the owner A (an invoice and a credit note of 10 of the recharge), and one to the other owner.
            var agencyInvoice = OwnerInvoice(AgencyInvoice, OwnerA, FiscalDocumentKind.Invoice, "PM2026", 1, new DateOnly(2026, 10, 31), 180m, 33m, 213m, "PM1", Line(FiscalLineType.Commission, 100m, 22m, 22m), Line(FiscalLineType.Recharge, 50m, 22m, 11m), Line(FiscalLineType.AdvancedExpense, 30m, 0m, 0m, "N1"));
            var agencyCredit = OwnerInvoice(AgencyCreditNote, OwnerA, FiscalDocumentKind.CreditNote, "PM2026", 2, new DateOnly(2026, 11, 30), 10m, 2.2m, 12.2m, "PM2", Line(FiscalLineType.Recharge, 10m, 22m, 2.2m));
            agencyCredit.CreditedDocumentId = AgencyInvoice;
            var agencyOfB = OwnerInvoice(AgencyInvoiceOfB, OwnerB, FiscalDocumentKind.Invoice, "PM2026", 3, new DateOnly(2026, 10, 31), 100m, 22m, 122m, "PMB", Line(FiscalLineType.Commission, 100m, 22m, 22m));

            Seed.FiscalDocuments.AddRange(a0, a1, a2, a3, a4, b1, b2, x1, services, draft, discarded, proforma, imported, agencyInvoice, agencyCredit, agencyOfB);
            await Seed.SaveChangesAsync();
            Seed.ChangeTracker.Clear();
        }

        private PropertyOwnership Ownership(Guid org, Guid property, Guid owner, Guid ownerUser) => new()
        {
            OrgId = org,
            PropertyId = property,
            PropertyOwnerId = owner,
            OwnerUserId = ownerUser,
            SharePercent = 100m,
            OwnershipStart = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        private OwnerPortalContact Contact(Guid ownerUser, OwnerPortalPermission permissions, OwnerPortalContactStatus status) => new()
        {
            OrgId = OrgId,
            PropertyOwnerId = OwnerA,
            Email = $"{ownerUser:N}@example.com",
            FirstName = "TEST",
            LastName = "Contatto",
            EmailHash = new string('a', 64),
            TokenHash = new string('b', 64),
            Permissions = permissions,
            Status = status,
            OwnerUserId = ownerUser,
            InvitedByOwnerUserId = OwnerUserA,
            InvitationExpiresAt = Cutoff.AddDays(30),
            CreatedAt = Cutoff,
            UpdatedAt = Cutoff,
        };

        private static FiscalDocumentLine Line(FiscalLineType type, decimal net, decimal rate, decimal vat, string? nature = null) => new()
        {
            LineType = type,
            Description = "TEST riga",
            Quantity = 1m,
            UnitPrice = net,
            TotalPrice = net,
            VatRatePercent = rate,
            VatAmount = vat,
            VatNatureCode = nature,
        };

        /// <summary>An issued document of a stay issued on <paramref name="profile"/>, with its recipient (a guest with contacts, a fiscal code and an address that must never reach the portal list), its issuer snapshot and its XML when it has one.</summary>
        private FiscalDocument Document(
            Guid id,
            Guid org,
            Guid profile,
            FiscalDocumentKind kind,
            string series,
            int sequence,
            DateOnly day,
            decimal taxable,
            decimal vat,
            decimal stamp,
            decimal total,
            string? xml,
            string recipient,
            params FiscalDocumentLine[] lines)
        {
            var document = new FiscalDocument
            {
                Id = id,
                OrgId = org,
                IssuerProfileId = profile,
                Purpose = FiscalDocumentPurpose.GuestStay,
                Source = FiscalDocumentSource.CasaZen,
                Kind = kind,
                Status = FiscalDocumentStatus.Issued,
                Series = series,
                SequenceNumber = sequence,
                Number = $"{series}/{sequence}",
                IssueDate = day,
                Currency = "EUR",
                TaxableBase = taxable,
                VatAmount = vat,
                StampDuty = stamp,
                Total = total,
                RecipientKind = InvoiceRecipientKind.Guest,
                RecipientName = recipient,
                RecipientFiscalCode = "RSSMRA80A01H501U",
                RecipientAddressStreet = "Via Privata 1",
                RecipientPec = "ospite@pec.example.com",
                PaymentChannel = FiscalPaymentChannel.Portal,
                PaymentState = FiscalDocumentPaymentState.Paid,
                SdiStatus = kind == FiscalDocumentKind.Receipt ? FiscalDocumentSdiStatuses.NotApplicable : FiscalDocumentSdiStatuses.ToSend,
                FatturaPaXml = xml is null ? null : XmlOf(xml),
                IssuerSnapshot = SnapshotOf(profile).ToJson(),
            };
            Attach(document, lines, org);
            return document;
        }

        private FiscalDocument OwnerInvoice(Guid id, Guid owner, FiscalDocumentKind kind, string series, int sequence, DateOnly day, decimal taxable, decimal vat, decimal total, string xml, params FiscalDocumentLine[] lines)
        {
            var document = Document(id, OrgId, ProfileOrg, kind, series, sequence, day, taxable, vat, 0m, total, xml, "TEST Proprietario", lines);
            document.Purpose = FiscalDocumentPurpose.OwnerInvoice;
            document.RecipientOwnerId = owner;
            document.PeriodYear = day.Year;
            document.PeriodMonth = day.Month;
            return document;
        }

        /// <summary>The issuer as its profile says it: the same name and tax id as the profile, so the documents of one owner are the register of one issuer.</summary>
        private FiscalIssuerSnapshot SnapshotOf(Guid profile)
        {
            if (profile == ProfileA)
                return new FiscalIssuerSnapshot { LegalName = "TEST Immobiliare Lago Srl", CountryCode = "IT", Regime = DocumentRegime.VatOrdinary, VatNumber = "01234567897", FiscalCode = "01234567897" };

            if (profile == ProfileB)
                return new FiscalIssuerSnapshot { LegalName = "TEST Mario Rossi", CountryCode = "IT", Regime = DocumentRegime.PrivatePerson, FiscalCode = "RSSMRA70A01H501S" };

            if (profile == ProfileA2)
                return new FiscalIssuerSnapshot { LegalName = "TEST Lago Privato", CountryCode = "IT", Regime = DocumentRegime.PrivatePerson, FiscalCode = "RSSMRA70A01H501S" };

            return new FiscalIssuerSnapshot { LegalName = "TEST Agenzia Srl", CountryCode = "IT", Regime = DocumentRegime.VatOrdinary, VatNumber = "09876543210", FiscalCode = "09876543210" };
        }

        /// <summary>The lines of a document, numbered and owned by its org.</summary>
        private static void Attach(FiscalDocument document, FiscalDocumentLine[] lines, Guid org)
        {
            document.Lines = [.. lines.Select((line, index) => new FiscalDocumentLine
            {
                DocumentId = document.Id,
                OrgId = org,
                LineNumber = index + 1,
                LineType = line.LineType,
                Description = line.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                TotalPrice = line.TotalPrice,
                VatRatePercent = line.VatRatePercent,
                VatAmount = line.VatAmount,
                VatNatureCode = line.VatNatureCode,
            })];
        }
    }

    // ---------------------------------------------------------------- reading what a test gets back

    private static async Task<byte[]> ReadAsync(Stream stream)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static Dictionary<string, byte[]> Unzip(MemoryStream stream)
    {
        stream.Position = 0;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            using var content = entry.Open();
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            files[entry.FullName] = copy.ToArray();
        }

        return files;
    }

    /// <summary>The rows of a CSV as the writer makes it: the cells split on <c>;</c>, a quoted cell kept whole, the BOM dropped; an empty row is one empty cell.</summary>
    private static List<List<string>> Csv(byte[] bytes)
    {
        var rows = new List<List<string>>();
        foreach (var line in Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF).Split("\r\n"))
        {
            var cells = new List<string>();
            var current = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (c == ';' && !quoted)
                {
                    cells.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            cells.Add(current.ToString());
            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>The rows of the table whose title starts with <paramref name="titlePrefix"/>: after its title and its header, up to the empty row.</summary>
    private static List<List<string>> TableRows(List<List<string>> csv, string titlePrefix)
    {
        var title = csv.FindIndex(r => r[0].StartsWith(titlePrefix, StringComparison.Ordinal));
        Assert.True(title >= 0, $"No table starts with «{titlePrefix}»");
        return [.. csv.Skip(title + 2).TakeWhile(r => r.Count > 1 || r[0].Length > 0)];
    }

    private static decimal Amount(string cell) => decimal.Parse(cell, NumberStyles.Number, Italian);

    /// <summary>The amounts of the cells, as numbers: «-0,00» and «0,00» are the same amount.</summary>
    private static decimal[] Amounts(IEnumerable<string> cells) => [.. cells.Select(Amount)];

    /// <summary>The files of the index of a ZIP: path, description, rows and SHA-256.</summary>
    private static List<List<string>> Index(Dictionary<string, byte[]> files) => TableRows(Csv(files["indice.csv"]), "Indice del pacchetto per il commercialista");

    /// <summary>The path of the only CSV register of the documents issued in the name of the owner (the folder of the documents).</summary>
    private static string RegisterOf(Dictionary<string, byte[]> files) =>
        files.Keys.Single(k => k.StartsWith("documenti-emessi/registro-", StringComparison.Ordinal) && k.EndsWith(".csv", StringComparison.Ordinal));

    private sealed class CapturingPdfRenderer(IPdfDocumentRenderer inner) : IPdfDocumentRenderer
    {
        public List<PdfDocumentContent> Rendered { get; } = [];

        public byte[] Render(PdfDocumentContent content)
        {
            Rendered.Add(content);
            return inner.Render(content);
        }
    }

    private sealed class SeedTenant : ITenantContext
    {
        public Guid? OrgId => null;

        public bool FilterEnabled => false;
    }

    /// <summary>The tenant of the owner portal: signed in, with no org: the filter of its request matches nothing.</summary>
    private sealed class FailClosedTenant : ITenantContext
    {
        public Guid? OrgId => null;

        public bool FilterEnabled => true;
    }
}

/// <summary>
/// E17/F02/US01/T01: the endpoints of the documents issued in the name of an owner. The export of the agency is the whole organization only and needs <c>agency.statement.view</c>; it
/// asks the service for the org of the caller and the owner of the route, and nothing for a period that is not one; the owner portal answers 401 without its claim and asks the
/// service for the owner of the token, never one the client names.
/// </summary>
public class OwnerIssuedDocumentsControllerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid OwnerId = Guid.NewGuid();

    /// <summary>
    /// Permission: the export of the agency asks for <c>agency.statement.view</c> on top of the class; a caller who reaches only some properties (a collaborator «Solo alcuni», an account
    /// outside the team) is refused (403) before anything is read, the service is never called; the whole organization asks for the org of the caller and the owner of the route; a
    /// period that is not one is a 400 and nothing is read.
    /// </summary>
    [Fact]
    public async Task OwnerIssuedDocumentsExport_PartialScopeCannotExport_Test()
    {
        var method = typeof(OwnerIssuerProfilesController).GetMethod(nameof(OwnerIssuerProfilesController.ExportIssuedDocuments))!;
        Assert.Contains(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Cast<AuthorizeAttribute>(), a => a.Policy == CasazenPolicies.AgencyStatementView);

        foreach (var restricted in new[] { new HostScope(OrgId, GrantedToUserId: "auth0|solo-alcuni"), new HostScope(OrgId, OwnerId: "auth0|solo-alcuni") })
        {
            var exports = new Mock<IOwnerIssuedDocumentsExport>(MockBehavior.Strict);
            var refused = Assert.IsType<ObjectResult>(await AgencyController(restricted).ExportIssuedDocuments(OwnerId, "2026", exports.Object, CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
            Assert.Equal(ProblemCodes.Forbidden, Assert.IsAssignableFrom<ProblemDetails>(refused.Value).Extensions["code"]);
            exports.VerifyNoOtherCalls();
        }

        var whole = new Mock<IOwnerIssuedDocumentsExport>(MockBehavior.Strict);
        whole
            .Setup(e => e.BuildAsync(It.Is<IReadOnlyCollection<OwnerIssuedDocumentsOwner>>(o => o.Single() == new OwnerIssuedDocumentsOwner(OrgId, OwnerId)), It.IsAny<AccountantPackagePeriod>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountantPackageResult(4, 9, 1000, []));
        Assert.IsType<EmptyResult>(await AgencyController(new HostScope(OrgId)).ExportIssuedDocuments(OwnerId, "2026-10", whole.Object, CancellationToken.None));
        whole.Verify(e => e.BuildAsync(It.IsAny<IReadOnlyCollection<OwnerIssuedDocumentsOwner>>(), It.Is<AccountantPackagePeriod>(p => p.Key == "2026-10"), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);

        foreach (var bad in new string?[] { null, "", "2026-13", "2026-1", "26", "next month" })
        {
            var invalid = Assert.IsType<ObjectResult>(await AgencyController(new HostScope(OrgId)).ExportIssuedDocuments(OwnerId, bad, whole.Object, CancellationToken.None));
            Assert.Equal((StatusCodes.Status400BadRequest, AccountantPackageErrors.InvalidPeriod), (invalid.StatusCode, Assert.IsAssignableFrom<ProblemDetails>(invalid.Value).Extensions["code"]));
        }

        whole.VerifyNoOtherCalls();
    }

    /// <summary>The owner portal: 401 without its claim, a period that is not one is a 400, and the service is asked for the owner of the token.</summary>
    [Fact]
    public async Task OwnerPortal_IssuedDocuments_AreAskedForTheOwnerOfTheToken_Test()
    {
        var portal = new Mock<IOwnerPortalService>(MockBehavior.Strict);

        var anonymous = PortalController(portal.Object, withOwnerClaim: false);
        Assert.IsType<UnauthorizedResult>((await anonymous.GetIssuedDocuments(null, CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>(await anonymous.GetIssuedDocumentPdf(Guid.NewGuid(), null, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await anonymous.GetIssuedDocumentXml(Guid.NewGuid(), CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await anonymous.ExportIssuedDocuments("2026", CancellationToken.None));
        portal.VerifyNoOtherCalls();

        var controller = PortalController(portal.Object, withOwnerClaim: true);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>((await controller.GetIssuedDocuments("2026-13", CancellationToken.None)).Result).StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(await controller.ExportIssuedDocuments("not a period", CancellationToken.None)).StatusCode);
        portal.VerifyNoOtherCalls();

        portal
            .Setup(p => p.GetOwnerIssuedDocumentsAsync(PortalOwner, It.Is<AccountantPackagePeriod?>(period => period != null && period.Key == "2026"), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OwnerPortalIssuedDocumentInfo(Guid.NewGuid(), OrgId, "TEST Agenzia", "SRL2026/1", new DateOnly(2026, 10, 14), FiscalDocumentKind.Invoice, 110m, "EUR", "to_send", true)]);
        portal
            .Setup(p => p.ExportOwnerIssuedDocumentsAsync(PortalOwner, It.Is<AccountantPackagePeriod>(period => period.Key == "2026-10"), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountantPackageResult(1, 3, 10, []));

        var list = Assert.IsType<OkObjectResult>((await controller.GetIssuedDocuments("2026", CancellationToken.None)).Result);
        var dto = Assert.Single(Assert.IsAssignableFrom<IEnumerable<OwnerPortalIssuedDocumentDto>>(list.Value));
        Assert.Equal(("SRL2026/1", "Invoice", "to_send", true), (dto.Number, dto.Kind, dto.SdiStatus, dto.HasXml));
        Assert.IsType<EmptyResult>(await controller.ExportIssuedDocuments("2026-10", CancellationToken.None));
        portal.VerifyAll();
    }

    private static readonly Guid PortalOwner = Guid.NewGuid();

    private static OwnerPortalController PortalController(IOwnerPortalService portal, bool withOwnerClaim)
    {
        var claims = withOwnerClaim ? new[] { new Claim(OwnerPortalClaims.OwnerId, PortalOwner.ToString()) } : [];
        return new OwnerPortalController(portal, Options.Create(new Auth0Options()), NullLogger<OwnerPortalController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static OwnerIssuerProfilesController AgencyController(HostScope scope)
    {
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var reach = new Mock<IHostScopeResolver>();
        reach.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Success());

        var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|solo-alcuni"), new Claim(ClaimTypes.NameIdentifier, "auth0|solo-alcuni")], "test");
        return new OwnerIssuerProfilesController(Mock.Of<IOwnerIssuerProfileService>(), org.Object, reach.Object, authorization.Object, NullLogger<OwnerIssuerProfilesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = services } },
        };
    }
}

/// <summary>
/// E17/F02/US01/T01: the reads of the documents issued in the name of an owner, run on the PostgreSQL provider without a server (see <see cref="NpgsqlTranslationProbe"/>). The proofs run on
/// InMemory, which accepts what Npgsql cannot turn into SQL; this fails here, before the CI, when the list, the one document with its lines, the documents of the ZIP with their lines or
/// the invoices of the agency to the owner have no translation.
/// </summary>
public class OwnerIssuedDocumentsNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid OwnerId = Guid.Parse("5b1f6c2e-3a47-4d90-9e18-7c2a4f6d8b30");
    private static readonly AccountantPackagePeriod Year = AccountantPackagePeriod.OfYear(2026);

    private static OwnerIssuedDocumentsExport ExportOn(AppDbContext db) =>
        new(db, Mock.Of<IOwnerStatementDocuments>(), new MigraDocPdfDocumentRenderer(), new ResxDocumentTexts(), Options.Create(new DocumentArchiveOptions()), NullLogger<OwnerIssuedDocumentsExport>.Instance);

    private static readonly Dictionary<string, Func<OwnerIssuedDocumentsExport, Task>> Calls = new()
    {
        ["profiles"] = e => e.ProfilesOfAsync([new OwnerIssuedDocumentsOwner(OrgId, OwnerId)], CancellationToken.None),
        ["issued-count"] = e => e.IssuedBy([Guid.NewGuid()], Year).CountAsync(),
        ["issued-list"] = e => e.ListRows([Guid.NewGuid()], null, 500).ToListAsync(),
        ["issued-list-of-a-month"] = e => e.ListRows([Guid.NewGuid()], AccountantPackagePeriod.OfMonth(2026, 10), 500).ToListAsync(),
        ["one-document-with-lines"] = e => e.OneOf([Guid.NewGuid()], Guid.NewGuid(), withLines: true).FirstOrDefaultAsync(),
        ["issued-documents-of-the-zip"] = e => e.Project(e.IssuedBy([Guid.NewGuid()], Year)).ToListAsync(),
        ["agency-invoices-count"] = e => e.InvoicesOfTheAgencyTo(new OwnerIssuedDocumentsOwner(OrgId, OwnerId), Year).CountAsync(),
        ["agency-invoices-of-the-zip"] = e => e.Project(e.InvoicesOfTheAgencyTo(new OwnerIssuedDocumentsOwner(OrgId, OwnerId), Year)).ToListAsync(),
    };

    public static TheoryData<string> EveryCall()
    {
        var data = new TheoryData<string>();
        foreach (var call in Calls.Keys)
            data.Add(call);

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task Call_IsTranslatedToSqlByNpgsql(string call)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Calls[call](ExportOn(db)));

        Assert.True(statements.Count >= 1, $"{call}: no statement was sent, so nothing was translated.");
    }
}
