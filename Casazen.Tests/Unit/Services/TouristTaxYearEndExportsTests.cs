using System.Globalization;
using System.Security.Claims;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FiscalDocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E18/F01/US02/T03 (NF-117, acceptance steps C9 and D3 of the complete flow): the tourist tax at year end. The declaration to the comune (the tax due less the tax remitted per comune, the stays with
/// and without an amount with the reason, the exempt guests by reason), the account of the agent by month or quarter, and the yearly count of the nights of Ticino from the documents issued, all as
/// CSV or Excel files made of the tourist tax summary and the stored data, never recomputed. Everything is invented (TEST properties, @example.com).
/// </summary>
public sealed class TouristTaxYearEndExportsTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 31, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Money: for each comune the declaration gives the tax due (the amounts the engine recorded on the stays of the year) less the tax remitted (the payments inside the year, instalments and a
    /// payment that spans two quarters included) as the difference, to the cent; the stays with no amount are counted apart with their reason (every guest exempt, the channel collects, never
    /// calculated) and the exempt guests by reason; a house in Switzerland is not in it; the account of the agent by quarter adds up to the year; and the files carry the same figures.
    /// </summary>
    [Fact]
    public async Task TouristTaxYearEnd_DueMinusRemittedPerMunicipality_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var loft = SeedProperty(db, orgId, "Loft Milano", "Milano");
        var bilocale = SeedProperty(db, orgId, "Bilocale Milano", "  milano ");
        var roma = SeedProperty(db, orgId, "Casa Roma", "Roma");
        var lugano = SeedProperty(db, orgId, "Casa Lugano", "Lugano", country: "CH");

        // Milano: 57.35 (one guest is a minor) and 9.55 recorded; a stay whose guests are all exempt, one the channel collects (declared), one never calculated. Roma: 72.10.
        var taxed = SeedStay(db, loft, Day(2026, 3, 2), nights: 3, touristTax: 57.35m);
        SeedRecord(db, orgId, taxed, confirmed: true, collector: TouristTaxCollector.Host, guests: [(TouristTaxExemptionKind.None, null), (TouristTaxExemptionKind.Age, TouristTaxExemptionReason.MinorAgeCode)]);
        SeedStay(db, bilocale, Day(2026, 3, 20), nights: 2, touristTax: 9.55m);
        var allExempt = SeedStay(db, loft, Day(2026, 5, 10), nights: 1, touristTax: 0m);
        SeedRecord(db, orgId, allExempt, confirmed: true, collector: TouristTaxCollector.Host, guests: [(TouristTaxExemptionKind.Host, TouristTaxExemptionReason.ResidentCode), (TouristTaxExemptionKind.Host, TouristTaxExemptionReason.ResidentCode)]);
        var channel = SeedStay(db, bilocale, Day(2026, 6, 12), nights: 2, touristTax: 0m);
        SeedRecord(db, orgId, channel, confirmed: false, collector: null, declared: TouristTaxCollector.Channel, guests: []);
        SeedStay(db, loft, Day(2026, 9, 4), nights: 1, touristTax: 0m);
        SeedStay(db, roma, Day(2026, 2, 10), nights: 4, touristTax: 72.10m);
        SeedStay(db, lugano, Day(2026, 3, 3), nights: 2, touristTax: 8m);

        // A no-show owes nothing (its guests never came): not a stay, not a night, not a tax, in the declaration as in every reader of the stays.
        SeedStay(db, loft, Day(2026, 7, 7), nights: 2, touristTax: 99m, noShow: true);

        // Payments: Milano in two instalments, each of a quarter (40.00 + 20.00), and one for March and April together (3.00): inside the year, inside no quarter. Roma 0.10 too much; Como, with no stays, 5.
        SeedPayment(db, orgId, "Milano", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 40m, new DateOnly(2026, 4, 10));
        SeedPayment(db, orgId, "Milano", new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), 20m, new DateOnly(2026, 7, 10));
        SeedPayment(db, orgId, "Milano", new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 30), 3m, new DateOnly(2026, 5, 10));
        SeedPayment(db, orgId, "Roma", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 72.20m, new DateOnly(2026, 4, 12));
        SeedPayment(db, orgId, "Como", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 5m, new DateOnly(2026, 4, 12));
        // A payment of 2025 is not of this year; another org's payment is nobody's here.
        SeedPayment(db, orgId, "Milano", new DateOnly(2025, 10, 1), new DateOnly(2025, 12, 31), 99m, new DateOnly(2026, 1, 10));
        SeedPayment(db, Guid.NewGuid(), "Milano", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 77m, new DateOnly(2026, 4, 10));
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var declaration = await sut.GetDeclarationAsync(new HostScope(orgId), 2026, withExemptionReasons: true);

        Assert.Equal(["Como", "Milano", "Roma"], declaration.Comuni.Select(c => c.Summary.Comune));
        Assert.All(declaration.Comuni, c => Assert.Equal(c.Summary.Owed - c.Summary.Paid!.Value, c.Summary.Balance!.Value));
        var como = declaration.Comuni[0];
        Assert.Equal((0, 0m, 5m, -5m), (como.Summary.Stays, como.Summary.Owed, como.Summary.Paid!.Value, como.Summary.Balance!.Value));
        var milano = declaration.Comuni[1];
        Assert.Equal((5, 9, 10), (milano.Summary.Stays, milano.Summary.Nights, milano.Summary.Guests));
        Assert.Equal((66.90m, 63m, 3.90m, TouristTaxSummaryStatus.ToPay), (milano.Summary.Owed, milano.Summary.Paid!.Value, milano.Summary.Balance!.Value, milano.Summary.Status!.Value));
        Assert.Equal([40m, 3m, 20m], milano.Summary.Remittances!.Select(r => r.Amount));
        Assert.Equal((2, 3), (milano.TaxedStays, milano.Summary.StaysWithoutAmount));
        Assert.Equal(
            [(TouristTaxStayReasons.CollectedByChannel, 1), (TouristTaxStayReasons.Exempt, 1), (TouristTaxStayReasons.NotCalculated, 1)],
            milano.StaysWithoutAmount.Select(r => (r.Reason, r.Stays)));
        Assert.Equal(milano.Summary.StaysWithoutAmount, milano.StaysWithoutAmount.Sum(r => r.Stays));
        Assert.Equal(
            [(TouristTaxExemptionReason.MinorAgeCode, 1, 1, 3), (TouristTaxExemptionReason.ResidentCode, 1, 2, 2)],
            milano.Exemptions.Select(r => (r.Reason, r.Stays, r.Guests, r.GuestNights)));
        var romaRow = declaration.Comuni[2];
        Assert.Equal((72.10m, 72.20m, -0.10m, TouristTaxSummaryStatus.Overpaid), (romaRow.Summary.Owed, romaRow.Summary.Paid!.Value, romaRow.Summary.Balance!.Value, romaRow.Summary.Status!.Value));
        Assert.Equal((1, 0), (romaRow.TaxedStays, romaRow.StaysWithoutAmount.Count));
        Assert.Equal(new TouristTaxSummaryTotals(6, 13, 12, 139.00m, 3, 140.20m, -1.20m), declaration.Total);

        // The account of the agent by quarter: what each comune is owed in a quarter and what was remitted for it, and the line of the year; the quarters add up to the year for what is owed.
        var account = await sut.GetAgentAccountAsync(new HostScope(orgId), 2026, TouristTaxAccountPeriod.Quarter);
        var milanoRows = account.Rows.Where(r => r.Comune == "Milano").ToList();
        Assert.Equal(["2026-Q1", "2026-Q2", "2026-Q3", "2026"], milanoRows.Select(r => r.Period));
        Assert.Equal([(66.90m, 40m), (0m, 20m), (0m, 0m), (66.90m, 63m)], milanoRows.Select(r => (r.Collected, r.Remitted)));
        Assert.Equal(milanoRows[^1].Collected, milanoRows.Where(r => !r.IsYear).Sum(r => r.Collected));
        Assert.Equal(3.90m, milanoRows[^1].Balance);
        Assert.Equal([40m, 3m, 20m], account.Remittances.Where(r => r.Comune == "Milano").Select(r => r.Amount));
        Assert.Equal(declaration.Total, account.Total);
        var months = await sut.GetAgentAccountAsync(new HostScope(orgId), 2026, TouristTaxAccountPeriod.Month);
        Assert.Equal(["2026-03", "2026-05", "2026-06", "2026-09", "2026"], months.Rows.Where(r => string.Equals(r.Comune, "Milano", StringComparison.OrdinalIgnoreCase) && r.Stays > 0).Select(r => r.Period));

        // The files: the same figures in the CSV and in the Excel file, in the language of the request.
        var texts = new ResxDocumentTexts();
        var csv = Encoding.UTF8.GetString(TouristTaxYearEndExports.DeclarationCsv(declaration, texts, DocumentLanguages.Italian));
        Assert.Contains("Milano;5;9;10;2;3;1;1;1;0;66,90;63,00;3,90;Da versare;Importo incompleto", csv, StringComparison.Ordinal);
        Assert.Contains("Roma;1;4;2;1;0;0;0;0;0;72,10;72,20;-0,10;Versata in più;", csv, StringComparison.Ordinal);
        Assert.Contains("Milano;Residenti nel comune;1;2;2", csv, StringComparison.Ordinal);
        Assert.Contains("Milano;01/01/2026;31/03/2026;40,00;10/04/2026", csv, StringComparison.Ordinal);
        Assert.Contains("Totale;6;13;12;3;3;", csv, StringComparison.Ordinal);
        Assert.Contains("139,00;140,20;-1,20", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Lugano", csv, StringComparison.Ordinal);
        var english = Encoding.UTF8.GetString(TouristTaxYearEndExports.DeclarationCsv(declaration, texts, DocumentLanguages.English));
        Assert.Contains("Tax remitted (euro)", english, StringComparison.Ordinal);
        Assert.Contains(";Overpaid;", english, StringComparison.Ordinal);
        Assert.True(TouristTaxYearEndExports.DeclarationXlsx(declaration, texts, DocumentLanguages.Italian).Length > 0);
        var accountCsv = Encoding.UTF8.GetString(TouristTaxYearEndExports.AgentAccountCsv(account, texts, DocumentLanguages.Italian));
        Assert.Contains("Milano;2026-Q1;01/01/2026;31/03/2026;2;5;4;66,90;0;40,00;26,90;", accountCsv, StringComparison.Ordinal);
        Assert.Contains("Milano;Anno;01/01/2026;31/12/2026;5;9;10;66,90;3;63,00;3,90;Importo incompleto", accountCsv, StringComparison.Ordinal);
        Assert.True(TouristTaxYearEndExports.AgentAccountXlsx(months, texts, DocumentLanguages.English).Length > 0);
        Assert.Equal("casazen-imposta-soggiorno-dichiarazione-2026.csv", TouristTaxYearEndExports.DeclarationFileName(2026, "csv"));
        Assert.Equal("casazen-imposta-soggiorno-conto-agente-2026-trimestrale.xlsx", TouristTaxYearEndExports.AgentAccountFileName(2026, TouristTaxAccountPeriod.Quarter, "xlsx"));
    }

    /// <summary>
    /// Data: the yearly count of the nights of Ticino is read from the stays of the year in the properties of the canton and the documents issued for them: the taxed nights (a tourist tax on the
    /// document, a credit note taking it away), the untaxed ones (a document without it), those with no document issued yet (told apart, never untaxed), and the francs of the documents; a property in
    /// another canton, a stay of another year or a cancelled one, and the documents of another org are not in it; a property in Switzerland whose canton is not known is in it, flagged.
    /// </summary>
    [Fact]
    public async Task TouristTaxYear_TicinoCountComesFromTheDocumentsOfTheStays_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = "5192", CantonCode = "TI", OfficialName = "Lugano" });
        db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = "261", CantonCode = "ZH", OfficialName = "Zürich" });
        var casa = SeedProperty(db, orgId, "Casa Lugano", "Lugano", country: "CH", municipality: "5192");
        var vuota = SeedProperty(db, orgId, "Casa Vuota Lugano", "Lugano", country: "CH", municipality: "5192");
        var zurigo = SeedProperty(db, orgId, "Casa Zurigo", "Zürich", country: "CH", municipality: "261");
        var senzaComune = SeedProperty(db, orgId, "Villa Senza Comune", "Chiasso", country: "CH", municipality: null);

        var taxed = SeedStay(db, casa, Day(2026, 3, 1), nights: 3, touristTax: 0m);
        var untaxed = SeedStay(db, casa, Day(2026, 4, 10), nights: 2, touristTax: 0m);
        var credited = SeedStay(db, casa, Day(2026, 12, 30), nights: 4, touristTax: 0m);
        SeedStay(db, casa, Day(2026, 8, 1), nights: 2, touristTax: 0m);
        SeedStay(db, casa, Day(2025, 12, 30), nights: 3, touristTax: 0m);
        SeedStay(db, casa, Day(2026, 6, 1), nights: 2, touristTax: 0m, status: BookingStatus.Cancelled);
        SeedStay(db, casa, Day(2026, 7, 1), nights: 3, touristTax: 0m, noShow: true);
        var elsewhere = SeedStay(db, zurigo, Day(2026, 3, 1), nights: 5, touristTax: 0m);
        SeedStay(db, senzaComune, Day(2026, 9, 9), nights: 2, touristTax: 0m);
        SeedDocument(db, orgId, casa, taxed, FiscalDocumentKind.Invoice, "CHF", tax: 12m);
        SeedDocument(db, orgId, casa, untaxed, FiscalDocumentKind.Invoice, "CHF", tax: 0m);
        SeedDocument(db, orgId, casa, credited, FiscalDocumentKind.Invoice, "CHF", tax: 16m);
        SeedDocument(db, orgId, casa, credited, FiscalDocumentKind.CreditNote, "CHF", tax: 16m);
        SeedDocument(db, orgId, zurigo, elsewhere, FiscalDocumentKind.Invoice, "CHF", tax: 20m);
        SeedDocument(db, Guid.NewGuid(), casa, taxed, FiscalDocumentKind.Invoice, "CHF", tax: 500m);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var count = await sut.GetTicinoYearlyCountAsync(new HostScope(orgId), 2026);

        Assert.Equal(new DateOnly(2027, 1, 5), count.DueDate);
        Assert.Equal(["Casa Lugano", "Casa Vuota Lugano", "Villa Senza Comune"], count.Properties.Select(p => p.PropertyName));
        var lugano = count.Properties[0];
        Assert.Equal((4, 11, 1, 3, 2, 6, 1, 2, "CHF", 12m), (lugano.Stays, lugano.Nights, lugano.TaxedStays, lugano.TaxedNights, lugano.UntaxedStays, lugano.UntaxedNights, lugano.StaysWithoutDocument, lugano.NightsWithoutDocument, lugano.Currency, lugano.TouristTax));
        Assert.Equal(lugano.Nights, lugano.TaxedNights + lugano.UntaxedNights + lugano.NightsWithoutDocument);
        var empty = count.Properties[1];
        Assert.Equal((0, 0, 0m), (empty.Stays, empty.Nights, empty.TouristTax));

        // A property in Switzerland whose canton is not known may be in Ticino: it is in the count with its stays and flagged, never dropped in silence; a property of Ticino is not flagged.
        var unknown = count.Properties[2];
        Assert.Equal((true, 1, 2, 1, 2), (unknown.CantonUnknown, unknown.Stays, unknown.Nights, unknown.StaysWithoutDocument, unknown.NightsWithoutDocument));
        Assert.All(count.Properties.Take(2), p => Assert.False(p.CantonUnknown));

        var texts = new ResxDocumentTexts();
        var csv = Encoding.UTF8.GetString(TouristTaxYearEndExports.TicinoCsv(count, texts, DocumentLanguages.Italian));
        Assert.Contains("05/01/2027", csv, StringComparison.Ordinal);
        Assert.Contains("Casa Lugano;Via Test 1;Lugano;5192;4;11;1;3;2;6;1;2;CHF;12,00", csv, StringComparison.Ordinal);
        Assert.Contains("Villa Senza Comune;Via Test 1;Chiasso;;1;2;0;0;0;0;1;2;CHF;0,00;Cantone non determinato", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Zurigo", csv, StringComparison.Ordinal);
        Assert.True(TouristTaxYearEndExports.TicinoXlsx(count, texts, DocumentLanguages.English).Length > 0);
        Assert.Equal("casazen-imposta-soggiorno-ticino-2026.csv", TouristTaxYearEndExports.TicinoFileName(2026, "csv"));
        Assert.Empty((await sut.GetTicinoYearlyCountAsync(new HostScope(Guid.NewGuid()), 2026)).Properties);
    }

    /// <summary>
    /// Permission and data: the year-end data of the whole org (a declaration of some of the properties would be false, the payments belong to the org) is refused to a caller that reaches only some of
    /// them, before anything is read; a year the reports do not support is refused; and the rules that say why a stay has no amount and what an exempt guest is counted under hold.
    /// </summary>
    [Fact]
    public async Task TouristTaxYearEnd_NeedsTheWholeOrgAndTheRulesHold_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var sut = CreateService(db);

        foreach (var restricted in new[] { new HostScope(orgId, GrantedToUserId: "auth0|collaboratore"), new HostScope(orgId, OwnerId: "auth0|collaboratore") })
        {
            foreach (var ask in new Func<Task>[]
                     {
                         () => sut.GetDeclarationAsync(restricted, 2026, withExemptionReasons: true),
                         () => sut.GetAgentAccountAsync(restricted, 2026, TouristTaxAccountPeriod.Month),
                         () => sut.GetTicinoYearlyCountAsync(restricted, 2026),
                     })
            {
                var refusal = await Assert.ThrowsAsync<DomainForbiddenException>(ask);
                Assert.Equal(TouristTaxErrorCodes.YearEndNeedsWholeOrg, refusal.Code);
            }
        }

        foreach (var ask in new Func<Task>[]
                 {
                     () => sut.GetDeclarationAsync(new HostScope(orgId), 1999, withExemptionReasons: true),
                     () => sut.GetAgentAccountAsync(new HostScope(orgId), 2101, TouristTaxAccountPeriod.Quarter),
                     () => sut.GetTicinoYearlyCountAsync(new HostScope(orgId), 0),
                 })
        {
            Assert.Equal("fiscal_report_period_invalid", (await Assert.ThrowsAsync<FiscalValidationException>(ask)).Code);
        }

        // Why a stay has no amount: no record at all, a tax never confirmed or never calculated, the channel that collects, every guest exempt, a tax that is nothing.
        var paying = new TouristTaxGuestFact(TouristTaxExemptionKind.None, null);
        var exempt = new TouristTaxGuestFact(TouristTaxExemptionKind.Host, TouristTaxExemptionReason.HealthCareCode);
        Assert.Equal(TouristTaxStayReasons.NotCalculated, TouristTaxYearEndRules.NoAmountReasonOf(null));
        Assert.Equal(TouristTaxStayReasons.NotCalculated, TouristTaxYearEndRules.NoAmountReasonOf(new TouristTaxStayFacts(false, TouristTaxQuoteStatus.Calculated, null, null, [])));
        Assert.Equal(TouristTaxStayReasons.NotCalculated, TouristTaxYearEndRules.NoAmountReasonOf(new TouristTaxStayFacts(true, TouristTaxQuoteStatus.RateUnavailable, TouristTaxCollector.Host, null, [])));
        Assert.Equal(TouristTaxStayReasons.CollectedByChannel, TouristTaxYearEndRules.NoAmountReasonOf(new TouristTaxStayFacts(true, TouristTaxQuoteStatus.Calculated, TouristTaxCollector.Channel, null, [paying])));
        Assert.Equal(TouristTaxStayReasons.CollectedByChannel, TouristTaxYearEndRules.NoAmountReasonOf(new TouristTaxStayFacts(false, TouristTaxQuoteStatus.Calculated, null, TouristTaxCollector.Channel, [])));
        Assert.Equal(TouristTaxStayReasons.Exempt, TouristTaxYearEndRules.NoAmountReasonOf(new TouristTaxStayFacts(true, TouristTaxQuoteStatus.Calculated, TouristTaxCollector.Host, null, [exempt, exempt])));
        Assert.Equal(TouristTaxStayReasons.NoTaxDue, TouristTaxYearEndRules.NoAmountReasonOf(new TouristTaxStayFacts(true, TouristTaxQuoteStatus.Calculated, TouristTaxCollector.Host, null, [paying, exempt])));

        // What an exempt guest is counted under: nothing for a paying guest, the age, the code the host chose, and «erased» once the reason went with the guest's data.
        Assert.Null(TouristTaxYearEndRules.ExemptionReasonOf(paying));
        Assert.Equal(TouristTaxExemptionReason.MinorAgeCode, TouristTaxYearEndRules.ExemptionReasonOf(new TouristTaxGuestFact(TouristTaxExemptionKind.Age, null)));
        Assert.Equal(TouristTaxExemptionReason.HealthCareCode, TouristTaxYearEndRules.ExemptionReasonOf(exempt));
        Assert.Equal(TouristTaxExemptionKinds.Erased, TouristTaxYearEndRules.ExemptionReasonOf(new TouristTaxGuestFact(TouristTaxExemptionKind.Host, null)));

        // The periods of the account of the agent, and the day Ticino wants its count.
        Assert.Equal(
            [("2026-Q1", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)), ("2026-Q2", new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)), ("2026-Q3", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)), ("2026-Q4", new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31))],
            TouristTaxYearEndRules.PeriodsOf(2026, TouristTaxAccountPeriod.Quarter));
        var monthly = TouristTaxYearEndRules.PeriodsOf(2028, TouristTaxAccountPeriod.Month);
        Assert.Equal(12, monthly.Count);
        Assert.Equal(("2028-02", new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)), monthly[1]);
        Assert.Equal(new DateOnly(2027, 1, 5), TouristTaxYearEndRules.TicinoCountDue(2026));
    }

    /// <summary>
    /// Privacy (art. 9 GDPR, review of S031): the reason of an exemption can reveal a health condition, so, as on the tax of a stay, the declaration to the comune names the reasons only to a caller that
    /// holds <c>guest.read</c> on the whole org (the accountant holds <c>payment.read</c> and not <c>guest.read</c>). The others get the age and one «restricted» row that counts the same stays,
    /// guests and nights, and not a word of any reason: neither in the data nor in the file the controller answers.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TouristTaxYearEnd_ExemptionReasonsOnlyToWhoHoldsGuestRead_Test(bool holdsGuestRead)
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var loft = SeedProperty(db, orgId, "Loft Milano", "Milano");

        // A minor and a guest exempt for a health reason, two residents, and a guest whose reason was erased with the data.
        var health = SeedStay(db, loft, Day(2026, 3, 2), nights: 3, touristTax: 20m);
        SeedRecord(db, orgId, health, confirmed: true, collector: TouristTaxCollector.Host, guests: [(TouristTaxExemptionKind.Age, TouristTaxExemptionReason.MinorAgeCode), (TouristTaxExemptionKind.Host, TouristTaxExemptionReason.HealthCareCode)]);
        var residents = SeedStay(db, loft, Day(2026, 5, 10), nights: 1, touristTax: 0m);
        SeedRecord(db, orgId, residents, confirmed: true, collector: TouristTaxCollector.Host, guests: [(TouristTaxExemptionKind.Host, TouristTaxExemptionReason.ResidentCode), (TouristTaxExemptionKind.Host, TouristTaxExemptionReason.ResidentCode)]);
        var erased = SeedStay(db, loft, Day(2026, 6, 15), nights: 2, touristTax: 0m);
        SeedRecord(db, orgId, erased, confirmed: true, collector: TouristTaxCollector.Host, guests: [(TouristTaxExemptionKind.Host, null)]);
        await db.SaveChangesAsync();

        // The data: with guest.read every reason, without it the age and «restricted», with the counts of the reasons it folds (3 stays, 4 guests, 7 nights of guests).
        var data = await CreateService(db).GetDeclarationAsync(new HostScope(orgId), 2026, holdsGuestRead);
        var counts = Assert.Single(data.Comuni).Exemptions.Select(r => (r.Reason, r.Stays, r.Guests, r.GuestNights)).ToList();
        List<(string Reason, int Stays, int Guests, int GuestNights)> expected = holdsGuestRead
            ? [(TouristTaxExemptionKinds.Erased, 1, 1, 2), (TouristTaxExemptionReason.HealthCareCode, 1, 1, 3), (TouristTaxExemptionReason.MinorAgeCode, 1, 1, 3), (TouristTaxExemptionReason.ResidentCode, 1, 2, 2)]
            : [(TouristTaxExemptionReason.MinorAgeCode, 1, 1, 3), (TouristTaxExemptionKinds.Restricted, 3, 4, 7)];
        Assert.Equal(expected, counts);

        // The file the controller answers: the reasons in words only for who holds guest.read, the permission asked as the policy of the area (never the one of the payments).
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.GuestRead))
            .ReturnsAsync(holdsGuestRead ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var org = new Mock<IOrgContextResolver>();
        org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostScope(orgId));
        var controller = new TouristTaxSummaryController(CreateService(db), org.Object, scopes.Object, Mock.Of<IStringLocalizer<SharedResources>>(), new ResxDocumentTexts(), authorization.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|commercialista")], "Test")) },
            },
        };

        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("it-IT");
        string csv;
        try
        {
            var answer = Assert.IsType<FileContentResult>(await controller.GetYearEndDeclaration(2026, "csv", CancellationToken.None));
            csv = Encoding.UTF8.GetString(answer.FileContents);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        Assert.Contains("Milano;Minori (esenzione per età);1;1;3", csv, StringComparison.Ordinal);
        if (holdsGuestRead)
        {
            Assert.Contains("Milano;Motivi di salute o di assistenza;1;1;3", csv, StringComparison.Ordinal);
            Assert.Contains("Milano;Residenti nel comune;1;2;2", csv, StringComparison.Ordinal);
            Assert.Contains("Milano;Motivo non più disponibile (dati dell'ospite cancellati);1;1;2", csv, StringComparison.Ordinal);
            Assert.DoesNotContain("motivo riservato", csv, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Milano;Esenti, motivo riservato (richiede il permesso sui dati degli ospiti);3;4;7", csv, StringComparison.Ordinal);
            Assert.DoesNotContain("salute", csv, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Residenti nel comune", csv, StringComparison.Ordinal);
            Assert.DoesNotContain("Motivo non più disponibile", csv, StringComparison.Ordinal);
        }
    }

    private static TouristTaxSummaryService CreateService(AppDbContext db)
    {
        var clock = new FakeTimeProvider(Now);
        var fiscal = new FiscalService(db, new MigraDocPdfDocumentRenderer(), Options.Create(new ShortStayFiscalOptions()), clock);
        return new TouristTaxSummaryService(db, fiscal, Mock.Of<IFileStorage>(), NullLogger<TouristTaxSummaryService>.Instance, clock);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static void SeedOrg(AppDbContext db, Guid orgId) =>
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Host",
            Slug = $"org-{orgId:N}"[..20],
            DisplayName = "Host",
            ContactEmail = "h@example.com",
        });

    private static Property SeedProperty(AppDbContext db, Guid orgId, string name, string city, string country = "IT", string? municipality = null)
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = "auth0|host",
            Name = name,
            Address = "Via Test 1",
            City = city,
            PostalCode = "00100",
            Country = country,
            Currency = country == "CH" ? "CHF" : "EUR",
            SwissMunicipalityCode = municipality,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        db.Properties.Add(property);
        return property;
    }

    private static Booking SeedStay(
        AppDbContext db,
        Property property,
        DateTime checkIn,
        int nights,
        decimal touristTax,
        BookingStatus status = BookingStatus.Confirmed,
        bool noShow = false)
    {
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            Property = property,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(nights),
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Direct,
            TouristTax = touristTax,
            TouristTaxAmount = touristTax,
            NoShowAt = noShow ? checkIn.AddDays(1) : null,
        };
        db.Bookings.Add(booking);
        return booking;
    }

    /// <summary>What a stay stores of its tourist tax: a record, confirmed or only declared, and the guests with the kind of their exemption and its reason.</summary>
    private static void SeedRecord(
        AppDbContext db,
        Guid orgId,
        Booking booking,
        bool confirmed,
        TouristTaxCollector? collector,
        IReadOnlyList<(TouristTaxExemptionKind Kind, string? Code)> guests,
        TouristTaxCollector? declared = null)
    {
        var nights = booking.CheckOutDate.Date.Subtract(booking.CheckInDate.Date).Days;
        db.StayTouristTaxes.Add(new StayTouristTax
        {
            OrgId = orgId,
            BookingId = booking.Id,
            Status = TouristTaxQuoteStatus.Calculated,
            Nights = nights,
            TaxableNights = nights,
            Total = booking.TouristTaxAmount,
            AmountDue = booking.TouristTaxAmount,
            Collector = collector,
            DeclaredCollector = declared,
            ConfirmedAt = confirmed ? Now.UtcDateTime : null,
            RetainUntil = StayTouristTax.RetentionFor(DateOnly.FromDateTime(booking.CheckOutDate)),
            Guests = [.. guests.Select((g, index) => new StayTouristTaxGuest { Position = index + 1, ExemptionKind = g.Kind, ExemptionCode = g.Code, TaxableNights = g.Kind == TouristTaxExemptionKind.None ? nights : 0 })],
        });
    }

    private static void SeedPayment(AppDbContext db, Guid orgId, string comune, DateOnly from, DateOnly to, decimal amount, DateOnly paidOn) =>
        db.TouristTaxRemittances.Add(new TouristTaxRemittance
        {
            OrgId = orgId,
            Comune = comune,
            ComuneKey = TouristTaxComune.NormalizeName(comune),
            PeriodFrom = from,
            PeriodTo = to,
            PaidAmount = amount,
            PaidOn = paidOn,
            RecordedByUserId = "auth0|host",
        });

    /// <summary>An issued document of a stay (a guest document) with a tourist tax line of <paramref name="tax"/> when it is above zero, and a line of the stay.</summary>
    private static void SeedDocument(AppDbContext db, Guid orgId, Property property, Booking booking, FiscalDocumentKind kind, string currency, decimal tax)
    {
        var lines = new List<FiscalDocumentLine> { new() { OrgId = orgId, LineNumber = 1, LineType = FiscalLineType.Accommodation, Description = "TEST soggiorno", Quantity = 1m, UnitPrice = 100m, TotalPrice = 100m } };
        if (tax > 0m)
            lines.Add(new FiscalDocumentLine { OrgId = orgId, LineNumber = 2, LineType = FiscalLineType.TouristTax, Description = "TEST tassa di soggiorno", Quantity = 1m, UnitPrice = tax, TotalPrice = tax });

        db.FiscalDocuments.Add(new FiscalDocument
        {
            OrgId = orgId,
            BookingId = booking.Id,
            PropertyId = property.Id,
            Purpose = FiscalDocumentPurpose.GuestStay,
            Kind = kind,
            Status = FiscalDocumentStatus.Issued,
            Currency = currency,
            Series = "TEST",
            Number = "TEST/" + Guid.NewGuid().ToString("N")[..8],
            IssueDate = new DateOnly(2027, 1, 7),
            Lines = lines,
        });
    }
}
