using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Fiscal reports for the accountant (CO-19, A5-23): totals from known payments, tax estimated only for the cedolare
/// secca, withholding detail, tourist tax per comune and month from the amounts recorded by BK-03, Italian A4 PDF with
/// tables over several pages, Italian CSV; partial tax profile update and regimes available per property.
/// </summary>
public class FiscalReportsTests
{
    private const int TaxYear = 2026;
    private const string TaxpayerX = "RSSMRA80A01H501U";
    private const string TaxpayerY = "VRDLGU75B12F205X";

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- summary

    [Fact]
    public async Task GetAnnualReport_KnownPayments_TotalsAndTaxEstimatedOnlyForCedolare()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var a = SeedProperty(db, orgId, "A Cedolare 21", taxpayer: TaxpayerX);
        var b = SeedProperty(db, orgId, "B Cedolare 26", taxpayer: TaxpayerX);
        var c = SeedProperty(db, orgId, "C IRPEF", taxpayer: TaxpayerY);
        var d = SeedProperty(db, orgId, "D Senza regime", taxpayer: TaxpayerY);
        SeedRegime(db, a, StrFiscalRegime.CedolareSecca21);
        SeedRegime(db, b, StrFiscalRegime.CedolareSecca26);
        SeedRegime(db, c, StrFiscalRegime.IrpefOrdinaria);

        // A: direct booking, tourist tax 100 included in the 1.100 paid.
        var stayA = SeedStay(db, a, Day(2026, 3, 1), nights: 3, totalPrice: 1100m, touristTax: 100m);
        SeedPayment(db, stayA, 1100m, Instant(2026, 3, 1, 10));
        // B: OTA payouts with withholding, one partially refunded (500 - 100 = 400, withholding 105 -> 84).
        var stayB = SeedStay(db, b, Day(2026, 4, 1), nights: 5, source: BookingSource.Airbnb);
        SeedPayment(db, stayB, 1000m, Instant(2026, 4, 10, 10), withholding: 210m);
        SeedPayment(db, stayB, 500m, Instant(2026, 4, 11, 10), withholding: 105m, refunded: 100m, status: PaymentStatus.PartiallyRefunded);
        SeedPayment(db, stayB, 999m, Instant(2026, 4, 12, 10), status: PaymentStatus.Pending);
        var stayC = SeedStay(db, c, Day(2026, 5, 1), nights: 2);
        SeedPayment(db, stayC, 300m, Instant(2026, 5, 1, 10));
        var stayD = SeedStay(db, d, Day(2026, 6, 1), nights: 2);
        SeedPayment(db, stayD, 200m, Instant(2026, 6, 1, 10));
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var report = await sut.GetAnnualReportAsync(new HostScope(orgId, null), TaxYear);

        var lineA = Assert.Single(report.Properties, l => l.PropertyId == a.Id);
        Assert.Equal(1100m, lineA.GrossIncome);
        Assert.Equal(100m, lineA.TouristTax);
        Assert.Equal(1000m, lineA.RentalIncome);
        Assert.Equal(1000m, lineA.TaxableIncome);
        Assert.Equal(0.21m, lineA.TaxRate);
        Assert.Equal(210m, lineA.EstimatedTax);
        Assert.Null(lineA.Commissions);
        Assert.Null(lineA.TaxNote);

        var lineB = Assert.Single(report.Properties, l => l.PropertyId == b.Id);
        Assert.Equal(1400m, lineB.GrossIncome);
        Assert.Equal(294m, lineB.Withholding);
        Assert.Equal(1106m, lineB.Net);
        Assert.Equal(0m, lineB.TouristTax);
        Assert.Equal(0.26m, lineB.TaxRate);
        Assert.Equal(364m, lineB.EstimatedTax);

        var lineC = Assert.Single(report.Properties, l => l.PropertyId == c.Id);
        Assert.Equal(300m, lineC.RentalIncome);
        Assert.Null(lineC.TaxableIncome);
        Assert.Null(lineC.EstimatedTax);
        Assert.Equal(FiscalTaxNotes.IrpefOrdinariaNotComputed, lineC.TaxNote);

        var lineD = Assert.Single(report.Properties, l => l.PropertyId == d.Id);
        Assert.Null(lineD.EstimatedTax);
        Assert.Equal(FiscalTaxNotes.RegimeNotAssigned, lineD.TaxNote);

        Assert.Equal(3000m, report.Totals.GrossIncome);
        Assert.Equal(100m, report.Totals.TouristTax);
        Assert.Equal(2900m, report.Totals.RentalIncome);
        Assert.Equal(294m, report.Totals.Withholding);
        Assert.Equal(2706m, report.Totals.Net);
        Assert.Equal(2400m, report.Totals.TaxableIncome);
        Assert.Equal(574m, report.Totals.EstimatedTax);
        Assert.Equal(2, report.Totals.LinesWithoutEstimate);

        Assert.Equal(2, report.Taxpayers.Count);
        var x = Assert.Single(report.Taxpayers, t => t.Index == lineA.TaxpayerIndex);
        Assert.Equal(2, x.Properties);
        Assert.Equal(2400m, x.RentalIncome);
        Assert.Equal(574m, x.EstimatedTax);
        Assert.EndsWith("501U", x.FiscalCodeMasked, StringComparison.Ordinal);
        Assert.NotEqual(TaxpayerX, x.FiscalCodeMasked);

        Assert.Equal(FiscalReportPeriod.WholeYear(TaxYear), report.Period);
        Assert.Equal(new DateOnly(2026, 9, 25), report.GeneratedOn);
        Assert.Equal("art. 1 c. 63 L. 213/2023", report.Rules.CedolareSource);
    }

    [Fact]
    public async Task GetAnnualReport_TaxpayerOverThreshold_NoEstimateEvenWithCedolare()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        foreach (var name in new[] { "Uno", "Due", "Tre" })
        {
            var property = SeedProperty(db, orgId, name, taxpayer: TaxpayerX);
            SeedRegime(db, property, StrFiscalRegime.CedolareSecca26);
            var stay = SeedStay(db, property, Day(2026, 2, 1), nights: 2);
            SeedPayment(db, stay, 500m, Instant(2026, 2, 1, 10));
        }

        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var report = await sut.GetAnnualReportAsync(new HostScope(orgId, null), TaxYear);

        Assert.All(report.Properties, l =>
        {
            Assert.Null(l.EstimatedTax);
            Assert.Equal(FiscalTaxNotes.ThresholdExceeded, l.TaxNote);
        });
        Assert.True(Assert.Single(report.Taxpayers).ThresholdExceeded);
        Assert.Equal(0m, report.Totals.EstimatedTax);
    }

    [Fact]
    public async Task GetAnnualReport_ImpresaRegime_NotEstimated()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, hasPartitaIva: true);
        var property = SeedProperty(db, orgId, "Impresa");
        SeedRegime(db, property, StrFiscalRegime.RegimeForfettario);
        SeedPayment(db, SeedStay(db, property, Day(2026, 2, 1), nights: 2), 800m, Instant(2026, 2, 1, 10));
        await db.SaveChangesAsync();

        var report = await CreateService(db).GetAnnualReportAsync(new HostScope(orgId, null), TaxYear);

        var line = Assert.Single(report.Properties);
        Assert.Equal(800m, line.GrossIncome);
        Assert.Null(line.EstimatedTax);
        Assert.Equal(FiscalTaxNotes.ImpresaNotComputed, line.TaxNote);
    }

    [Fact]
    public async Task GetAnnualReport_Quarter_UsesThePaymentDateInRome()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Trimestre");
        SeedRegime(db, property, StrFiscalRegime.CedolareSecca21);
        var stay = SeedStay(db, property, Day(2026, 3, 30), nights: 3);
        // 21:30 UTC of 31 March is 23:30 in Rome (Q1); 22:30 UTC is 00:30 of 1 April in Rome (Q2).
        SeedPayment(db, stay, 100m, new DateTime(2026, 3, 31, 21, 30, 0, DateTimeKind.Utc));
        SeedPayment(db, stay, 40m, new DateTime(2026, 3, 31, 22, 30, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();
        var sut = CreateService(db);
        var scope = new HostScope(orgId, null);

        var q1 = await sut.GetAnnualReportAsync(scope, TaxYear, new FiscalReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)));
        var q2 = await sut.GetAnnualReportAsync(scope, TaxYear, new FiscalReportPeriod(new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)));

        Assert.Equal(100m, q1.Totals.GrossIncome);
        Assert.Equal(21m, q1.Totals.EstimatedTax);
        Assert.Equal(40m, q2.Totals.GrossIncome);
    }

    [Theory]
    [InlineData("2026-03-01", "2026-02-01")]
    [InlineData("2025-12-01", "2026-01-31")]
    [InlineData("2026-12-01", "2027-01-31")]
    public async Task GetAnnualReport_PeriodInvalidOrOutsideTheTaxYear_ThrowsValidation(string from, string to)
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var ex = await Assert.ThrowsAsync<FiscalValidationException>(() => sut.GetAnnualReportAsync(
            new HostScope(orgId, null), TaxYear, new FiscalReportPeriod(DateOnly.Parse(from), DateOnly.Parse(to))));

        Assert.Equal("fiscal_report_period_invalid", ex.Code);
        Assert.Equal("FiscalReportPeriodInvalid", ex.MessageKey);
    }

    [Fact]
    public async Task GetReports_OwnerScope_OnlyTheCallersProperties()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var mine = SeedProperty(db, orgId, "Mia", ownerId: "auth0|me");
        var other = SeedProperty(db, orgId, "Altrui", ownerId: "auth0|other");
        SeedPayment(db, SeedStay(db, mine, Day(2026, 5, 1), nights: 2, touristTax: 10m, totalPrice: 210m), 210m, Instant(2026, 5, 1, 10), withholding: 44.10m);
        SeedPayment(db, SeedStay(db, other, Day(2026, 5, 1), nights: 2, touristTax: 20m, totalPrice: 420m), 420m, Instant(2026, 5, 1, 10), withholding: 88.20m);
        await db.SaveChangesAsync();
        var sut = CreateService(db);
        var scope = new HostScope(orgId, "auth0|me");

        var annual = await sut.GetAnnualReportAsync(scope, TaxYear);
        var withholding = await sut.GetWithholdingReportAsync(scope, TaxYear);
        var touristTax = await sut.GetTouristTaxReportAsync(scope, FiscalReportPeriod.WholeYear(TaxYear));

        Assert.Equal(mine.Id, Assert.Single(annual.Properties).PropertyId);
        Assert.Equal(mine.Id, Assert.Single(withholding.Lines).PropertyId);
        Assert.Equal(10m, touristTax.Totals.Amount);
    }

    // ---------------------------------------------------------------- withholding

    [Fact]
    public async Task GetWithholdingReport_Lines_CarryPropertyBookingCodeRomeDateAndTotals()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Casa Ritenute");
        var airbnb = SeedStay(db, property, Day(2026, 7, 1), nights: 3, source: BookingSource.Airbnb);
        airbnb.BookingCode = "ABCDE12345";
        var bookingCom = SeedStay(db, property, Day(2026, 7, 10), nights: 3, source: BookingSource.BookingCom);
        SeedPayment(db, airbnb, 600m, new DateTime(2026, 7, 31, 22, 30, 0, DateTimeKind.Utc), withholding: 126m);
        SeedPayment(db, bookingCom, 400m, Instant(2026, 7, 12, 10), withholding: 84m, source: WithholdingSource.Manual);
        SeedPayment(db, bookingCom, 100m, Instant(2026, 7, 13, 10));
        await db.SaveChangesAsync();

        var report = await CreateService(db).GetWithholdingReportAsync(new HostScope(orgId, null), TaxYear);

        Assert.Equal(2, report.Lines.Count);
        var first = report.Lines[^1];
        Assert.Equal("ABCDE-12345", first.BookingCode);
        Assert.Equal("Casa Ritenute", first.PropertyName);
        Assert.Equal(new DateOnly(2026, 8, 1), first.PaidOn);
        Assert.Equal(WithholdingSource.Manual, report.Lines[0].WithholdingSource);
        Assert.Equal(new WithholdingTotals(1000m, 210m, 790m, 2), report.Totals);
        Assert.Equal(["Airbnb", "BookingCom"], report.ByOta.Select(o => o.Source));
    }

    // ---------------------------------------------------------------- tourist tax

    [Fact]
    public async Task GetTouristTaxReport_GroupsByComuneAndMonth_WithTheRecordedAmounts()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var milano = SeedProperty(db, orgId, "Loft Milano", city: "Milano");
        var milano2 = SeedProperty(db, orgId, "Bilocale Milano", city: "  milano ");
        var roma = SeedProperty(db, orgId, "Casa Roma", city: "Roma");
        SeedStay(db, milano, Day(2026, 3, 2), nights: 3, guests: 2, touristTax: 57m);
        SeedStay(db, milano2, Day(2026, 3, 20), nights: 2, guests: 2, touristTax: 0m);
        SeedStay(db, milano, Day(2026, 4, 5), nights: 1, guests: 1, touristTax: 9.50m, status: BookingStatus.CheckedOut);
        SeedStay(db, roma, Day(2026, 3, 10), nights: 4, guests: 3, touristTax: 72m, status: BookingStatus.CheckedIn);
        SeedStay(db, milano, Day(2026, 3, 25), nights: 2, guests: 2, touristTax: 38m, status: BookingStatus.Cancelled);
        SeedStay(db, milano, Day(2026, 3, 26), nights: 2, guests: 2, touristTax: 38m, status: BookingStatus.Pending);
        SeedStay(db, milano, Day(2026, 5, 1), nights: 2, guests: 2, touristTax: 38m);
        await db.SaveChangesAsync();

        var report = await CreateService(db).GetTouristTaxReportAsync(
            new HostScope(orgId, null),
            new FiscalReportPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 30)));

        Assert.Equal(
            [("Milano", 3, 2, 5, 4, 57m, 1), ("Milano", 4, 1, 1, 1, 9.50m, 0), ("Roma", 3, 1, 4, 3, 72m, 0)],
            report.Rows.Select(r => (r.Comune, r.Month, r.Stays, r.Nights, r.Guests, r.Amount, r.StaysWithoutAmount)));
        Assert.Equal(new TouristTaxTotals(4, 10, 8, 138.50m, 1), report.Totals);
        Assert.Equal(["Milano", "Roma"], report.ByComune.Select(c => c.Comune));
        Assert.Equal(66.50m, report.ByComune[0].Amount);
        Assert.Equal(4, report.Stays.Count);
        Assert.All(report.Stays, s => Assert.Matches("^[0-9A-Z]{5}-[0-9A-Z]{5}$", s.BookingCode));
    }

    [Fact]
    public async Task GetTouristTaxReport_PeriodLongerThanOneYear_ThrowsValidation()
    {
        await using var db = CreateDb();
        var sut = CreateService(db);

        var ex = await Assert.ThrowsAsync<FiscalValidationException>(() => sut.GetTouristTaxReportAsync(
            new HostScope(Guid.NewGuid(), null), new FiscalReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 2))));

        Assert.Equal("fiscal_report_period_invalid", ex.Code);
    }

    // ---------------------------------------------------------------- PDF

    [Fact]
    public void ToPdf_AnnualReportWithManyProperties_ItalianTablesOnSeveralA4Pages()
    {
        var lines = Enumerable.Range(1, 70)
            .Select(i => new AnnualIncomeLine(
                Guid.NewGuid(), $"Immobile{i:D3}", StrFiscalRegime.CedolareSecca26, 12345.67m, 2592.59m, 9753.08m, 0m, 12345.67m,
                null, 12345.67m, 0.26m, 3209.87m, null, 0))
            .ToList();
        var report = new AnnualIncomeReport(
            TaxYear,
            FiscalCopy.PackLabel,
            FiscalCopy.Disclaimer,
            lines,
            new AnnualIncomeTotals(864196.90m, 181481.30m, 682715.60m, 0m, 864196.90m, 864196.90m, 224690.90m, 0),
            FiscalReportPeriod.WholeYear(TaxYear),
            "Host Srl",
            new DateOnly(2026, 9, 25),
            [new AnnualTaxpayerTotals(0, null, true, false, 70, 864196.90m, 181481.30m, 224690.90m)],
            Rules());

        var pdf = new MigraDocPdfDocumentRenderer().Render(FiscalReportDocuments.Pdf(report));

        var pages = PdfTestReader.Pages(pdf);
        Assert.True(pages.Count > 2, $"{pages.Count} pages");
        Assert.All(pages, page =>
        {
            Assert.Equal(595, Math.Round(page.Width));
            Assert.Equal(842, Math.Round(page.Height));
        });
        var words = PdfTestReader.BodyWords(pdf);
        // Two tables per property (income, estimated tax): every property appears twice, in order.
        var properties = words.Where(w => w.StartsWith("Immobile0", StringComparison.Ordinal)).ToList();
        Assert.Equal(Enumerable.Range(1, 70).Select(i => $"Immobile{i:D3}").Concat(Enumerable.Range(1, 70).Select(i => $"Immobile{i:D3}")), properties);
        // Amounts, including a 6-digit total, are never split across lines.
        Assert.Contains("12.345,67", words);
        Assert.Contains("864.196,90", words);
        Assert.Contains("26%", words);
        var text = PdfTestReader.Text(pdf);
        Assert.Contains("Riepilogo fiscale affitti brevi 2026", text, StringComparison.Ordinal);
        Assert.Contains("Periodo: dal 01/01/2026 al 31/12/2026", text, StringComparison.Ordinal);
        Assert.Contains("Incassato lordo", text, StringComparison.Ordinal);
        Assert.Contains("Imposta stimata", text, StringComparison.Ordinal);
        Assert.Contains("Note e fonti", text, StringComparison.Ordinal);
        Assert.Contains("art. 1 c. 63 L. 213/2023", text, StringComparison.Ordinal);
        Assert.Contains("n.d.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Gross", text, StringComparison.Ordinal);
        Assert.DoesNotContain("withholding", text, StringComparison.OrdinalIgnoreCase);
        // The header row of the income table is repeated on the pages it spans.
        Assert.True(
            pages.Count(p => p.Text.Contains("Immobile Regime Incassato", StringComparison.Ordinal)) > 1,
            string.Join("\n---\n", pages.Select(p => p.Text[..Math.Min(300, p.Text.Length)])));
    }

    [Fact]
    public void ToPdf_WithholdingReport_ChannelAndPaymentTablesOnSeveralPages()
    {
        var lines = Enumerable.Range(1, 120)
            .Select(i => new WithholdingLine(
                Guid.NewGuid(), Guid.NewGuid(), i % 2 == 0 ? "BookingCom" : "Airbnb", new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
                100000m, 21000m, 79000m, $"Casa{i:D3}", $"ABCDE-{i:D5}", new DateOnly(2026, 3, 1), WithholdingSource.AutoOta))
            .ToList();
        var report = new WithholdingReport(
            TaxYear,
            FiscalCopy.PackLabel,
            [new WithholdingOtaBucket("Airbnb", 6000000m, 1260000m, 4740000m, 60), new WithholdingOtaBucket("BookingCom", 6000000m, 1260000m, 4740000m, 60)],
            lines,
            new FiscalReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)),
            FiscalCopy.Disclaimer,
            "Host",
            new DateOnly(2026, 9, 25),
            new WithholdingTotals(12000000m, 2520000m, 9480000m, 120),
            Rules());

        var pdf = new MigraDocPdfDocumentRenderer().Render(FiscalReportDocuments.Pdf(report));

        var pages = PdfTestReader.Pages(pdf);
        Assert.True(pages.Count > 2, $"{pages.Count} pages");
        var words = PdfTestReader.BodyWords(pdf);
        Assert.Equal(Enumerable.Range(1, 120).Select(i => $"ABCDE-{i:D5}"), words.Where(w => w.StartsWith("ABCDE-", StringComparison.Ordinal)));
        Assert.Contains("Booking.com", words);
        Assert.Contains("01/03/2026", words);
        Assert.Contains("100.000,00", words);
        var text = PdfTestReader.Text(pdf);
        Assert.Contains("Ritenute subite dagli intermediari 2026", text, StringComparison.Ordinal);
        Assert.Contains("Periodo: dal 01/01/2026 al 31/03/2026", text, StringComparison.Ordinal);
        Assert.Contains("art. 4 c. 5 D.L. 50/2017", text, StringComparison.Ordinal);
        Assert.True(pages.Count(p => p.Text.Contains("Prenotazione Canale", StringComparison.Ordinal)) > 1);
    }

    [Fact]
    public void ToPdf_TouristTaxReport_TablesPerComuneMonthAndStay()
    {
        var stays = Enumerable.Range(1, 60)
            .Select(i => new TouristTaxStayLine(
                Guid.NewGuid(), $"MILAN-{i:D5}", Guid.NewGuid(), "Loft", "Milano", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 4),
                3, 2, BookingSource.Direct, i % 10 == 0 ? 0m : 57m))
            .ToList();
        var report = new TouristTaxReport(
            new FiscalReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)),
            FiscalCopy.TouristTaxDisclaimer,
            "Host",
            new DateOnly(2026, 9, 25),
            [new TouristTaxPeriodRow("Milano", 2026, 3, 60, 180, 120, 3078m, 6)],
            [new TouristTaxComuneTotals("Milano", 60, 180, 120, 3078m, 6)],
            stays,
            new TouristTaxTotals(60, 180, 120, 3078m, 6));

        var pdf = new MigraDocPdfDocumentRenderer().Render(FiscalReportDocuments.Pdf(report));

        Assert.True(PdfTestReader.Pages(pdf).Count > 1);
        var words = PdfTestReader.BodyWords(pdf);
        Assert.Equal(60, words.Count(w => w.StartsWith("MILAN-", StringComparison.Ordinal)));
        Assert.Contains("3.078,00", words);
        Assert.Contains("01/03/2026", words);
        var text = PdfTestReader.Text(pdf);
        Assert.Contains("Tassa di soggiorno per comune e periodo", text, StringComparison.Ordinal);
        Assert.Contains("marzo 2026", text, StringComparison.Ordinal);
        Assert.Contains("Soggiorni a Milano", text, StringComparison.Ordinal);
        Assert.Contains("Modello 21", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- CSV

    [Fact]
    public async Task ToCsv_AnnualReport_ItalianHeadersSemicolonsDecimalCommaAndFormulaGuard()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "=HYPERLINK(\"x\")");
        SeedRegime(db, property, StrFiscalRegime.CedolareSecca21);
        SeedPayment(db, SeedStay(db, property, Day(2026, 3, 1), nights: 3, totalPrice: 1100m, touristTax: 100m), 1100m, Instant(2026, 3, 1, 10));
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var csv = sut.ToCsv(await sut.GetAnnualReportAsync(new HostScope(orgId, null), TaxYear));

        Assert.Equal(Encoding.UTF8.GetPreamble(), csv[..3]);
        var lines = Encoding.UTF8.GetString(csv[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "Immobile;Regime;Titolare;Incassato lordo;Tassa di soggiorno;Canone lordo;Commissioni;Ritenute;Netto incassato;Imponibile;Aliquota;Imposta stimata;Nota",
            lines[0]);
        Assert.Equal(
            "\"'=HYPERLINK(\"\"x\"\")\";Cedolare secca 21% (unità designata);Profilo fiscale dell'organizzazione;1100,00;100,00;1000,00;n.d.;0,00;1100,00;1000,00;21%;210,00;",
            lines[1]);
        Assert.Equal("Totale;;;1100,00;100,00;1000,00;n.d.;0,00;1100,00;1000,00;;210,00;", lines[2]);
    }

    [Fact]
    public async Task ToCsv_TouristTaxReport_RowsPerComuneAndMonthWithTotal()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Loft", city: "Milano");
        SeedStay(db, property, Day(2026, 3, 2), nights: 3, guests: 2, touristTax: 57m);
        SeedStay(db, property, Day(2026, 4, 2), nights: 1, guests: 1, touristTax: 9.5m);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var csv = sut.ToCsv(await sut.GetTouristTaxReportAsync(new HostScope(orgId, null), FiscalReportPeriod.WholeYear(TaxYear)));

        var lines = Encoding.UTF8.GetString(csv[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            [
                "Comune;Anno;Mese;Soggiorni;Notti;Ospiti;Imposta registrata;Soggiorni senza importo",
                "Milano;2026;3;1;3;2;57,00;0",
                "Milano;2026;4;1;1;1;9,50;0",
                "Totale;;;2;4;3;66,50;0",
            ],
            lines);
    }

    [Fact]
    public async Task ToCsv_WithholdingReport_OneLinePerPaymentWithOrigin()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Casa; con punto e virgola");
        var stay = SeedStay(db, property, Day(2026, 7, 1), nights: 3, source: BookingSource.BookingCom);
        stay.BookingCode = "ABCDE12345";
        SeedPayment(db, stay, 600m, Instant(2026, 7, 2, 10), withholding: 126m);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var csv = sut.ToCsv(await sut.GetWithholdingReportAsync(new HostScope(orgId, null), TaxYear));

        var lines = Encoding.UTF8.GetString(csv[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Data;Immobile;Prenotazione;Canale;Lordo;Ritenuta;Netto;Origine della ritenuta", lines[0]);
        Assert.Equal("2026-07-02;\"Casa; con punto e virgola\";ABCDE-12345;Booking.com;600,00;126,00;474,00;Calcolata da CasaZen", lines[1]);
        Assert.Equal("Totale;;;;600,00;126,00;474,00;", lines[2]);
    }

    // ---------------------------------------------------------------- tax profile and regimes

    [Fact]
    public async Task UpdateTaxProfile_OnlyFiscalCode_KeepsTheSavedPartitaIva()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, hasPartitaIva: true);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var profile = await sut.UpdateTaxProfileAsync(orgId, new FiscalTaxProfileUpdate(null, null, " rssmra80a01h501u "));

        Assert.True(profile.HasPartitaIva);
        Assert.Equal("12345678901", profile.PartitaIvaNumber);
        Assert.Equal(TaxpayerX, profile.FiscalCode);
    }

    [Fact]
    public async Task UpdateTaxProfile_NumberOnlyWithSavedPartitaIva_UpdatesTheNumber()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, hasPartitaIva: true, fiscalCode: TaxpayerX);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var profile = await sut.UpdateTaxProfileAsync(orgId, new FiscalTaxProfileUpdate(null, "109 876 543 21", null));

        Assert.Equal("10987654321", profile.PartitaIvaNumber);
        Assert.Equal(TaxpayerX, profile.FiscalCode);
    }

    [Fact]
    public async Task UpdateTaxProfile_PartitaIvaRemoved_ClearsTheNumberKeepsTheFiscalCode()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, hasPartitaIva: true, fiscalCode: TaxpayerX);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var profile = await sut.UpdateTaxProfileAsync(orgId, new FiscalTaxProfileUpdate(false, null, null));

        Assert.False(profile.HasPartitaIva);
        Assert.Null(profile.PartitaIvaNumber);
        Assert.Equal(TaxpayerX, profile.FiscalCode);
    }

    [Fact]
    public async Task UpdateTaxProfile_EmptyFiscalCode_ClearsIt()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, fiscalCode: TaxpayerX);
        await db.SaveChangesAsync();

        var profile = await CreateService(db).UpdateTaxProfileAsync(orgId, new FiscalTaxProfileUpdate(null, null, ""));

        Assert.Null(profile.FiscalCode);
        Assert.False(profile.HasPartitaIva);
    }

    [Theory]
    [InlineData(true, null, null)]
    [InlineData(true, "1234", null)]
    [InlineData(null, null, "RSSMRA80A01")]
    [InlineData(false, "12345678901", null)]
    public async Task UpdateTaxProfile_InvalidIdentifiers_ThrowsValidation(bool? hasPartitaIva, string? number, string? fiscalCode)
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<FiscalValidationException>(() =>
            CreateService(db).UpdateTaxProfileAsync(orgId, new FiscalTaxProfileUpdate(hasPartitaIva, number, fiscalCode)));

        Assert.Equal("fiscal_tax_identifier_invalid", ex.Code);
    }

    [Fact]
    public async Task GetRegime_WithoutPartitaIva_OffersOnlyTheShortRentalRegimes()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Uno");
        SeedStay(db, property, Day(2026, 5, 1), nights: 2);
        await db.SaveChangesAsync();

        var snapshot = await CreateService(db).GetRegimeAsync(orgId, TaxYear);

        Assert.Equal(
            [StrFiscalRegime.CedolareSecca21, StrFiscalRegime.CedolareSecca26, StrFiscalRegime.IrpefOrdinaria],
            Assert.Single(snapshot.Properties).AvailableRegimes);
    }

    [Fact]
    public async Task GetRegime_TaxpayerOverThresholdWithPartitaIva_OffersOnlyImpresaRegimes()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, hasPartitaIva: true);
        foreach (var name in new[] { "Uno", "Due", "Tre" })
            SeedStay(db, SeedProperty(db, orgId, name), Day(2026, 5, 1), nights: 2);
        await db.SaveChangesAsync();

        var snapshot = await CreateService(db).GetRegimeAsync(orgId, TaxYear);

        Assert.All(snapshot.Properties, row => Assert.Equal(
            [StrFiscalRegime.RegimeOrdinario, StrFiscalRegime.RegimeForfettario], row.AvailableRegimes));
    }

    [Fact]
    public async Task AssignRegime_ImpresaWithoutPartitaIva_ThrowsLocalizedDomainRule()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Uno");
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            CreateService(db).AssignRegimeAsync(orgId, property.Id, TaxYear, StrFiscalRegime.RegimeOrdinario, null));

        Assert.Equal("fiscal_partita_iva_required", ex.Code);
    }

    // ---------------------------------------------------------------- helpers

    private static FiscalReportRules Rules()
    {
        var options = new ShortStayFiscalOptions();
        return new FiscalReportRules(
            options.CedolareRate,
            options.CedolareReducedRate,
            options.CedolareSource,
            options.OtaWithholdingRate,
            options.OtaWithholdingSource,
            options.MaxApartmentsPerTaxpayer,
            options.ThresholdSource);
    }

    private static FiscalService CreateService(AppDbContext db) =>
        new(db, new MigraDocPdfDocumentRenderer(), Options.Create(new ShortStayFiscalOptions()), new FakeTimeProvider(Now));

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options, NullTenantContext.Instance);
    }

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime Instant(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static void SeedOrg(AppDbContext db, Guid orgId, bool hasPartitaIva = false, string? fiscalCode = null) =>
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Host",
            Slug = $"org-{orgId:N}"[..20],
            DisplayName = "Host",
            ContactEmail = "h@example.com",
            HasPartitaIva = hasPartitaIva,
            PartitaIvaNumber = hasPartitaIva ? "12345678901" : null,
            FiscalCode = fiscalCode,
        });

    private static Property SeedProperty(
        AppDbContext db,
        Guid orgId,
        string name,
        string? taxpayer = null,
        string city = "Roma",
        string ownerId = "auth0|host")
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = ownerId,
            Name = name,
            Address = "Via Test 1",
            City = city,
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
            TaxpayerFiscalCode = taxpayer,
        };
        db.Properties.Add(property);
        return property;
    }

    private static void SeedRegime(AppDbContext db, Property property, StrFiscalRegime regime) =>
        db.PropertyFiscalYears.Add(new PropertyFiscalYear
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            TaxYear = TaxYear,
            Regime = regime,
            IsPrimaryForCedolare = regime == StrFiscalRegime.CedolareSecca21,
        });

    private static Booking SeedStay(
        AppDbContext db,
        Property property,
        DateTime checkIn,
        int nights,
        int guests = 2,
        decimal touristTax = 0m,
        decimal totalPrice = 0m,
        BookingStatus status = BookingStatus.Confirmed,
        BookingSource source = BookingSource.Direct)
    {
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            Property = property,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(nights),
            NumberOfGuests = guests,
            Status = status,
            Source = source,
            TouristTax = touristTax,
            TouristTaxAmount = touristTax,
            TotalPrice = totalPrice,
        };
        db.Bookings.Add(booking);
        return booking;
    }

    private static Payment SeedPayment(
        AppDbContext db,
        Booking booking,
        decimal amount,
        DateTime processedAt,
        decimal withholding = 0m,
        decimal refunded = 0m,
        PaymentStatus status = PaymentStatus.Completed,
        WithholdingSource source = WithholdingSource.AutoOta)
    {
        var payment = new Payment
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            Booking = booking,
            Amount = amount,
            RefundedAmount = refunded,
            Status = status,
            OtaWithholdingTax = withholding,
            WithholdingTaxApplied = withholding > 0,
            NetAmountAfterWithholding = amount - withholding,
            WithholdingSource = withholding > 0 ? source : WithholdingSource.None,
            CreatedAt = processedAt,
            ProcessedAt = processedAt,
        };
        db.Payments.Add(payment);
        return payment;
    }

    /// <summary>
    /// PC-05, A2-18: the EF Core SoftDelete filter on <see cref="Property"/> propagates through
    /// <c>Include</c>/navigation (it turns the join into a filtered one), so without
    /// <c>IgnoreQueryFilters([SoftDeleteQueryFilter])</c> a soft-deleted property's payment would silently vanish
    /// from every fiscal report (<c>FiscalService.Reports.cs</c>, <c>ScopedPayments</c>). This locks in that
    /// <see cref="FiscalService.GetAnnualReportAsync"/> still reports it.
    /// </summary>
    [Fact]
    public async Task GetAnnualReport_PropertySoftDeletedAfterThePayment_StillReportsItsIncome()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var a = SeedProperty(db, orgId, "A Cedolare 21", taxpayer: TaxpayerX);
        SeedRegime(db, a, StrFiscalRegime.CedolareSecca21);
        var stayA = SeedStay(db, a, Day(2026, 3, 1), nights: 3, totalPrice: 1100m, touristTax: 100m);
        SeedPayment(db, stayA, 1100m, Instant(2026, 3, 1, 10));
        await db.SaveChangesAsync();

        a.IsDeleted = true;
        a.DeletedAt = Instant(2026, 6, 1, 0);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sut = CreateService(db);
        var report = await sut.GetAnnualReportAsync(new HostScope(orgId, null), TaxYear);

        var line = Assert.Single(report.Properties, l => l.PropertyId == a.Id);
        Assert.Equal(1100m, line.GrossIncome);
        Assert.Equal(100m, line.TouristTax);
    }
}
