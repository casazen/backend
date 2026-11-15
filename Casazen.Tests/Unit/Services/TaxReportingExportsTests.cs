using System.Text;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E18/F01/US02/T02 (NF-117, ⚖ T2): the data of the 770 (the withholdings made and paid, month by month, with the F24 of the month) and the communication of the short-let contracts handled by
/// the agency without withholding, both as exports. The payouts are made by the real services (<see cref="OwnerPayoutFactsTests.FactsWorld"/>: the committed Italian rule, 21%, code 1919).
/// Everything is invented (TEST owners, @example.com).
/// </summary>
public class TaxReportingExportsTests
{
    /// <summary>
    /// Data: the communication of the contracts lists the qualifying short lets the agency handled and did NOT withhold on (a stay of Airbnb whose account the agency holds, withheld by the portal
    /// by rule; a stay whose payment carries the portal's withholding; a stay of a portal that pays the owner), with the lessor, the property, the duration and the consideration. It never lists a
    /// stay the agency withheld on, a stay that is no short-let regime (the owner is a business), a stay whose payout was reversed, or another year or another org.
    /// </summary>
    [Fact]
    public async Task ContractsCommunication_ExcludesStaysWithAgencyWithholding_Test()
    {
        var world = new OwnerPayoutFactsTests.FactsWorld();
        var anna = await world.OwnerAsync();
        var house = await world.PropertyAsync(anna);
        await world.AgencyHoldsAccountAsync(house, BookingSource.Airbnb);
        var withheldByTheAgency = await world.StayAsync(house, BookingSource.Manual, 1000m, paid: true);
        var airbnb = await world.StayAsync(house, BookingSource.Airbnb, 500m);
        var withheldOnPayment = await world.StayAsync(house, BookingSource.BookingCom, 300m, paid: true, portalWithholding: 63m);
        var paidToTheOwner = await world.StayAsync(house, BookingSource.Expedia, 200m);
        await PaidAsync(world, anna, "k-anna");

        // A business owner: its stay is no short-let regime, so no contract to communicate, and the agency withholds nothing on it.
        var company = await world.OwnerAsync();
        (await world.Db.OwnerFiscalProfiles.SingleAsync(p => p.PropertyOwnerId == company)).EntityKind = OwnerEntityKind.LegalEntity;
        await world.Db.SaveChangesAsync();
        var companyStay = await world.StayAsync(await world.PropertyAsync(company), BookingSource.Manual, 400m, paid: true);
        await PaidAsync(world, company, "k-company");

        // A payout that was reversed decides nothing: its stay is not a contract of the year (it comes back with the payout that decides it).
        var dario = await world.OwnerAsync();
        var darioHouse = await world.PropertyAsync(dario);
        await world.AgencyHoldsAccountAsync(darioHouse, BookingSource.Airbnb);
        var reversedStay = await world.StayAsync(darioHouse, BookingSource.Airbnb, 700m);
        var reversed = await PaidAsync(world, dario, "k-dario");
        await world.Payouts.ReversePayoutAsync(world.OrgId, reversed.Payout.Id, "TEST bonifico respinto", null);

        var data = await world.Withholding.GetContractsCommunicationAsync(world.OrgId, 2026);

        Assert.Equal(new[] { airbnb, withheldOnPayment, paidToTheOwner }.Order(), data.Rows.Select(r => r.BookingId).Order());
        Assert.DoesNotContain(data.Rows, r => r.BookingId == withheldByTheAgency || r.BookingId == companyStay || r.BookingId == reversedStay);
        var row = data.Rows.Single(r => r.BookingId == airbnb);
        Assert.Equal((anna, "TEST Proprietario", "RSSMRA70A01H501W", "TEST Casa", "Via 1", "Airbnb"), (row.LessorId, row.Lessor.Name, row.Lessor.FiscalCode, row.Property!.Name, row.Property.Address, row.Channel));
        Assert.Equal((new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12), 2, "EUR", 500m), (row.CheckIn, row.CheckOut, row.Nights, row.Currency, row.Consideration));
        Assert.Equal(
            [
                (airbnb, OwnerWithholdingRules.AlreadyWithheldByPortal),
                (withheldOnPayment, OwnerWithholdingRules.AlreadyWithheldByPortal),
                (paidToTheOwner, OwnerWithholdingRules.NoCollectionOrIntervention),
            ],
            data.Rows.OrderByDescending(r => r.Consideration).Select(r => (r.BookingId, r.Reason)));
        Assert.Equal(210m, (await world.Payouts.ReconcileAsync(world.OrgId, 2026)).ObligationsTotal);

        // The file says it in the words of the language: the reasons, the lessor, the property, never the stay the agency withheld on (its 1000 is not a consideration of the file).
        var texts = new ResxDocumentTexts();
        var csv = Encoding.UTF8.GetString(TaxReportingExports.ContractsCsv(data, texts, DocumentLanguages.Italian));
        Assert.Contains("Ritenuta già operata dal portale", csv, StringComparison.Ordinal);
        Assert.Contains("L'agenzia non ha incassato né è intervenuta nel pagamento", csv, StringComparison.Ordinal);
        Assert.Contains("RSSMRA70A01H501W", csv, StringComparison.Ordinal);
        Assert.Contains(";10/10/2026;12/10/2026;2;Airbnb;EUR;500,00;", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("1000,00", csv, StringComparison.Ordinal);
        Assert.Equal("comunicazione-locazioni-brevi-2026.csv", TaxReportingExports.ContractsFileName(2026, "csv"));
        Assert.Contains("Already withheld by the portal", Encoding.UTF8.GetString(TaxReportingExports.ContractsCsv(data, texts, DocumentLanguages.English)), StringComparison.Ordinal);
        Assert.True(TaxReportingExports.ContractsXlsx(data, texts, DocumentLanguages.Italian).Length > 0);

        // Another year and another org have none.
        Assert.Empty((await world.Withholding.GetContractsCommunicationAsync(world.OrgId, 2025)).Rows);
        Assert.Empty((await world.Withholding.GetContractsCommunicationAsync(Guid.NewGuid(), 2026)).Rows);
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => world.Withholding.GetContractsCommunicationAsync(world.OrgId, 1999));
        Assert.Equal(OwnerAccountingErrors.PeriodInvalid, invalid.Code);
    }

    /// <summary>
    /// Money: the data of the 770 has one line for each month and tax code whose withholding is the sum of the evidence of the month (a payout and its reversal in the month cancelling), with the F24
    /// of the month when there is one (its amount, due date, the day it was marked as paid, its state and its references) and the difference with what was withheld; a month whose F24 was never
    /// generated says so and leaves its F24 columns empty, never a payment nobody recorded.
    /// </summary>
    [Fact]
    public async Task Tax770_WithheldAndPaidMonthByMonthWithTheF24_Test()
    {
        var world = new OwnerPayoutFactsTests.FactsWorld();
        var anna = await world.OwnerAsync();
        await world.StayAsync(await world.PropertyAsync(anna), BookingSource.Manual, 1000m, paid: true);
        await PaidAsync(world, anna, "k-november");

        // The F24 of November is generated, reviewed and paid; a payout of 3 December has no F24.
        var f24 = await world.Withholding.GenerateF24Async(world.OrgId, 11, 2026);
        await world.Payouts.SetF24FilingStateAsync(world.OrgId, f24.Id, new TaxFilingCommand { State = TaxFilingState.Reviewed }, "auth0|admin");
        (await world.Db.F24Records.SingleAsync()).PaidAt = new DateTime(2026, 12, 10, 9, 0, 0, DateTimeKind.Utc);
        await world.Db.SaveChangesAsync();
        var bruno = await world.OwnerAsync();
        await world.StayAsync(await world.PropertyAsync(bruno), BookingSource.Manual, 500m, paid: true);
        var version = await world.ApprovedVersionAsync(bruno);
        await world.Payouts.ConfirmPayoutAsync(
            world.OrgId,
            new OwnerPayoutCommand { IdempotencyKey = "k-december", OwnerId = bruno, StatementVersionId = version, PaidOn = new DateOnly(2026, 12, 3), Reference = "TEST-DICEMBRE" },
            null);

        var data = await world.Withholding.Get770DataAsync(world.OrgId, 2026);

        Assert.Equal(2026, data.Year);
        Assert.Equal(2, data.Lines.Count);
        var november = data.Lines[0];
        Assert.Equal((11, "1919", 1000m, 210m, 1), (november.Month, november.TaxCode, november.GrossBase, november.Withheld, november.EvidenceCount));
        Assert.Equal((210m, new DateOnly(2026, 12, 16), new DateOnly(2026, 12, 10), TaxFilingState.Reviewed), (november.F24Amount, november.F24DueDate, november.F24PaidOn, november.FilingState));
        var december = data.Lines[1];
        Assert.Equal((12, "1919", 500m, 105m, 1), (december.Month, december.TaxCode, december.GrossBase, december.Withheld, december.EvidenceCount));
        Assert.Null(december.F24Amount);
        Assert.Null(december.F24DueDate);
        Assert.Null(december.F24PaidOn);
        Assert.Null(december.FilingState);
        Assert.Equal(315m, data.Lines.Sum(l => l.Withheld));

        // The file: the line of November with its F24 and a difference of zero, the line of December with no F24 and no difference.
        var texts = new ResxDocumentTexts();
        var csv = Encoding.UTF8.GetString(TaxReportingExports.Tax770Csv(data, texts, DocumentLanguages.Italian));
        Assert.Contains("11;2026;1919;1000,00;210,00;210,00;16/12/2026;10/12/2026;verificato, non trasmesso;;;0,00;1", csv, StringComparison.Ordinal);
        Assert.Contains("12;2026;1919;500,00;105,00;;;;F24 non generato;;;;1", csv, StringComparison.Ordinal);
        Assert.Equal("770-2026.csv", TaxReportingExports.Tax770FileName(2026, "csv"));
        Assert.True(TaxReportingExports.Tax770Xlsx(data, texts, DocumentLanguages.English).Length > 0);

        // A year without any decision has no data; a year that is not one is refused.
        var empty = await Assert.ThrowsAsync<DomainRuleException>(() => world.Withholding.Get770DataAsync(world.OrgId, 2025));
        Assert.Equal(OwnerPayoutErrors.NoObligations, empty.Code);
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => world.Withholding.Get770DataAsync(world.OrgId, 2101));
        Assert.Equal(OwnerAccountingErrors.PeriodInvalid, invalid.Code);
    }

    /// <summary>The statement of October of the owner approved and the payout of 5 November confirmed.</summary>
    private static async Task<OwnerPayoutResult> PaidAsync(OwnerPayoutFactsTests.FactsWorld world, Guid owner, string key)
    {
        var version = await world.ApprovedVersionAsync(owner);
        return await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, key), null);
    }
}
