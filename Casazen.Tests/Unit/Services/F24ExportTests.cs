using System.IO.Compression;
using System.Text;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E18/F01/US01/T02 (NF-117, ADR-018-R8): the wire of a payout carries its net after the withholding the facts decided, and the F24 data of a month is exported with
/// its breakdown per owner and payout, the totals being those of the evidence. The payouts are made by the real services (<see cref="OwnerPayoutFactsTests.FactsWorld"/>).
/// </summary>
public class F24ExportTests
{
    /// <summary>
    /// Money: the export of November has one line per tax code whose amount is the sum of the evidence of the month (the decisions less the reversal of a payout cancelled
    /// in the month), due on 16 December, and a breakdown whose rows add up to it, an owner paid in francs included (the withholding in euro, the payout in francs); the F24
    /// record of the month says the same, and the CSV and the Excel file carry the same figures.
    /// </summary>
    [Fact]
    public async Task F24Export_TotalsEqualEvidence_Test()
    {
        var world = new OwnerPayoutFactsTests.FactsWorld();
        var first = await PaidOwnerAsync(world, "EUR", 1000m, "k-first");
        await world.RateAsync("EUR", "CHF", new DateOnly(2026, 10, 10), 0.95m);
        await world.RateAsync("EUR", "CHF", new DateOnly(2026, 11, 4), 0.94m);
        var franc = await PaidOwnerAsync(world, "CHF", 600m, "k-franc");
        var reversed = await PaidOwnerAsync(world, "EUR", 500m, "k-reversed");
        await world.Payouts.ReversePayoutAsync(world.OrgId, reversed.Payout.Id, "TEST bonifico respinto", null);

        var data = await world.Withholding.GetF24DataAsync(world.OrgId, 2026, 11);

        var evidence = await world.Db.TaxObligationEvidence.AsNoTracking().Where(e => e.PeriodYear == 2026 && e.PeriodMonth == 11).ToListAsync();
        var line = Assert.Single(data.Lines);
        Assert.Equal(("1919", 11, 2026, new DateOnly(2026, 12, 16)), (line.TaxCode, line.ReferenceMonth, line.ReferenceYear, line.DueDate));
        Assert.Equal((evidence.Sum(e => e.Amount ?? 0m), evidence.Count), (line.Amount, line.EvidenceCount));
        Assert.Equal(210m + 126m, line.Amount);
        Assert.Equal(line.Amount, data.Breakdown.Sum(r => r.Amount));
        Assert.Equal(line.Amount, (await world.Withholding.GenerateF24Async(world.OrgId, 11, 2026)).TotalAmount);

        // The rows: each decision with its payout, the reversal with the opposite amounts; the owner paid in francs in its currency.
        Assert.Equal(4, data.Breakdown.Count);
        var francRow = Assert.Single(data.Breakdown, r => r.PayoutId == franc.Payout.Id);
        Assert.Equal((600m, 21m, 126m, "CHF", 570m, 118.44m, 451.56m), (francRow.GrossBase, francRow.RatePercent!.Value, francRow.Amount, francRow.PayoutCurrency, francRow.PayoutGross, francRow.PayoutWithheld, francRow.PayoutNet));
        var reversal = Assert.Single(data.Breakdown, r => r.Reversal);
        Assert.Equal((reversed.Payout.Id, -105m, -500m, -395m), (reversal.PayoutId, reversal.Amount, reversal.PayoutGross, reversal.PayoutNet));
        Assert.Equal(210m, Assert.Single(data.Breakdown, r => r.PayoutId == first.Payout.Id).Amount);

        // The files: the line of the form in the CSV (decimal comma, the day as dd/MM/yyyy) and in the first sheet of the Excel file.
        var texts = new ResxDocumentTexts();
        var csv = Encoding.UTF8.GetString(F24Documents.Csv(data, texts, DocumentLanguages.Italian));
        Assert.Contains("1919;11;2026;336,00;16/12/2026;4", csv, StringComparison.Ordinal);
        Assert.Contains("-105,00", csv, StringComparison.Ordinal);
        using var workbook = new ZipArchive(new MemoryStream(F24Documents.Xlsx(data, texts, DocumentLanguages.Italian)), ZipArchiveMode.Read);
        using var sheet = new StreamReader(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        Assert.Contains("<v>336.00</v>", await sheet.ReadToEndAsync(), StringComparison.Ordinal);
        Assert.NotNull(workbook.GetEntry("xl/worksheets/sheet2.xml"));
        Assert.Equal("f24-2026-11.csv", F24Documents.ExportFileName(2026, 11, "csv"));

        // A month without any decision has no F24 data; a month that is not one is refused.
        var empty = await Assert.ThrowsAsync<DomainRuleException>(() => world.Withholding.GetF24DataAsync(world.OrgId, 2026, 10));
        Assert.Equal(OwnerPayoutErrors.NoObligations, empty.Code);
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => world.Withholding.GetF24DataAsync(world.OrgId, 2026, 13));
        Assert.Equal(OwnerAccountingErrors.PeriodInvalid, invalid.Code);
    }

    /// <summary>
    /// Money: the wire of a payout pays its net after the withholding the facts of the ledger decided (1000 − 21% = 790), never its gross; the file says it to the cent
    /// (the seeded payouts of <c>Pain001V09Tests.WireBatch_AmountIsPayoutNet_Test</c> prove the same for any net).
    /// </summary>
    [Fact]
    public async Task WireBatch_PaysThePayoutNetAfterWithholding_Test()
    {
        var world = new OwnerPayoutFactsTests.FactsWorld();
        var result = await PaidOwnerAsync(world, "EUR", 1000m, "k-wire", publish: true);
        Assert.Equal((1000m, 210m, 790m), (result.Payout.GrossAmount, result.Payout.WithholdingAmount, result.Payout.NetAmount));

        // The owner of Como with an Italian IBAN, the agency with its issuer profile: the file of the batch is a SEPA transfer in euro.
        var owner = await world.Db.PropertyOwners.SingleAsync(o => o.Id == result.Payout.PropertyOwnerId);
        (owner.BillingStreet, owner.BillingCivic, owner.BillingPostalCode, owner.BillingCity, owner.BillingCountry) = WireTestData.ComoAddress;
        owner.Iban = WireTestData.ItalianIban;
        await world.Db.SaveChangesAsync();
        await WireTestData.AddIssuerAsync(world.Db, world.OrgId);

        var (batch, xml) = await WireTestData.NewService(world.Db).CreateBatchAsync(world.OrgId, [result.Payout.Id], WireTestData.ExecutionDate);

        Assert.Equal((790m, 1), (batch.TotalAmount, batch.TransferCount));
        Assert.Equal(790m, (await world.Db.WireBatchPayouts.AsNoTracking().SingleAsync()).Amount);
        Assert.Contains(">790.00</InstdAmt>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain(">1000.00</InstdAmt>", xml, StringComparison.Ordinal);
    }

    /// <summary>An owner with one stay of October worth <paramref name="gross"/>, its version approved (and published) and the payout of 5 November confirmed.</summary>
    private static async Task<OwnerPayoutResult> PaidOwnerAsync(OwnerPayoutFactsTests.FactsWorld world, string currency, decimal gross, string key, bool publish = false)
    {
        var owner = await world.OwnerAsync(currency);
        var house = await world.PropertyAsync(owner);
        await world.StayAsync(house, BookingSource.Manual, gross, paid: true);
        var version = await world.ApprovedVersionAsync(owner);
        if (publish)
            await world.Accounting.PublishVersionAsync(world.OrgId, version, null);
        return await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, key), null);
    }
}
