using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
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
    private const string AgencyVat = "01234567897";
    private const string NotIncludedTitle = "Autofatture TD17 non incluse in questo file";
    private const string RejectedTitle = "Autofatture TD17 scartate dal SDI, escluse dalle cifre";
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
            new[] { "Autofattura TD17", "TD17-2026/1", "03/11/2026", "TEST Portale B.V.", "2026-10", "135,00", "29,70", "0,00", "164,70", "EUR", "NL", "NL805734958B01", "Da inviare" },
            row);
        Assert.Equal((company.TaxableBase, company.VatAmount, company.StampDuty, company.Total), (Money(row[5]), Money(row[6]), Money(row[7]), Money(row[8])));

        // The VAT as the lines store it (one rate: both lines are at 22%), and the totals for each rate.
        Assert.Equal(new[] { "TD17-2026/1", "03/11/2026", "22,00", string.Empty, "135,00", "29,70", "EUR" }, Assert.Single(register[1].Skip(2)));
        Assert.Equal(new[] { "EUR", "22,00", string.Empty, "135,00", "29,70", "164,70", "1" }, Assert.Single(register[2].Skip(2)));

        // The other holder issues on its own series and has its own register (its number is the first of its series too).
        var second = documents[1];
        var secondRow = Assert.Single(Blocks(files[RegisterOf(SecondCompanyVat)])[0].Skip(2));
        Assert.Equal(
            new[] { "Autofattura TD17", "TD17-2026/1", "04/11/2026", "TEST Alloggi Ltd", "2026-10", "100,00", "22,00", "0,00", "122,00", "EUR", "IE", "IE6388047V", "Da inviare" },
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
    /// owner ledger (a listing whose account the owner holds is not in it), it ties to the figure of the agency summary, the treatment says whether it is recharged to the owner or not charged back on an invoice
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

        // The agency that does not recharge the commissions: the same amount, said as not charged back on an invoice (the label never says who bears it).
        var profile = await rig.Db.OrgFiscalIssuerProfiles.SingleAsync(p => p.OrgId == rig.OrgId);
        profile.RechargePortalCommissions = false;
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        var (_, borne) = await BuildAsync(rig, AccountantPackageAccess.Full, October);
        Assert.Equal(new[] { "2026-10", "TEST Proprietario", "Booking.com", "CHF", "1", "120,00", "Non riaddebitata in fattura" }, Assert.Single(Blocks(borne[PortalCommissionsPath])[0].Skip(2)));

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

    /// <summary>
    /// PROOF TEST: permission rule, with an Italian issuer (the rig of the other tests is Swiss). The VAT file of an Italian issuer carries the TD17 as a class of its own only with the owner accounting;
    /// a caller of the whole org without <c>agency.statement.view</c> finds none of them in it and the file says why (the reason of the omitted folders), never a file that reads as «no TD17»; a caller
    /// that reaches only some properties is told of the invoices to the owners and of the TD17; the caller that holds the permission finds no note.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_ItalianVatFile_SaysThatTheSelfBilledAreOwnerAccounting_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        var agency = CompanyOf("TEST Agenzia Srl", AgencyVat);
        rig.Db.FiscalDocuments.AddRange(
            GuestOf(rig.OrgId, agency),
            SelfBilled(rig.OrgId, 1, 10, new DateOnly(2026, 11, 3), agency, "TEST Portale B.V.", "NL", "NL805734958B01", 100.00m));
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        var vat = $"iva/iva-it-{AgencyVat}.csv";

        // With the owner accounting: the TD17 of the agency (V2) is a class of its VAT file, next to its invoice to the guest, and the file has no note.
        var (_, full) = await BuildAsync(rig, AccountantPackageAccess.Full, November);
        var fullText = Text(full[vat]);
        Assert.Contains("2026-11;Fatture ordinarie;22,00;;EUR;100,00;22,00;122,00;1", fullText, StringComparison.Ordinal);
        Assert.Contains("2026-11;Autofatture TD17;22,00;;EUR;100,00;22,00;122,00;1", fullText, StringComparison.Ordinal);
        Assert.DoesNotContain(NotIncludedTitle, fullText, StringComparison.Ordinal);

        // The whole org without agency.statement.view: no TD17 figure, and the file says why with the reason the omitted folders give.
        var (_, without) = await BuildAsync(rig, AccountantPackageAccess.WithoutOwnerAccounting, November);
        var withoutText = Text(without[vat]);
        Assert.Contains("2026-11;Fatture ordinarie;22,00;;EUR;100,00;22,00;122,00;1", withoutText, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-11;Autofatture TD17", withoutText, StringComparison.Ordinal);
        var withoutBlocks = Blocks(without[vat]);
        Assert.Equal(2, withoutBlocks.Count);
        var note = Assert.Single(withoutBlocks, b => b[0][0] == NotIncludedTitle);
        Assert.Equal("Servono il permesso di lettura dei rendiconti dei proprietari (agency.statement.view) e l'accesso a tutta l'organizzazione.", Assert.Single(note.Skip(2))[0]);

        // A caller that reaches only some properties reads neither the invoices to the owners nor the TD17: the file says both.
        var texts = new PackageTexts(new ResxDocumentTexts(), PackageTexts.PackageLanguage);
        var issuer = new AgencyIssuer($"IT|{AgencyVat}|TEST Agenzia Srl", "TEST Agenzia Srl", "IT", AgencyVat, DocumentRegime.VatOrdinary);
        var restricted = AccountantPackageDocuments.Vat(issuer, [], [], texts, new VatCoverage(OwnerInvoices: false, PortalCommissions: false, SelfBilled: false));
        Assert.Equal(3, restricted.Count);
        Assert.Equal(("Fatture emesse ai proprietari non incluse in questo file", NotIncludedTitle), (restricted[1].Title, restricted[2].Title));
    }

    /// <summary>
    /// PROOF TEST: money and data rule. A TD17 the SDI rejected counts as not issued (⚖ the default of the PO and the accountant): its row is in the register of its issuer with the state «Scartata», but it
    /// is in neither table of the rates (the totals are the ones of the valid TD17, to the cent), not in the class «Autofatture TD17» of the VAT file (which says how many are left out), and it has no PDF and
    /// no XML; an issuer whose TD17 are all rejected still has its register and its VAT file, with the note and no figure.
    /// </summary>
    [Fact]
    public async Task AccountantPackage_ARejectedSelfBilledInvoice_IsListedButNeverCounted_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        var (baseline, _) = await BuildAsync(rig, AccountantPackageAccess.Full, November);

        var valid = SelfBilled(rig.OrgId, 1, 10, new DateOnly(2026, 11, 3), CompanyOf("TEST Immobiliare Lago Srl", CompanyVat), "TEST Portale B.V.", "NL", "NL805734958B01", 67.50m, 67.50m);
        valid.SdiStatus = FiscalDocumentSdiStatuses.Delivered;
        var rejected = SelfBilled(rig.OrgId, 2, 10, new DateOnly(2026, 11, 5), CompanyOf("TEST Immobiliare Lago Srl", CompanyVat), "TEST Portale B.V.", "NL", "NL805734958B01", 100.00m);
        rejected.SdiStatus = FiscalDocumentSdiStatuses.Rejected;
        var onlyRejected = SelfBilled(rig.OrgId, 1, 10, new DateOnly(2026, 11, 4), CompanyOf("TEST Casa Monte Srl", SecondCompanyVat), "TEST Alloggi Ltd", "IE", "IE6388047V", 50.00m);
        onlyRejected.SdiStatus = FiscalDocumentSdiStatuses.Rejected;
        rig.Db.FiscalDocuments.AddRange(valid, rejected, onlyRejected);
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();

        var (result, files) = await BuildAsync(rig, AccountantPackageAccess.Full, November);

        // One document has files: the valid one (its PDF and its XML); the rejected ones have none, and only the valid one is a document of the package.
        var folder = AccountantPackageStructure.SelfBilledFolder;
        Assert.Equal(baseline.Documents + 1, result.Documents);
        Assert.Contains($"{folder}/2026-11-03-TD17-2026-1.pdf", files.Keys);
        Assert.Contains($"{folder}/2026-11-03-TD17-2026-1.xml", files.Keys);
        Assert.Equal(2, files.Keys.Count(k => k.StartsWith(folder + "/", StringComparison.Ordinal) && (k.EndsWith(".pdf", StringComparison.Ordinal) || k.EndsWith(".xml", StringComparison.Ordinal))));

        // The register lists both with their state and their own amounts; the two tables of the rates hold the valid one only (the exact totals of the valid TD17).
        var register = Blocks(files[RegisterOf(CompanyVat)]);
        var rows = register[0].Skip(2).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "Autofattura TD17", "TD17-2026/1", "03/11/2026", "TEST Portale B.V.", "2026-10", "135,00", "29,70", "0,00", "164,70", "EUR", "NL", "NL805734958B01", "Consegnata" }, rows[0]);
        Assert.Equal(new[] { "Autofattura TD17", "TD17-2026/2", "05/11/2026", "TEST Portale B.V.", "2026-10", "100,00", "22,00", "0,00", "122,00", "EUR", "NL", "NL805734958B01", "Scartata" }, rows[1]);
        Assert.Equal(new[] { "TD17-2026/1", "03/11/2026", "22,00", string.Empty, "135,00", "29,70", "EUR" }, Assert.Single(register[1].Skip(2)));
        Assert.Equal(new[] { "EUR", "22,00", string.Empty, "135,00", "29,70", "164,70", "1" }, Assert.Single(register[2].Skip(2)));
        Assert.Equal(RejectedTitle, register[3][0][0]);
        Assert.StartsWith("Autofatture TD17 scartate dal SDI: 1.", Assert.Single(register[3].Skip(2))[0], StringComparison.Ordinal);

        // The class «Autofatture TD17» of the VAT file is the valid TD17 only, and a note says how many are left out.
        var vat = Blocks(files[$"iva/iva-it-{CompanyVat}.csv"]);
        Assert.Equal(new[] { "2026-11", "Autofatture TD17", "22,00", string.Empty, "EUR", "135,00", "29,70", "164,70", "1" }, Assert.Single(vat[0].Skip(2)));
        Assert.Equal(RejectedTitle, vat[1][0][0]);
        Assert.StartsWith("Autofatture TD17 scartate dal SDI: 1.", Assert.Single(vat[1].Skip(2))[0], StringComparison.Ordinal);

        // The issuer whose only TD17 was rejected: its register lists it, the tables of the rates and its VAT file have no figure, the note is there, and the index lists the files that exist.
        var monte = Blocks(files[RegisterOf(SecondCompanyVat)]);
        Assert.Equal("Scartata", Assert.Single(monte[0].Skip(2))[12]);
        Assert.Empty(monte[1].Skip(2));
        Assert.Empty(monte[2].Skip(2));
        var monteVat = Blocks(files[$"iva/iva-it-{SecondCompanyVat}.csv"]);
        Assert.Empty(monteVat[0].Skip(2));
        Assert.Equal(RejectedTitle, monteVat[1][0][0]);
        AssertTheIndex(files);
    }

    /// <summary>
    /// PROOF TEST: data rule (FIX/BE-TD17-JOB-EXIT). The numbers closed as «non più dovuta» are in the package, in a register of the unused numbers next to the register of the TD17 (<c>numeri-non-utilizzati</c>, CSV and
    /// Excel, listed in the index with their hash): for each one the issuer (its series is its own), the number, the day of the rejected original, the day of the closing, the reason in words, the note, the SDI outcome of the
    /// original (its state and the code of the rejection) and the user (the name, else a fixed text: the id of a user is nowhere in the files). Only the closings of the period count, by the day of Rome (a closing at 00:30 of the 1st of December is
    /// December's; a year has them all), never another organization's and never a draft that was discarded without a closing; only for who reads the owner accounting of the whole organization; a text a spreadsheet would
    /// run as a formula (the note) is written as text in both files. <b>A period without a closing is the package it always was: no file, not a byte changed.</b>
    /// </summary>
    [Fact]
    public async Task AccountantPackage_ListsTheUnusedNumbers_AndAPeriodWithoutThemIsUnchanged_Test()
    {
        await using var rig = await AccountantPackageRig.CreateAsync();
        var unusedPath = $"{AccountantPackageStructure.SelfBilledFolder}/{AccountantPackageStructure.UnusedNumbersFile}.csv";
        var (_, before) = await BuildAsync(rig, AccountantPackageAccess.Full, September);
        Assert.DoesNotContain(before.Keys, k => k.Contains(AccountantPackageStructure.UnusedNumbersFile, StringComparison.Ordinal));

        // Three numbers closed: one of the company on the 20th of November (a formula as its note), one of the same company at 00:30 of the 1st of December in Rome (23:30 UTC of the 30th), one of another issuer on the
        // 2nd of December (a user that is gone); a draft discarded without a closing; and a closing of another organization.
        var company = CompanyOf("TEST Immobiliare Lago Srl", CompanyVat);
        var other = Guid.NewGuid();
        var (rejectedA, closedA) = ClosedNumber(rig.OrgId, 1, new DateOnly(2026, 11, 3), new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc), company, SelfBillingCloseReasons.PortalCreditNote, "=SUM(1+1)", "auth0|agency");
        var (rejectedC, closedC) = ClosedNumber(rig.OrgId, 2, new DateOnly(2026, 11, 5), new DateTime(2026, 11, 30, 23, 30, 0, DateTimeKind.Utc), company, SelfBillingCloseReasons.DuplicateOrError, "Duplicato della TD17 di ottobre, gia' emessa", "auth0|agency");
        var (rejectedB, closedB) = ClosedNumber(rig.OrgId, 1, new DateOnly(2026, 11, 4), new DateTime(2026, 12, 2, 22, 30, 0, DateTimeKind.Utc), CompanyOf("TEST Casa Monte Srl", SecondCompanyVat), SelfBillingCloseReasons.HolderNoLongerSubject, null, "auth0|ghost");
        var (rejectedX, closedX) = ClosedNumber(other, 9, new DateOnly(2026, 11, 3), new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc), CompanyOf("TEST Altra Societa Srl", "05555555555"), SelfBillingCloseReasons.PortalCreditNote, "TEST nota di un'altra agenzia", "auth0|altra");
        var dropped = SelfBilled(rig.OrgId, 3, 10, new DateOnly(2026, 11, 6), company, "TEST Portale B.V.", "NL", "NL805734958B01", 10.00m);
        (dropped.Status, dropped.SequenceNumber, dropped.IssueDate) = (FiscalDocumentStatus.Discarded, null, null);
        rig.Db.Users.Add(new User { Id = "auth0|agency", Email = "mario.rossi@example.com", FirstName = "Mario", LastName = "Rossi", Role = UserRole.PropertyManager });
        rig.Db.FiscalDocuments.AddRange(rejectedA, closedA, rejectedC, closedC, rejectedB, closedB, rejectedX, closedX, dropped);
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        var columns = new[]
        {
            "Emittente", "Numero", "Data del documento scartato", "Chiusa il", "Motivo della chiusura", "Nota", "Esito SDI del documento originale", "Codice di scarto SDI", "Chiusa da",
        };

        // November: the number closed on the 20th, with every column (the closing of the 1st of December in Rome is December's), the issuer told apart, the note as text, the outcome of the rejected original.
        var (_, november) = await BuildAsync(rig, AccountantPackageAccess.Full, November);
        var register = Blocks(november[unusedPath]);
        Assert.StartsWith("Numeri delle autofatture TD17 non utilizzati", register[0][0][0], StringComparison.Ordinal);
        Assert.Contains("01/11/2026", register[0][0][0], StringComparison.Ordinal);
        Assert.Contains("30/11/2026", register[0][0][0], StringComparison.Ordinal);
        Assert.Equal(columns, register[0][1]);
        Assert.Equal(
            new[] { "TEST Immobiliare Lago Srl (IT 09876543217)", "TD17-2026/1", "03/11/2026", "20/11/2026", "Nota di credito del portale", "'=SUM(1+1)", "Scartata", "00417", "Mario Rossi" },
            Assert.Single(register[0].Skip(2)));
        using (var workbook = new ZipArchive(new MemoryStream(november[unusedPath.Replace(".csv", ".xlsx", StringComparison.Ordinal)]), ZipArchiveMode.Read))
        using (var sheet = new StreamReader(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8))
        {
            var xml = await sheet.ReadToEndAsync();
            Assert.Contains("=SUM(1+1)", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
        }

        AssertTheIndex(november);

        // December: the number closed at 00:30 of the 1st (Rome) and the one of the other issuer, oldest closing first; a user that is gone is named by a fixed text, never by the id the closing recorded, and a closing without a note has an empty cell.
        var (_, december) = await BuildAsync(rig, AccountantPackageAccess.Full, AccountantPackagePeriod.OfMonth(2026, 12));
        var decemberRows = Blocks(december[unusedPath])[0].Skip(2).ToList();
        Assert.Equal(2, decemberRows.Count);
        Assert.Equal(
            new[] { "TEST Immobiliare Lago Srl (IT 09876543217)", "TD17-2026/2", "05/11/2026", "01/12/2026", "Duplicato o errore", "Duplicato della TD17 di ottobre, gia' emessa", "Scartata", "00417", "Mario Rossi" },
            decemberRows[0]);
        Assert.Equal(
            new[] { "TEST Casa Monte Srl (IT 01112223334)", "TD17-2026/1", "04/11/2026", "02/12/2026", "Titolare non più soggetto passivo", string.Empty, "Scartata", "00417", "utente non più presente" },
            decemberRows[1]);
        using (var workbook = new ZipArchive(new MemoryStream(december[unusedPath.Replace(".csv", ".xlsx", StringComparison.Ordinal)]), ZipArchiveMode.Read))
        using (var sheet = new StreamReader(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8))
        {
            Assert.Contains("utente non più presente", await sheet.ReadToEndAsync(), StringComparison.Ordinal);
        }

        AssertTheIndex(december);

        // A year has all three, oldest closing first; none of another organization or of a draft discarded without a closing is in any file.
        var (_, year) = await BuildAsync(rig, AccountantPackageAccess.Full, Year2026);
        Assert.Equal(new[] { "TD17-2026/1", "TD17-2026/2", "TD17-2026/1" }, Blocks(year[unusedPath])[0].Skip(2).Select(r => r[1]));
        Assert.Equal(new[] { "20/11/2026", "01/12/2026", "02/12/2026" }, Blocks(year[unusedPath])[0].Skip(2).Select(r => r[3]));
        foreach (var (path, content) in year.Where(f => f.Key.EndsWith(".csv", StringComparison.Ordinal)))
        {
            var text = Text(content);
            foreach (var foreign in new[] { "TD17-2026/9", "TEST Altra Societa", "05555555555", "TEST nota di un'altra agenzia", "auth0|altra" })
                Assert.False(text.Contains(foreign, StringComparison.Ordinal), $"{path} holds «{foreign}» of another organization");
        }

        // The id of a user is nowhere in the files, a user that is gone included (the fixed text is written instead): not in a CSV, not in any part of an Excel file. The id stays in the column of the database.
        foreach (var files in new[] { november, december, year })
            AssertNoUserIds(files, "auth0|ghost", "auth0|agency");

        // Owner accounting of the whole organization only: without the permission, or for a caller who reaches only some properties, there is no register (and none of its words in any file).
        var (_, withoutPermission) = await BuildAsync(rig, AccountantPackageAccess.WithoutOwnerAccounting, Year2026);
        var (_, restricted) = await BuildAsync(rig, AccountantPackageAccess.Full, Year2026, new HostScope(rig.OrgId, GrantedToUserId: "auth0|lago"));
        foreach (var files in new[] { withoutPermission, restricted })
        {
            Assert.DoesNotContain(files.Keys, k => k.Contains(AccountantPackageStructure.UnusedNumbersFile, StringComparison.Ordinal));
            foreach (var (path, content) in files.Where(f => f.Key.EndsWith(".csv", StringComparison.Ordinal) && f.Key != AccountantPackageStructure.IndexFile))
            {
                var text = Text(content);
                foreach (var word in new[] { "TD17-2026/2", "Mario Rossi", "Nota di credito del portale", "auth0|ghost" })
                    Assert.False(text.Contains(word, StringComparison.Ordinal), $"{path} holds «{word}», which is owner accounting");
            }
        }

        // A period without a closing is the package it always was: September has the same files with the same content as before the closings existed (no file, not a byte of the CSV changed).
        var (_, after) = await BuildAsync(rig, AccountantPackageAccess.Full, September);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var path in before.Keys.Where(k => k.EndsWith(".csv", StringComparison.Ordinal) && k != AccountantPackageStructure.IndexFile))
            Assert.Equal(before[path], after[path]);
    }

    // ---------------------------------------------------------------- the data

    /// <summary>
    /// A number closed as «non più dovuta»: the TD17 the SDI rejected (issued on <paramref name="issued"/>, discarded by its correction, its sequence number given up, rejected with the code 00417) and the replacement draft
    /// that holds its number, discarded with the reason, the note, the user and the day of the closing (<paramref name="closedAt"/>, UTC).
    /// </summary>
    private static (FiscalDocument Original, FiscalDocument Closed) ClosedNumber(
        Guid orgId, int sequence, DateOnly issued, DateTime closedAt, string issuerSnapshot, string reason, string? note, string? userId)
    {
        var original = SelfBilled(orgId, sequence, 10, issued, issuerSnapshot, "TEST Portale B.V.", "NL", "NL805734958B01", 67.50m);
        original.Status = FiscalDocumentStatus.Discarded;
        original.SequenceNumber = null;
        original.SdiStatus = FiscalDocumentSdiStatuses.Rejected;
        original.SdiErrorCode = "00417";
        var closed = SelfBilled(orgId, sequence, 10, issued, issuerSnapshot, "TEST Portale B.V.", "NL", "NL805734958B01", 67.50m);
        closed.Status = FiscalDocumentStatus.Discarded;
        closed.IssueDate = null;
        closed.IssuerSnapshot = null;
        closed.FatturaPaXml = null;
        closed.SdiStatus = FiscalDocumentSdiStatuses.NotApplicable;
        closed.ReplacesDocumentId = original.Id;
        closed.ClosedNotDueReason = reason;
        closed.ClosedNotDueNote = note;
        closed.ClosedNotDueAt = closedAt;
        closed.ClosedNotDueByUserId = userId;
        return (original, closed);
    }

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

    /// <summary>An invoice to a guest issued by an Italian issuer (no booking: the whole org reads it), 100.00 at 22% in November: the VAT file of its issuer exists whatever the caller may read.</summary>
    private static FiscalDocument GuestOf(Guid orgId, string issuerSnapshot)
    {
        var document = SelfBilled(orgId, 1, 10, new DateOnly(2026, 11, 2), issuerSnapshot, "TEST Ospite", "IT", "RSSMRA80A01H501U", 100.00m);
        document.Purpose = FiscalDocumentPurpose.GuestStay;
        document.Series = "FT2026";
        document.Number = "FT2026/1";
        document.PeriodYear = null;
        document.PeriodMonth = null;
        document.RecipientKind = InvoiceRecipientKind.Guest;
        document.RecipientVatNumber = null;
        document.PaymentChannel = FiscalPaymentChannel.CasaZen;
        document.PaymentState = FiscalDocumentPaymentState.Paid;
        document.FatturaPaXml = null;
        foreach (var line in document.Lines)
            line.LineType = FiscalLineType.Accommodation;

        return document;
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

    /// <summary>No CSV file, and no part of an Excel file, holds the id of a user: the package names who closed a number by name, or by a fixed text when the user is gone.</summary>
    private static void AssertNoUserIds(Dictionary<string, byte[]> files, params string[] ids)
    {
        foreach (var (path, content) in files.Where(f => f.Key.EndsWith(".csv", StringComparison.Ordinal) || f.Key.EndsWith(".xlsx", StringComparison.Ordinal)))
        {
            var texts = new List<string>();
            if (path.EndsWith(".xlsx", StringComparison.Ordinal))
            {
                using var workbook = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
                foreach (var entry in workbook.Entries)
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    texts.Add(reader.ReadToEnd());
                }
            }
            else
            {
                texts.Add(Text(content));
            }

            foreach (var id in ids)
                Assert.False(texts.Any(text => text.Contains(id, StringComparison.Ordinal)), $"{path} holds the id of a user ({id})");
        }
    }

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
