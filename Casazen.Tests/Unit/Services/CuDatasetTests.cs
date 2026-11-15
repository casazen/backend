using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E18/F01/US02/T01 (NF-117, ⚖ I6, T2): the annual Certificazione Unica data per recipient, its exports and the owner's view of its own withholding. The payouts are made by the real
/// services (<see cref="OwnerPayoutFactsTests.FactsWorld"/>: the committed Italian rule, 21%, code 1919, the gross without the tourist tax). Everything is invented (TEST owners, @example.com).
/// </summary>
public class CuDatasetTests
{
    /// <summary>The fiscal code of the first owner (invented, as the profile of the world gives it).</summary>
    private const string FirstFiscalCode = "RSSMRA70A01H501W";

    /// <summary>The fiscal code of the second owner (invented).</summary>
    private const string SecondFiscalCode = "VRDLGU80B02F205Z";

    /// <summary>
    /// Money (⚖ I6): a house of two co-owners (60% and 40%, a stay of 1000 paid to the agency) is reported on the signatory (the largest share, its mandate in force) with the shares of the
    /// co-owners, by default; when the mandate of the signatory says per share each co-owner has its own record. Either way the shares add up to what the signatory has (1000 base, 210
    /// withheld), the lines of each record add up to it, and the totals of the annual data are the evidence's, so the F24 of the month and the reconciliation of the year say the same euro.
    /// </summary>
    [Fact]
    public async Task CuDataset_SharesSumToOwnerTotals_Test()
    {
        var world = new OwnerPayoutFactsTests.FactsWorld();
        var (first, second) = await CoOwnedPaidAsync(world);

        // The default: everything on the signatory, who says what each co-owner puts into it.
        var built = await world.Payouts.GenerateAnnualDatasetAsync(world.OrgId, 2026, "auth0|admin");
        var onSignatory = Assert.Single(CuDatasetPayload.Parse(built.PayloadJson));
        Assert.Equal((first, 1000m, 210m, CuAttributions.Signatory, 2), (onSignatory.OwnerId, onSignatory.GrossBase, onSignatory.Withheld, onSignatory.Attribution, onSignatory.Evidence));
        Assert.Equal(
            [(first, 600m, 126m), (second, 400m, 84m)],
            onSignatory.Shares.OrderByDescending(s => s.GrossBase).Select(s => (s.OwnerId, s.GrossBase, s.Withheld)));
        Assert.Equal((onSignatory.GrossBase, onSignatory.Withheld), (onSignatory.Shares.Sum(s => s.GrossBase), onSignatory.Shares.Sum(s => s.Withheld)));
        Assert.Equal((onSignatory.GrossBase, onSignatory.Withheld), (onSignatory.Lines.Sum(l => l.GrossBase), onSignatory.Lines.Sum(l => l.Withheld)));

        // The mandate of the signatory says per share: each co-owner keeps its share, and together they are what the signatory had.
        var mandate = await world.Db.OwnerMandates.SingleAsync(m => m.PropertyOwnerId == first);
        mandate.CoOwnerTaxReporting = CoOwnerTaxReporting.PerShare;
        await world.Db.SaveChangesAsync();
        var regenerated = await world.Payouts.GenerateAnnualDatasetAsync(world.OrgId, 2026, "auth0|admin");
        var perShare = CuDatasetPayload.Parse(regenerated.PayloadJson).OrderByDescending(r => r.GrossBase).ToList();

        Assert.Equal([(first, 600m, 126m), (second, 400m, 84m)], perShare.Select(r => (r.OwnerId, r.GrossBase, r.Withheld)));
        Assert.All(perShare, r => Assert.Equal((CuAttributions.Share, 1, 0), (r.Attribution, r.Evidence, r.Shares.Count)));
        Assert.Equal((onSignatory.GrossBase, onSignatory.Withheld), (perShare.Sum(r => r.GrossBase), perShare.Sum(r => r.Withheld)));
        Assert.All(perShare, r => Assert.Equal((r.GrossBase, r.Withheld), (r.Lines.Sum(l => l.GrossBase), r.Lines.Sum(l => l.Withheld))));

        // The totals of the data are the evidence's, so the F24 of the month and the reconciliation of the year agree (nothing was lost or added by the attribution).
        Assert.Equal((1000m, 210m, 2), (regenerated.TotalGrossBase, regenerated.TotalWithheld, regenerated.EvidenceCount));
        Assert.Equal(210m, (await world.Withholding.GenerateF24Async(world.OrgId, 11, 2026)).TotalAmount);
        Assert.True((await world.Payouts.ReconcileAsync(world.OrgId, 2026)).Reconciled);

        // The shares of a payout are split to the cent: 21% of 600.06 is 126.01, and the three stays carry 126.01 between them, never 126.00 or 126.02.
        var owner = await world.OwnerAsync();
        var house = await world.PropertyAsync(owner);
        await world.StayAsync(house, BookingSource.Manual, 100.01m, paid: true);
        await world.StayAsync(house, BookingSource.Manual, 200.02m, paid: true);
        await world.StayAsync(house, BookingSource.Manual, 300.03m, paid: true);
        var version = await world.ApprovedVersionAsync(owner);
        var payout = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-split"), null);
        var evidence = Assert.Single(payout.Evidence);
        Assert.Equal((600.06m, 126.01m), (evidence.GrossBase!.Value, evidence.Amount!.Value));
        var pieces = TaxEvidenceLines.Of(evidence);
        Assert.Equal(3, pieces.Count);
        Assert.Equal((600.06m, 126.01m), (pieces.Sum(p => p.GrossBase), pieces.Sum(p => p.Withheld)));
        Assert.All(pieces, p => Assert.InRange(Math.Abs(p.Withheld - (p.GrossBase * 0.21m)), 0m, 0.01m));
    }

    /// <summary>The split of a withholding between stays: always the whole, to the cent, the leftover cents on the largest remainders, the first stay among equals.</summary>
    [Theory]
    [InlineData(100, new long[] { 1, 1, 1 }, new long[] { 34, 33, 33 })]
    [InlineData(12601, new long[] { 10001, 20002, 30003 }, new long[] { 2100, 4200, 6301 })]
    [InlineData(0, new long[] { 5, 7 }, new long[] { 0, 0 })]
    [InlineData(5, new long[] { 0, 0 }, new long[] { 5, 0 })]
    [InlineData(7, new long[] { 1 }, new long[] { 7 })]
    public void EvidenceLines_Split_AddsUpToTheWholeToTheCent(long total, long[] weights, long[] expected)
    {
        var parts = TaxEvidenceLines.Split(total, weights);

        Assert.Equal(expected, parts);
        Assert.Equal(total, parts.Sum());
    }

    /// <summary>
    /// Privacy: the payload of the annual data is encrypted at rest (it names the fiscal code and the address of every recipient): the stored text is a Data Protection payload that holds
    /// none of them, the model reads it back, the column is on the one list of the encrypted columns, a payload stored in clear before (the totals of each owner) is still read, and the API of the
    /// annual data answers the totals of each owner without the recipients. The shape the accountant package reads (<c>ownerId</c>, <c>grossBase</c>, <c>withheld</c>, <c>evidence</c>) is kept.
    /// </summary>
    [Fact]
    public async Task CuDataset_PayloadEncrypted_Test()
    {
        var world = new OwnerPayoutFactsTests.FactsWorld();
        var owner = await world.OwnerAsync();
        var house = await world.PropertyAsync(owner);
        var registry = await world.Db.PropertyOwners.SingleAsync();
        (registry.BillingStreet, registry.BillingCivic, registry.BillingPostalCode, registry.BillingCity, registry.BillingProvince, registry.BillingCountry) =
            ("Via del Lago Maggiore", "7", "22012", "Cernobbio", "CO", "IT");
        await world.StayAsync(house, BookingSource.Manual, 1000m, paid: true);
        var version = await world.ApprovedVersionAsync(owner);
        await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-privacy"), null);

        var dataset = await world.Payouts.GenerateAnnualDatasetAsync(world.OrgId, 2026, "auth0|admin");

        // The column is declared encrypted, with its own purpose, and the model of the test has its converter.
        Assert.Contains(
            EncryptedColumns.All,
            c => c.EntityType == typeof(TaxReportingDataset) && c.Property == nameof(TaxReportingDataset.PayloadJson) && c.Purpose == EncryptedColumns.TaxReportingDatasetPurpose);
        Assert.True(EncryptedColumns.IsConfigured(world.Db));

        // What the column holds (the EF InMemory database keeps the values the model hands it, so the proof is on the converter of the model, as for the other encrypted columns): a payload, none of the
        // recipient's data in it, never the clear JSON, read back only through the decryption of its own purpose.
        var provider = new EphemeralDataProtectionProvider();
        await using var keyed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenantContext: null, provider);
        var column = keyed.Model.FindEntityType(typeof(TaxReportingDataset))!.FindProperty(nameof(TaxReportingDataset.PayloadJson))!;
        var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());
        Assert.Null(column.GetMaxLength());
        var raw = (string)converter.ConvertToProvider(dataset.PayloadJson)!;
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, raw, StringComparison.Ordinal);
        Assert.NotEqual(dataset.PayloadJson, raw);
        Assert.DoesNotContain(FirstFiscalCode, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("TEST Proprietario", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Via del Lago Maggiore", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Cernobbio", raw, StringComparison.Ordinal);
        Assert.Equal(dataset.PayloadJson, converter.ConvertFromProvider(raw));
        Assert.ThrowsAny<CryptographicException>(() => provider.CreateProtector(EncryptedColumns.GuestDocumentPurpose).Unprotect(raw));

        // The recipient is in the clear payload the model hands the service: with its fiscal code, its address, the property and the stay.
        var read = await world.Withholding.GetCuDatasetAsync(world.OrgId, 2026);
        var record = Assert.Single(read.Records);
        var recipient = record.Recipient!;
        Assert.Equal(("TEST Proprietario", FirstFiscalCode, "Via del Lago Maggiore", "7", "22012", "Cernobbio"), (recipient.Name, recipient.FiscalCode, recipient.Street, recipient.Civic, recipient.PostalCode, recipient.City));
        Assert.Empty(recipient.Missing);
        var line = Assert.Single(record.Lines);
        Assert.Equal((new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12), 2, 1000m, 210m), (line.CheckIn, line.CheckOut, line.Nights, line.GrossBase, line.Withheld));
        Assert.Equal("TEST Casa", Assert.Single(record.Properties).Name);

        // The API of the annual data answers the totals of each owner and no recipient; the shape the accountant package reads is kept.
        var api = TaxReportingDatasetDto.From(read.Dataset);
        Assert.DoesNotContain(FirstFiscalCode, api.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Cernobbio", api.PayloadJson, StringComparison.Ordinal);
        var legacyShape = JsonSerializer.Deserialize<List<LegacyOwner>>(api.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal((owner, 1000m, 210m, 1), (legacyShape[0].OwnerId, legacyShape[0].GrossBase, legacyShape[0].Withheld, legacyShape[0].Evidence));
        var fullShape = JsonSerializer.Deserialize<List<LegacyOwner>>(read.Dataset.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal((owner, 1000m, 210m, 1), (fullShape[0].OwnerId, fullShape[0].GrossBase, fullShape[0].Withheld, fullShape[0].Evidence));

        // A payload stored in clear before the column was encrypted (the totals of each owner) is read as it is by the converter; its records name no recipient, so nothing is exported from it.
        var clear = JsonSerializer.Serialize(new[] { new { ownerId = owner, grossBase = 1000m, withheld = 210m, evidence = 1 } });
        Assert.Equal(clear, converter.ConvertFromProvider(clear));
        (await world.Db.TaxReportingDatasets.SingleAsync()).PayloadJson = clear;
        await world.Db.SaveChangesAsync();
        var outdated = await Assert.ThrowsAsync<DomainRuleException>(() => world.Withholding.GetCuDatasetAsync(world.OrgId, 2026));
        Assert.Equal(TaxReportingErrors.CuDatasetOutdated, outdated.Code);
        var totals = Assert.Single(CuDatasetPayload.Parse(clear));
        Assert.Equal((owner, 1000m, 210m, 1), (totals.OwnerId, totals.GrossBase, totals.Withheld, totals.Evidence));
        Assert.Null(totals.Recipient);
    }

    /// <summary>
    /// Privacy: the owner sees its own withholding and its own Certificazione Unica and nothing else. Only the owner profiles of the claim are read (another owner's payouts, stays and CU are
    /// not there), a person with no ownership has nothing, a delegated contact needs <c>accounting.read</c>, the CU is shown once the agency reviewed it (never a draft) and only as totals (no
    /// recipient data), and with a flag off the answer is the empty summary it was. The totals are the evidence's, the details add up to them.
    /// </summary>
    [Fact]
    public async Task OwnerPortalWithholding_OnlyOwnClaim_Test()
    {
        var world = new OwnerPayoutFactsTests.FactsWorld();
        world.Db.Orgs.Add(new OrgEntity { Id = world.OrgId, Name = "TEST Agenzia", DisplayName = "TEST Agenzia", Slug = "test-agenzia", PlanTier = PlanTier.Agency });
        var (anna, annaUser) = await PaidOwnerWithUserAsync(world, "Anna", 1000m, "k-anna", FirstFiscalCode);
        var (bruno, brunoUser) = await PaidOwnerWithUserAsync(world, "Bruno", 500m, "k-bruno", SecondFiscalCode);
        var flags = new TestFeatureFlags(FeatureFlags.AgencyTier, FeatureFlags.TaxReportingExports);
        var portal = new OwnerPortalService(world.Db, world.Clock, null, flags, new OwnerPortalAccessResolver(world.Db, world.Clock));

        // Its own withholding only, in euro, with the property and the month; the details add up to the total.
        var mine = await portal.GetOwnerWithholdingAsync(annaUser, 2026);
        Assert.Equal((2026, "EUR", 210m), (mine.Year, mine.Currency, mine.TotalWithheld));
        var detail = Assert.Single(mine.Details);
        Assert.Equal(("TEST Agenzia", "TEST Casa di Anna", 11, 1000m, 210m), (detail.AgencyName, detail.PropertyName, detail.Month, detail.GrossBase, detail.AmountWithheld));
        Assert.Equal(mine.TotalWithheld, mine.Details.Sum(d => d.AmountWithheld));
        var theirs = await portal.GetOwnerWithholdingAsync(brunoUser, 2026);
        Assert.Equal((105m, "TEST Casa di Bruno"), (theirs.TotalWithheld, Assert.Single(theirs.Details).PropertyName));

        // A year with nothing, a person with no ownership and a stranger have nothing: an empty summary, not an error.
        Assert.Empty((await portal.GetOwnerWithholdingAsync(annaUser, 2025)).Details);
        var nobody = await portal.GetOwnerWithholdingAsync(Guid.NewGuid(), 2026);
        Assert.Equal((0m, 0, 0), (nobody.TotalWithheld, nobody.Details.Count, nobody.CuStatements.Count));

        // The CU: not shown while the data is only generated (it can be built again), shown to its owner once reviewed, as totals, and never the other owner's or any recipient data.
        var dataset = await world.Payouts.GenerateAnnualDatasetAsync(world.OrgId, 2026, "auth0|admin");
        Assert.Empty((await portal.GetOwnerWithholdingAsync(annaUser, 2026)).CuStatements);
        await world.Payouts.SetDatasetFilingStateAsync(world.OrgId, dataset.Id, new TaxFilingCommand { State = TaxFilingState.Reviewed }, "auth0|admin");
        var reviewed = await portal.GetOwnerWithholdingAsync(annaUser, 2026);
        var cu = Assert.Single(reviewed.CuStatements);
        Assert.Equal(("TEST Agenzia", 2026, TaxFilingState.Reviewed, 1000m, 210m, "1919", CuAttributions.Owner), (cu.AgencyName, cu.Year, cu.State, cu.GrossBase, cu.Withheld, cu.TaxCode, cu.Attribution));
        var brunoCu = Assert.Single((await portal.GetOwnerWithholdingAsync(brunoUser, 2026)).CuStatements);
        Assert.Equal((500m, 105m), (brunoCu.GrossBase, brunoCu.Withheld));
        var answer = JsonSerializer.Serialize(reviewed);
        foreach (var other in new[] { "TEST Casa di Bruno", SecondFiscalCode, FirstFiscalCode, bruno.ToString(), anna.ToString(), "Via del Test" })
            Assert.DoesNotContain(other, answer, StringComparison.Ordinal);

        // A delegated contact of Anna needs accounting.read: without it 403, with it the same as Anna and nothing of Bruno.
        var without = await ContactOfAsync(world, anna, OwnerPortalPermission.DocumentsRead);
        var refusal = await Assert.ThrowsAsync<DomainForbiddenException>(() => portal.GetOwnerWithholdingAsync(without, 2026));
        Assert.Equal(OwnerPortalErrors.PermissionMissing, refusal.Code);
        var with = await ContactOfAsync(world, anna, OwnerPortalPermission.AccountingRead);
        Assert.Equal(210m, (await portal.GetOwnerWithholdingAsync(with, 2026)).TotalWithheld);

        // With either flag off the summary is the empty one it was, whoever asks.
        foreach (var off in new[] { FeatureFlags.TaxReportingExports, FeatureFlags.AgencyTier })
        {
            flags.Set(off, false);
            var empty = await portal.GetOwnerWithholdingAsync(annaUser, 2026);
            Assert.Equal((0m, 0, 0), (empty.TotalWithheld, empty.Details.Count, empty.CuStatements.Count));
            flags.Set(off, true);
        }
    }

    /// <summary>
    /// Data: the files of the annual data carry the recipient (name, fiscal code, address), the property (address, CIN, cadastral data), the stay and the amounts, the same in the CSV and in the Excel
    /// file; the draft PDF of an owner is a PDF with its watermark; an owner with no record, or of another org, is a 404; the files name what is missing of a recipient.
    /// </summary>
    [Fact]
    public async Task CuExports_CarryTheRecipientTheStaysAndTheAmounts_Test()
    {
        var world = new OwnerPayoutFactsTests.FactsWorld();
        var (first, second) = await CoOwnedPaidAsync(world);

        // Per share, so both co-owners have a record, and the second one has no fiscal code.
        (await world.Db.OwnerFiscalProfiles.SingleAsync(p => p.PropertyOwnerId == second)).FiscalCode = null;
        (await world.Db.OwnerMandates.SingleAsync(m => m.PropertyOwnerId == first)).CoOwnerTaxReporting = CoOwnerTaxReporting.PerShare;
        await world.Db.SaveChangesAsync();
        await world.Payouts.GenerateAnnualDatasetAsync(world.OrgId, 2026, "auth0|admin");
        var stored = await world.Withholding.GetCuDatasetAsync(world.OrgId, 2026);
        var texts = new ResxDocumentTexts();

        var csv = Encoding.UTF8.GetString(TaxReportingExports.CuCsv(2026, stored.Records, texts, DocumentLanguages.Italian));
        Assert.Contains(FirstFiscalCode, csv, StringComparison.Ordinal);
        Assert.Contains("TEST Casa in comproprietà", csv, StringComparison.Ordinal);
        Assert.Contains("Via Condivisa 2", csv, StringComparison.Ordinal);
        Assert.Contains("IT013075C27G5FFZDZ", csv, StringComparison.Ordinal);
        Assert.Contains("A/2", csv, StringComparison.Ordinal);
        Assert.Contains("600,00;126,00;1919", csv, StringComparison.Ordinal);
        Assert.Contains("400,00;84,00;1919", csv, StringComparison.Ordinal);
        Assert.Contains("10/10/2026;12/10/2026;2", csv, StringComparison.Ordinal);
        Assert.Contains("codice fiscale", csv, StringComparison.Ordinal);
        Assert.Equal("cu-2026.csv", TaxReportingExports.CuFileName(2026, "csv"));

        using var workbook = new ZipArchive(new MemoryStream(TaxReportingExports.CuXlsx(2026, stored.Records, texts, DocumentLanguages.English)), ZipArchiveMode.Read);
        using var recipients = new StreamReader(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = await recipients.ReadToEndAsync();
        Assert.Contains("<v>600.00</v>", sheet, StringComparison.Ordinal);
        Assert.Contains("<v>126.00</v>", sheet, StringComparison.Ordinal);
        Assert.NotNull(workbook.GetEntry("xl/worksheets/sheet2.xml"));

        // The draft PDF of an owner; nothing for an owner with no record (a signatory's co-owner without one, another org's owner, a stranger).
        var export = new StatementExportService(world.Db, new MigraDocPdfDocumentRenderer(), texts, Microsoft.Extensions.Logging.Abstractions.NullLogger<StatementExportService>.Instance);
        var pdf = await export.ExportCuProspectAsync(world.OrgId, 2026, first, "it");
        Assert.Equal(("application/pdf", $"prospetto-cu-2026-{first:N}.pdf", "%PDF"), (pdf.ContentType, pdf.FileName, Encoding.ASCII.GetString(pdf.Content, 0, 4)));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => export.ExportCuProspectAsync(world.OrgId, 2026, Guid.NewGuid(), "it"));
        Assert.Equal(TaxReportingErrors.CuRecordNotFound, missing.Code);
        await Assert.ThrowsAsync<NotFoundException>(() => export.ExportCuProspectAsync(Guid.NewGuid(), 2026, first, "it"));
        var noData = await Assert.ThrowsAsync<NotFoundException>(() => export.ExportCuProspectAsync(world.OrgId, 2025, first, "it"));
        Assert.Equal(OwnerPayoutErrors.DatasetNotFound, noData.Code);

        // The second co-owner's record says its fiscal code is missing, in the language of the file.
        var secondRecord = stored.Records.Single(r => r.OwnerId == second);
        Assert.Equal([TaxReportingPeople.Missing.FiscalCode], secondRecord.Recipient!.Missing);
        Assert.Contains("fiscal code", Encoding.UTF8.GetString(TaxReportingExports.CuCsv(2026, stored.Records, texts, DocumentLanguages.English)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A house of two co-owners, <paramref name="shares"/> of the first and the rest of the second, a stay of 1000 paid to the agency, both statements approved and both payouts made on 5
    /// November: 600 and 400 of base, 126 and 84 withheld.
    /// </summary>
    private static async Task<(Guid First, Guid Second)> CoOwnedPaidAsync(OwnerPayoutFactsTests.FactsWorld world)
    {
        var first = await world.OwnerAsync();
        var second = await world.OwnerAsync();
        (await world.Db.OwnerFiscalProfiles.SingleAsync(p => p.PropertyOwnerId == second)).FiscalCode = SecondFiscalCode;
        var house = new Property
        {
            OrgId = world.OrgId,
            OwnerId = "auth0|agency",
            Name = "TEST Casa in comproprietà",
            Address = "Via Condivisa 2",
            City = "Como",
            PostalCode = "22100",
            Country = "IT",
            Currency = "EUR",
            CinCode = "IT013075C27G5FFZDZ",
            CadastralSheet = "12",
            CadastralParcel = "345",
            CadastralSubaltern = "6",
            CadastralCategory = "A/2",
        };
        world.Db.Properties.Add(house);
        world.Db.PropertyOwnerships.Add(Ownership(world, house.Id, first, 60m));
        world.Db.PropertyOwnerships.Add(Ownership(world, house.Id, second, 40m));
        await world.Db.SaveChangesAsync();

        await world.StayAsync(house.Id, BookingSource.Manual, 1000m, paid: true);
        var firstVersion = await world.ApprovedVersionAsync(first);
        var secondVersion = await world.ApprovedVersionAsync(second);
        await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(first, firstVersion, "k-first"), null);
        await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(second, secondVersion, "k-second"), null);
        return (first, second);
    }

    private static PropertyOwnership Ownership(OwnerPayoutFactsTests.FactsWorld world, Guid property, Guid owner, decimal share) => new()
    {
        OrgId = world.OrgId,
        PropertyId = property,
        PropertyOwnerId = owner,
        SharePercent = share,
        OwnershipStart = new DateTime(2026, 1, 1),
    };

    /// <summary>An owner with a house named after it, a person of the portal that holds its ownership, one stay worth <paramref name="gross"/> and its payout of 5 November.</summary>
    private static async Task<(Guid Owner, Guid User)> PaidOwnerWithUserAsync(OwnerPayoutFactsTests.FactsWorld world, string name, decimal gross, string key, string fiscalCode)
    {
        var owner = await world.OwnerAsync();
        (await world.Db.OwnerFiscalProfiles.SingleAsync(p => p.PropertyOwnerId == owner)).FiscalCode = fiscalCode;
        var house = await world.PropertyAsync(owner);
        (await world.Db.Properties.SingleAsync(p => p.Id == house)).Name = "TEST Casa di " + name;
        var user = Guid.NewGuid();
        world.Db.OwnerUsers.Add(new OwnerUser { Id = user, Auth0Sub = $"auth0|{user:N}", Email = $"{user:N}@example.com", FirstName = "TEST", LastName = name });
        (await world.Db.PropertyOwnerships.SingleAsync(o => o.PropertyOwnerId == owner)).OwnerUserId = user;
        await world.Db.SaveChangesAsync();

        await world.StayAsync(house, BookingSource.Manual, gross, paid: true);
        var version = await world.ApprovedVersionAsync(owner);
        await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, key), null);
        return (owner, user);
    }

    /// <summary>A person the owner delegated the portal to, with <paramref name="permissions"/> only.</summary>
    private static async Task<Guid> ContactOfAsync(OwnerPayoutFactsTests.FactsWorld world, Guid owner, OwnerPortalPermission permissions)
    {
        var user = Guid.NewGuid();
        var now = world.Clock.GetUtcNow().UtcDateTime;
        world.Db.OwnerPortalContacts.Add(new OwnerPortalContact
        {
            OrgId = world.OrgId,
            PropertyOwnerId = owner,
            Email = $"{user:N}@example.com",
            FirstName = "TEST",
            LastName = "Contatto",
            EmailHash = new string('a', 64),
            TokenHash = new string('b', 64),
            Permissions = permissions,
            Status = OwnerPortalContactStatus.Active,
            OwnerUserId = user,
            InvitationExpiresAt = now.AddDays(30),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await world.Db.SaveChangesAsync();
        return user;
    }

    /// <summary>The shape of the annual data the accountant package reads (<c>AccountantPackageService.ReadCu</c>).</summary>
    private sealed record LegacyOwner(Guid OwnerId, decimal GrossBase, decimal Withheld, int Evidence);
}
