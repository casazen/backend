using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FiscalDocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/BE-ACCOUNTANT-GAPS (the finding of the Italian acceptance scenario): the ZIP of the accountant holds the self-billed invoices (TD17) issued in the period, with their register, their PDF and
/// their XML, and the portal commissions of the accounts the agency holds (V2), as files of their own in <c>fatture-proprietari/</c>. They are written only for a period that has them (any other
/// period is the package it always was) and only with the owner accounting of the whole organization; every amount is the stored one, to the cent.
/// </summary>
public class AccountantPackageSelfBilledTests
{
    private const string CompanyVat = "09876543217";
    private const string SecondCompanyVat = "01112223334";
    private const string PortalCommissionsPath = AccountantPackageStructure.OwnerInvoicesFolder + "/" + AccountantPackageStructure.PortalCommissionsFile + ".csv";

    private static readonly AccountantPackagePeriod September = AccountantPackagePeriod.OfMonth(2026, 9);
    private static readonly AccountantPackagePeriod October = AccountantPackagePeriod.OfMonth(2026, 10);
    private static readonly AccountantPackagePeriod November = AccountantPackagePeriod.OfMonth(2026, 11);
    private static readonly AccountantPackagePeriod Year2026 = AccountantPackagePeriod.OfYear(2026);
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private static string RegisterOf(string vat) => $"{AccountantPackageStructure.SelfBilledFolder}/registro-it-{vat}.csv";

    /// <summary>
    /// PROOF TEST: money rule. The TD17 issued in the period are in the package, identified («Autofattura TD17»), with the foreign supplier where a guest document has its recipient, the month of the
    /// commissions, and the taxable amount, the VAT and the total of the document to the cent (the very amounts the document stores), the VAT of each rate as its lines store it, its PDF and its XML;
    /// one register for each issuer, the VAT file of the issuer has them as a class of their own, and the index lists every new file with its hash. A month holds the TD17 issued in it and a year all of them.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_ContainsSelfBilledInvoices_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        var (baseline, _) = await BuildAsync(rig, AccountantPackageAccess.Full, November);

        var documents = await SeedSelfBilledAsync(rig);
        var (result, files) = await BuildAsync(rig, AccountantPackageAccess.Full, November);

        // Two TD17 issued in November (the third is of December): two more documents, each with its PDF and its XML.
        Assert.Equal(baseline.Documents + 2, result.Documents);
        var company = documents[0];
        Assert.Equal((135.00m, 29.70m, 0m, 164.70m), (company.TaxableBase, company.VatAmount, company.StampDuty, company.Total));

        // The register of the company: one row, the amounts of the stored document, its supplier, the month it integrates (October).
        var register = Blocks(files[RegisterOf(CompanyVat)]);
        Assert.Contains("TEST Immobiliare Lago Srl", register[0][0][0], StringComparison.Ordinal);
        Assert.Contains("01/11/2026", register[0][0][0], StringComparison.Ordinal);
        Assert.Contains("30/11/2026", register[0][0][0], StringComparison.Ordinal);
        var row = Assert.Single(register[0].Skip(2));
        Assert.Equal(
            new[] { "Autofattura TD17", "TD17-2026/1", "03/11/2026", "TEST Portale B.V.", "2026-10", "135,00", "29,70", "0,00", "164,70", "EUR", "NL", "NL805734958B01" },
            row);
        Assert.Equal((company.TaxableBase, company.VatAmount, company.StampDuty, company.Total), (Money(row[5]), Money(row[6]), Money(row[7]), Money(row[8])));

        // The VAT as the lines store it (one rate: both lines are at 22%), and the totals for each rate.
        Assert.Equal(new[] { "TD17-2026/1", "03/11/2026", "22,00", string.Empty, "135,00", "29,70", "EUR" }, Assert.Single(register[1].Skip(2)));
        Assert.Equal(new[] { "EUR", "22,00", string.Empty, "135,00", "29,70", "164,70", "1" }, Assert.Single(register[2].Skip(2)));

        // The other holder issues on its own series and has its own register (its number is the first of its series too).
        var second = documents[1];
        var secondRow = Assert.Single(Blocks(files[RegisterOf(SecondCompanyVat)])[0].Skip(2));
        Assert.Equal(
            new[] { "Autofattura TD17", "TD17-2026/1", "04/11/2026", "TEST Alloggi Ltd", "2026-10", "100,00", "22,00", "0,00", "122,00", "EUR", "IE", "IE6388047V" },
            secondRow);
        Assert.Equal((second.TaxableBase, second.VatAmount, second.Total), (Money(secondRow[5]), Money(secondRow[6]), Money(secondRow[8])));

        // The document files are the ones the screens download: the PDF of the layout of a TD17 and the XML as it was stored.
        var folder = AccountantPackageStructure.SelfBilledFolder;
        Assert.Equal("%PDF", Encoding.ASCII.GetString(files[$"{folder}/2026-11-03-TD17-2026-1.pdf"], 0, 4));
        Assert.Equal(company.FatturaPaXml, Text(files[$"{folder}/2026-11-03-TD17-2026-1.xml"]));
        Assert.Equal(second.FatturaPaXml, Text(files[$"{folder}/2026-11-04-TD17-2026-1.xml"]));
        Assert.DoesNotContain(files.Keys, k => k.Contains("2026-12-02", StringComparison.Ordinal));

        // The VAT file of the issuer carries the TD17 as a class of its own, with the stored VAT, never mixed with its other invoices.
        Assert.Contains("2026-11;Autofatture TD17;22,00;;EUR;135,00;29,70;164,70;1", Text(files[$"iva/iva-it-{CompanyVat}.csv"]), StringComparison.Ordinal);
        Assert.Contains("2026-11;Autofatture TD17;22,00;;EUR;100,00;22,00;122,00;1", Text(files[$"iva/iva-it-{SecondCompanyVat}.csv"]), StringComparison.Ordinal);
        AssertTheIndex(files);

        // A year holds the three, the December one included, added up for each rate of the company.
        var (_, year) = await BuildAsync(rig, AccountantPackageAccess.Full, Year2026);
        var yearRegister = Blocks(year[RegisterOf(CompanyVat)]);
        Assert.Equal(2, yearRegister[0].Count - 2);
        Assert.Equal(new[] { "EUR", "22,00", string.Empty, "185,00", "40,70", "225,70", "2" }, Assert.Single(yearRegister[2].Skip(2)));
        Assert.Contains("2026-12;Autofatture TD17;22,00;;EUR;50,00;11,00;61,00;1", Text(year[$"iva/iva-it-{CompanyVat}.csv"]), StringComparison.Ordinal);
        Assert.Contains($"{folder}/2026-12-02-TD17-2026-2.pdf", year.Keys);
        AssertTheIndex(year);
    }

    /// <summary>
    /// PROOF TEST: money rule. The portal commissions of the accounts the agency holds (V2) are in the package, one row for each statement month, owner and channel: the amount is the one of the
    /// owner ledger (a listing whose account the owner holds is not in it), it ties to the figure of the agency summary, the treatment says whether it is recharged to the owner or borne by the agency
    /// (<c>RechargePortalCommissions</c>), and a reversal cancels the line it reverses out of the table.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_ContainsTheV2PortalCommission_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        var (lake, stay) = await V2ListingAsync(rig);

        var (_, files) = await BuildAsync(rig, AccountantPackageAccess.Full, October);
        var blocks = Blocks(files[PortalCommissionsPath]);
        var row = Assert.Single(blocks[0].Skip(2));
        Assert.Equal(new[] { "2026-10", "TEST Proprietario", "Booking.com", "CHF", "1", "120,00", "Riaddebitata al proprietario" }, row);
        Assert.Equal(new[] { "Booking.com", "CHF", "Riaddebitata al proprietario", "120,00" }, Assert.Single(blocks[1].Skip(2)));
        Assert.True(files.ContainsKey(PortalCommissionsPath.Replace(".csv", ".xlsx", StringComparison.Ordinal)));

        // The amount is what the ledger says of the lake house (the 150.00 of the mountain house, whose account the owner holds, is not here) and the figure of the summary.
        var ledger = -await rig.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.EntryType == OwnerLedgerEntryType.ChannelFee && e.PropertyId == lake).SumAsync(e => e.Amount);
        var summary = await rig.Summaries.GetAsync(rig.Scope, AccountantPackageAccess.Full, October);
        Assert.Equal((120.00m, 120.00m), (ledger, Money(row[5])));
        Assert.Equal(Money(row[5]), Assert.Single(summary.PortalCommissionsBorne).Amount);
        Assert.DoesNotContain("150,00", Text(files[PortalCommissionsPath]), StringComparison.Ordinal);
        AssertTheIndex(files);

        // The agency that does not recharge the commissions bears them: the same amount, said so.
        var profile = await rig.Db.OrgFiscalIssuerProfiles.SingleAsync(p => p.OrgId == rig.OrgId);
        profile.RechargePortalCommissions = false;
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        var (_, borne) = await BuildAsync(rig, AccountantPackageAccess.Full, October);
        Assert.Equal(new[] { "2026-10", "TEST Proprietario", "Booking.com", "CHF", "1", "120,00", "A carico dell'agenzia" }, Assert.Single(Blocks(borne[PortalCommissionsPath])[0].Skip(2)));

        // A reversal and the line it reverses cancel out: nothing is left to list, and the table is not written.
        var original = await rig.Db.OwnerLedgerEntries.AsNoTracking().SingleAsync(e => e.EntryType == OwnerLedgerEntryType.ChannelFee && e.PropertyId == lake);
        rig.Db.OwnerLedgerEntries.Add(Fee(rig, lake, stay, 10, 120.00m, reverses: original.Id));
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        var (_, reversed) = await BuildAsync(rig, AccountantPackageAccess.Full, October);
        Assert.DoesNotContain(reversed.Keys, k => k.Contains(AccountantPackageStructure.PortalCommissionsFile, StringComparison.Ordinal));
        Assert.Empty((await rig.Summaries.GetAsync(rig.Scope, AccountantPackageAccess.Full, October)).PortalCommissionsBorne);
    }

    /// <summary>
    /// PROOF TEST: data rule. A period with no TD17 and no commission of a V2 account is the package it always was: none of the new files, none of their words in the VAT file and, with
    /// the TD17 and the commissions of other periods in the database, the same files with the same content (the CSV of the period do not change by one byte).
    /// </summary>
    [Fact]
    public async Task AccountantPackage_PeriodWithoutThem_IsUnchanged_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        var (lake, stay) = await V2ListingAsync(rig);
        var (_, before) = await BuildAsync(rig, AccountantPackageAccess.Full, September);

        Assert.DoesNotContain(before.Keys, k => k.StartsWith(AccountantPackageStructure.SelfBilledFolder, StringComparison.Ordinal) || k.Contains(AccountantPackageStructure.PortalCommissionsFile, StringComparison.Ordinal));
        Assert.DoesNotContain("Autofatture", Text(Assert.Single(before, f => f.Key.StartsWith("iva/iva-", StringComparison.Ordinal) && f.Key.EndsWith(".csv", StringComparison.Ordinal)).Value), StringComparison.Ordinal);
        Assert.Contains("fatture-proprietari/registro.csv", before.Keys);

        // November has a TD17 and a commission of the V2 listing: September, which has none, does not change.
        await SeedSelfBilledAsync(rig);
        rig.Db.OwnerLedgerEntries.Add(Fee(rig, lake, stay, 11, -80.00m));
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        var (_, after) = await BuildAsync(rig, AccountantPackageAccess.Full, September);

        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var path in before.Keys.Where(k => k.EndsWith(".csv", StringComparison.Ordinal) && k != AccountantPackageStructure.IndexFile))
            Assert.Equal(before[path], after[path]);

        // And the periods that do have them are not the same package.
        var (_, november) = await BuildAsync(rig, AccountantPackageAccess.Full, November);
        Assert.Contains(november.Keys, k => k.StartsWith(AccountantPackageStructure.SelfBilledFolder, StringComparison.Ordinal));
        Assert.Contains(PortalCommissionsPath, november.Keys);
    }

    /// <summary>
    /// PROOF TEST: tenant rule. The documents and the ledger lines of another organization are never in the package: its TD17 (with a number, a supplier and an issuer of its own) and a
    /// commission line that points at the listing and the stay of this org change no file, in any file, name or number.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_NeverContainsAnotherOrgsDocuments_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        await SeedSelfBilledAsync(rig);
        var (lake, stay) = await V2ListingAsync(rig);
        var (ownResult, own) = await BuildAsync(rig, AccountantPackageAccess.Full, Year2026);

        var other = Guid.NewGuid();
        rig.Db.FiscalDocuments.Add(SelfBilled(other, 9, 11, new DateOnly(2026, 11, 3), CompanyOf("TEST Altra Societa Srl", "05555555555"), "TEST Altro Portale B.V.", "NL", "NL999999999B01", 10.00m, 20.00m));
        rig.Db.OwnerLedgerEntries.Add(Fee(rig, lake, stay, 10, -987.65m, orgId: other));
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        var (result, files) = await BuildAsync(rig, AccountantPackageAccess.Full, Year2026);

        Assert.Equal(ownResult.Documents, result.Documents);
        Assert.Equal(own.Keys.Order(StringComparer.Ordinal), files.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, content) in files.Where(f => f.Key.EndsWith(".csv", StringComparison.Ordinal) || f.Key.EndsWith(".xml", StringComparison.Ordinal)))
        {
            var text = Text(content);
            foreach (var foreign in new[] { "TD17-2026/9", "TEST Altro Portale", "TEST Altra Societa", "05555555555", "NL999999999B01", "987,65" })
                Assert.False(text.Contains(foreign, StringComparison.Ordinal), $"{path} holds «{foreign}» of another organization");

            if (path.EndsWith(".csv", StringComparison.Ordinal) && path != AccountantPackageStructure.IndexFile)
                Assert.Equal(own[path], content);
        }
    }

    /// <summary>
    /// PROOF TEST: permission and privacy rule. The TD17 and the commissions of the V2 accounts are owner accounting (the cost of a portal of an owner company is that owner's): a caller without
    /// <c>agency.statement.view</c>, and one that reaches only some properties, find none of it in any file, not even as a figure of the VAT file; the caller that has both has all of it.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_SelfBilledAndPortalCommissions_AreOwnerAccounting_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        await SeedSelfBilledAsync(rig);

        var (_, full) = await BuildAsync(rig, AccountantPackageAccess.Full, Year2026);
        Assert.Contains(full.Keys, k => k.StartsWith(AccountantPackageStructure.SelfBilledFolder, StringComparison.Ordinal));
        Assert.Contains(PortalCommissionsPath, full.Keys);

        var (_, withoutPermission) = await BuildAsync(rig, AccountantPackageAccess.WithoutOwnerAccounting, Year2026);
        var (_, restricted) = await BuildAsync(rig, AccountantPackageAccess.Full, Year2026, new HostScope(rig.OrgId, GrantedToUserId: "auth0|lago"));
        foreach (var files in new[] { withoutPermission, restricted })
        {
            Assert.DoesNotContain(files.Keys, k => k.StartsWith("fatture-proprietari/", StringComparison.Ordinal));
            Assert.DoesNotContain(files.Keys, k => k.Contains("-it-", StringComparison.Ordinal) && k.StartsWith("iva/", StringComparison.Ordinal));
            foreach (var (path, content) in files.Where(f => f.Key.EndsWith(".csv", StringComparison.Ordinal) && f.Key != AccountantPackageStructure.IndexFile))
            {
                var text = Text(content);
                foreach (var word in new[] { "TD17", "Autofatture", "TEST Portale", "TEST Immobiliare Lago", "TEST Alloggi", "NL805734958B01", "Booking.com;" })
                    Assert.False(text.Contains(word, StringComparison.Ordinal), $"{path} holds «{word}», which is owner accounting");
            }
        }
    }

    /// <summary>
    /// PROOF TEST: data rule. A name that a spreadsheet would run as a formula is written as text in the register of the TD17: with a leading apostrophe in the CSV and as a string that is never
    /// calculated in the Excel file (the rule of every file of the package, kept for the new register).
    /// </summary>
    [Fact]
    public async Task AccountantPackage_SelfBilledRegister_NeverRunsAsAFormula_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        rig.Db.FiscalDocuments.Add(SelfBilled(rig.OrgId, 5, 10, new DateOnly(2026, 11, 5), CompanyOf("TEST Immobiliare Lago Srl", CompanyVat), "=SUM(1+1)", "NL", "NL805734958B01", 10.00m));
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();

        var (_, files) = await BuildAsync(rig, AccountantPackageAccess.Full, November);

        var row = Assert.Single(Blocks(files[RegisterOf(CompanyVat)])[0].Skip(2));
        Assert.Equal("'=SUM(1+1)", row[3]);
        using var workbook = new ZipArchive(new MemoryStream(files[RegisterOf(CompanyVat).Replace(".csv", ".xlsx", StringComparison.Ordinal)]), ZipArchiveMode.Read);
        using var sheet = new StreamReader(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8);
        var xml = await sheet.ReadToEndAsync();
        Assert.Contains("=SUM(1+1)", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the data

    /// <summary>Three TD17 of two owner companies (V1 holders): the first and the second issued in November for October, the third in December for November; a series of its own for each company.</summary>
    private static async Task<IReadOnlyList<FiscalDocument>> SeedSelfBilledAsync(AccountantPackageRig rig)
    {
        var documents = new[]
        {
            SelfBilled(rig.OrgId, 1, 10, new DateOnly(2026, 11, 3), CompanyOf("TEST Immobiliare Lago Srl", CompanyVat), "TEST Portale B.V.", "NL", "NL805734958B01", 67.50m, 67.50m),
            SelfBilled(rig.OrgId, 1, 10, new DateOnly(2026, 11, 4), CompanyOf("TEST Casa Monte Srl", SecondCompanyVat), "TEST Alloggi Ltd", "IE", "IE6388047V", 100.00m),
            SelfBilled(rig.OrgId, 2, 11, new DateOnly(2026, 12, 2), CompanyOf("TEST Immobiliare Lago Srl", CompanyVat), "TEST Portale B.V.", "NL", "NL805734958B01", 50.00m),
        };
        rig.Db.FiscalDocuments.AddRange(documents);
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        return documents;
    }

    private static string CompanyOf(string legalName, string vatNumber) =>
        new FiscalIssuerSnapshot { LegalName = legalName, CountryCode = "IT", VatNumber = vatNumber, FiscalCode = vatNumber, Regime = DocumentRegime.VatOrdinary }.ToJson();

    /// <summary>
    /// An issued TD17 as the self-billing issues it: the foreign supplier in the recipient columns, the holder as the issuer of the snapshot, one commission line at 22% for each net amount
    /// (the VAT of a line to the cent), the amounts the sums of its lines, the XML kept with it.
    /// </summary>
    private static FiscalDocument SelfBilled(
        Guid orgId, int sequence, int periodMonth, DateOnly issued, string issuerSnapshot, string supplierName, string country, string vatNumber, params decimal[] nets)
    {
        var document = new FiscalDocument
        {
            OrgId = orgId,
            Purpose = FiscalDocumentPurpose.SelfBilling,
            Source = FiscalDocumentSource.CasaZen,
            Kind = FiscalDocumentKind.Invoice,
            Status = FiscalDocumentStatus.Issued,
            Series = "TD17-2026",
            SequenceNumber = sequence,
            Number = $"TD17-2026/{sequence}",
            IssueDate = issued,
            Currency = "EUR",
            IssuerSnapshot = issuerSnapshot,
            RuleVersion = "td17-2026.11-1",
            PeriodYear = 2026,
            PeriodMonth = periodMonth,
            ServicePeriodFrom = new DateOnly(2026, periodMonth, 1),
            ServicePeriodTo = new DateOnly(2026, periodMonth, 1).AddMonths(1).AddDays(-1),
            RecipientKind = InvoiceRecipientKind.Company,
            RecipientName = supplierName,
            RecipientVatNumber = vatNumber,
            RecipientAddressStreet = "Via del Test",
            RecipientAddressCivic = "1",
            RecipientPostalCode = "1000",
            RecipientCity = "Amsterdam",
            RecipientCountryCode = country,
            PaymentChannel = FiscalPaymentChannel.Unknown,
            PaymentState = FiscalDocumentPaymentState.Unknown,
            SdiStatus = FiscalDocumentSdiStatuses.ToSend,
            FatturaPaXml = $"<p:FatturaElettronica>TEST TD17 {orgId:N} {sequence} {issued:yyyyMMdd}</p:FatturaElettronica>",
            CreatedAt = issued.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            UpdatedAt = issued.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        };
        var number = 0;
        foreach (var net in nets)
        {
            number++;
            document.Lines.Add(new FiscalDocumentLine
            {
                OrgId = orgId,
                DocumentId = document.Id,
                LineNumber = number,
                LineType = FiscalLineType.Commission,
                Description = $"TEST Commissioni di intermediazione {supplierName} - TEST Immobile {number} - {periodMonth:00}/2026",
                Quantity = 1m,
                UnitPrice = net,
                TotalPrice = net,
                VatRatePercent = 22m,
                VatAmount = decimal.Round(net * 0.22m, 2, MidpointRounding.AwayFromZero),
            });
        }

        document.TaxableBase = document.Lines.Sum(l => l.TotalPrice);
        document.VatAmount = document.Lines.Sum(l => l.VatAmount ?? 0m);
        document.Total = document.TaxableBase + document.VatAmount;
        return document;
    }

    /// <summary>The lake house of the rig (its Booking.com account is the agency's, V2) and the stay its commission of October was kept on.</summary>
    private static async Task<(Guid Property, Guid Stay)> V2ListingAsync(AccountantPackageRig rig)
    {
        var property = (await rig.Db.PropertyChannelAccountHolders.AsNoTracking().SingleAsync()).PropertyId;
        var stay = await rig.Db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.EntryType == OwnerLedgerEntryType.ChannelFee && e.PropertyId == property)
            .Select(e => e.SourceId!.Value)
            .SingleAsync();
        return (property, stay);
    }

    /// <summary>A channel fee line of the owner ledger of <paramref name="month"/> 2026 on a stay of the listing (negative: a cost of the owner, a reversal is positive and names the line it reverses).</summary>
    private static OwnerLedgerEntry Fee(AccountantPackageRig rig, Guid propertyId, Guid stayId, int month, decimal amount, Guid? reverses = null, Guid? orgId = null) => new()
    {
        OrgId = orgId ?? rig.OrgId,
        PropertyOwnerId = rig.OwnerId,
        PropertyId = propertyId,
        EntryType = OwnerLedgerEntryType.ChannelFee,
        SourceType = OwnerLedgerSourceType.Booking,
        SourceId = stayId,
        IdempotencyKey = $"test:{Guid.NewGuid():N}",
        ServiceDate = new DateOnly(2026, month, 20),
        RecognitionDate = new DateOnly(2026, month, 20),
        PeriodYear = 2026,
        PeriodMonth = month,
        SharePercent = 100m,
        OriginalCurrency = "CHF",
        SourceAmount = amount,
        OriginalAmount = amount,
        StatementCurrency = "CHF",
        Amount = amount,
        ReversesEntryId = reverses,
        PostedAt = new DateTime(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc),
    };

    // ---------------------------------------------------------------- the files

    private static async Task<(AccountantPackageResult Result, Dictionary<string, byte[]> Files)> BuildAsync(
        AccountantPackageRig rig, AccountantPackageAccess access, AccountantPackagePeriod period, HostScope? scope = null)
    {
        using var output = new MemoryStream();
        var result = await rig.Package().BuildAsync(scope ?? rig.Scope, access, period, output);
        return (result, Unzip(output.ToArray()));
    }

    /// <summary>The index lists every file of the ZIP and no other, with the SHA-256 of its bytes.</summary>
    private static void AssertTheIndex(Dictionary<string, byte[]> files)
    {
        var index = Blocks(files[AccountantPackageStructure.IndexFile])[0].Skip(2).ToList();
        Assert.Equal(files.Keys.Where(k => k != AccountantPackageStructure.IndexFile).Order(StringComparer.Ordinal), index.Select(r => r[0]).Order(StringComparer.Ordinal));
        foreach (var entry in index)
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(files[entry[0]])), entry[3]);
    }

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

    /// <summary>An amount as the CSV writes it (a decimal comma, no thousands separator).</summary>
    private static decimal Money(string cell) => decimal.Parse(cell, NumberStyles.Number, Italian);

    /// <summary>The tables of a CSV, each as its rows (the title, the header, the data), the empty row between two tables left out; a cell is what the file holds, quotes undone.</summary>
    private static List<List<List<string>>> Blocks(byte[] bytes)
    {
        var text = Text(bytes);
        var blocks = new List<List<List<string>>>();
        var current = new List<List<string>>();
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
                    if (row.Count == 1 && row[0].Length == 0)
                    {
                        blocks.Add(current);
                        current = [];
                    }
                    else
                    {
                        current.Add(row);
                    }

                    row = [];
                    break;
                default:
                    cell.Append(c);
                    break;
            }
        }

        if (current.Count > 0)
            blocks.Add(current);

        return blocks;
    }
}
