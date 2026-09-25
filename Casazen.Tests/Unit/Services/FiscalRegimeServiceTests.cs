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
/// STR fiscal rules (CO-18, A5-22; fiscale.md § CO-18): threshold per taxpayer on the apartments with short-term stays in
/// the tax year, withholding only outside the business (impresa) regime, IRPEF ordinaria regime, one 21% unit per taxpayer.
/// </summary>
public class FiscalRegimeServiceTests
{
    private const int TaxYear = 2026;
    private const string TaxpayerA = "RSSMRA80A01H501U";
    private const string TaxpayerB = "VRDLGU75B12F205X";
    private const string TaxpayerC = "BNCGNN90C41L219K";

    [Theory]
    [InlineData(1, false, "CedolareSecca21")]
    [InlineData(2, false, "CedolareSecca26")]
    [InlineData(3, true, "RequiresPartitaIva")]
    public async Task SimulateAsync_HypotheticalApartments_PresumesBusinessBeyondConfiguredThreshold(
        int apartments, bool expectedBusiness, string expectedLabel)
    {
        await using var db = CreateDb();
        var sut = CreateService(db);

        var result = await sut.SimulateAsync(Guid.NewGuid(), TaxYear, apartments);

        Assert.Equal(expectedBusiness, result.RequiresPartitaIva);
        Assert.Equal(expectedLabel, result.RecommendedForCount);
    }

    [Fact]
    public async Task SimulateAsync_ThresholdFromConfiguration_NoMagicNumber()
    {
        await using var db = CreateDb();
        var sut = CreateService(db, new ShortStayFiscalOptions { MaxApartmentsPerTaxpayer = 4 });

        var result = await sut.SimulateAsync(Guid.NewGuid(), TaxYear, 4);

        Assert.False(result.RequiresPartitaIva);
    }

    [Fact]
    public void CalculateOtaWithholding_ConfiguredRate_Is21PercentOfGross()
    {
        Assert.Equal(210.00m, FiscalCopy.CalculateOtaWithholding(1000m, new ShortStayFiscalOptions().OtaWithholdingRate));
    }

    [Fact]
    public void StrFiscalRegime_IrpefOrdinaria_IsAppendedWithoutRenumbering()
    {
        Assert.Equal(0, (int)StrFiscalRegime.CedolareSecca21);
        Assert.Equal(1, (int)StrFiscalRegime.CedolareSecca26);
        Assert.Equal(2, (int)StrFiscalRegime.RegimeOrdinario);
        Assert.Equal(3, (int)StrFiscalRegime.RegimeForfettario);
        Assert.Equal(4, (int)StrFiscalRegime.IrpefOrdinaria);
    }

    [Fact]
    public async Task GetRegime_SingleApartmentWithShortStay_RecommendsCedolare21()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Casa Uno");
        SeedStay(db, property, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), nights: 3);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var snapshot = await sut.GetRegimeAsync(orgId, TaxYear);

        Assert.Equal(1, snapshot.StrPropertyCount);
        Assert.False(snapshot.RequiresPartitaIva);
        Assert.Equal(2, snapshot.MaxShortStayApartmentsPerTaxpayer);
        Assert.Contains("L. 199/2025", snapshot.ThresholdSource, StringComparison.Ordinal);
        var row = Assert.Single(snapshot.Properties);
        Assert.Equal(StrFiscalRegime.CedolareSecca21, row.RecommendedRegime);
        Assert.True(row.ShortStayInTaxYear);
        Assert.Contains("informativa", snapshot.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRegime_CountsOnlyApartmentsWithShortStaysInTaxYear()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var shortStay = SeedProperty(db, orgId, "A Breve");
        SeedStay(db, shortStay, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), nights: 30);
        SeedProperty(db, orgId, "B Senza soggiorni");
        var inactiveWithStay = SeedProperty(db, orgId, "C Disattivato");
        inactiveWithStay.IsActive = false;
        SeedStay(db, inactiveWithStay, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), nights: 2);
        var longStay = SeedProperty(db, orgId, "D Oltre 30 notti");
        SeedStay(db, longStay, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), nights: 31);
        var cancelled = SeedProperty(db, orgId, "E Annullato");
        SeedStay(db, cancelled, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), nights: 2, BookingStatus.Cancelled);
        var otherYear = SeedProperty(db, orgId, "F Anno precedente");
        SeedStay(db, otherYear, new DateTime(2025, 12, 28, 0, 0, 0, DateTimeKind.Utc), nights: 5);
        var ltr = SeedProperty(db, orgId, "G Affitto lungo");
        SeedLease(db, orgId, ltr);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var snapshot = await sut.GetRegimeAsync(orgId, TaxYear);

        Assert.Equal(2, snapshot.StrPropertyCount);
        var counted = snapshot.Properties.Where(p => p.ShortStayInTaxYear).Select(p => p.Name).ToList();
        Assert.Equal(new[] { "A Breve", "C Disattivato" }, counted);
        Assert.DoesNotContain(snapshot.Properties, p => p.Name == "G Affitto lungo");
        Assert.Contains(snapshot.Properties, p => p.Name == "B Senza soggiorni" && !p.ShortStayInTaxYear);
    }

    [Fact]
    public async Task GetRegime_PropertyManagerWithThreeTaxpayersOneApartmentEach_NoPartitaIvaRequired()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        foreach (var (name, taxpayer) in new[] { ("Casa A", TaxpayerA), ("Casa B", TaxpayerB), ("Casa C", TaxpayerC) })
        {
            var property = SeedProperty(db, orgId, name);
            property.TaxpayerFiscalCode = taxpayer;
            SeedStay(db, property, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), nights: 4);
        }

        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var snapshot = await sut.GetRegimeAsync(orgId, TaxYear);

        Assert.Equal(3, snapshot.StrPropertyCount);
        Assert.False(snapshot.RequiresPartitaIva);
        Assert.Equal(3, snapshot.Taxpayers.Count);
        Assert.All(snapshot.Taxpayers, t =>
        {
            Assert.Equal(1, t.ShortStayApartmentCount);
            Assert.False(t.ThresholdExceeded);
            Assert.False(t.IsOrgTaxProfile);
            Assert.StartsWith("************", t.FiscalCodeMasked);
        });
        Assert.All(snapshot.Properties, p => Assert.Equal(StrFiscalRegime.CedolareSecca21, p.RecommendedRegime));
        Assert.Equal(3, snapshot.Properties.Select(p => p.TaxpayerIndex).Distinct().Count());

        // Each owner has his own 21% unit: no conflict across taxpayers of the same org.
        foreach (var row in snapshot.Properties)
        {
            var assigned = await sut.AssignRegimeAsync(orgId, row.PropertyId, TaxYear, StrFiscalRegime.CedolareSecca21, true);
            Assert.True(assigned.IsPrimaryForCedolare);
            Assert.Equal(0.21m, assigned.CedolareRate);
        }

        Assert.Equal(3, await db.PropertyFiscalYears.CountAsync(y => y.IsPrimaryForCedolare));
    }

    [Fact]
    public async Task AssignRegime_SameTaxpayerThreeApartments_RefusesCedolareAndIrpefOrdinaria()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, hasPartitaIva: true);
        var properties = Enumerable.Range(1, 3).Select(i => SeedProperty(db, orgId, $"Casa {i}")).ToList();
        foreach (var property in properties)
            SeedStay(db, property, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), nights: 2);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var snapshot = await sut.GetRegimeAsync(orgId, TaxYear);
        Assert.True(snapshot.RequiresPartitaIva);
        var taxpayer = Assert.Single(snapshot.Taxpayers);
        Assert.True(taxpayer.IsOrgTaxProfile);
        Assert.Equal(3, taxpayer.ShortStayApartmentCount);
        Assert.True(taxpayer.ThresholdExceeded);
        Assert.All(snapshot.Properties, p =>
        {
            Assert.Null(p.RecommendedRegime);
            Assert.Equal(FiscalTaxNotes.ThresholdExceeded, p.TaxNote);
        });

        foreach (var regime in new[] { StrFiscalRegime.CedolareSecca21, StrFiscalRegime.CedolareSecca26, StrFiscalRegime.IrpefOrdinaria })
        {
            var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
                sut.AssignRegimeAsync(orgId, properties[0].Id, TaxYear, regime, null));
            Assert.Equal("fiscal_short_stay_threshold_exceeded", ex.Code);
            Assert.Equal("FiscalShortStayThresholdExceeded", ex.MessageKey);
        }

        var impresa = await sut.AssignRegimeAsync(orgId, properties[0].Id, TaxYear, StrFiscalRegime.RegimeOrdinario, null);
        Assert.Equal(StrFiscalRegime.RegimeOrdinario, impresa.AssignedRegime);
    }

    [Fact]
    public async Task AssignRegime_ThirdApartmentWithoutStaysYet_RefusesCedolare()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        SeedStay(db, SeedProperty(db, orgId, "Casa 1"), new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), nights: 2);
        SeedStay(db, SeedProperty(db, orgId, "Casa 2"), new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), nights: 2);
        var third = SeedProperty(db, orgId, "Casa 3");
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var snapshot = await sut.GetRegimeAsync(orgId, TaxYear);
        Assert.False(snapshot.RequiresPartitaIva);
        Assert.Null(snapshot.Properties.Single(p => p.PropertyId == third.Id).RecommendedRegime);

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            sut.AssignRegimeAsync(orgId, third.Id, TaxYear, StrFiscalRegime.CedolareSecca26, null));
    }

    [Fact]
    public async Task AssignRegime_ThirdApartmentDeclaredBeforeAnyStay_RefusesShortRentalRegime()
    {
        // Apartments with a cedolare or IRPEF-ordinaria regime for the year are declared short-term even before a booking.
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var properties = Enumerable.Range(1, 3).Select(i => SeedProperty(db, orgId, $"Casa {i}")).ToList();
        await db.SaveChangesAsync();
        var sut = CreateService(db);
        await sut.AssignRegimeAsync(orgId, properties[0].Id, TaxYear, StrFiscalRegime.CedolareSecca21, true);
        await sut.AssignRegimeAsync(orgId, properties[1].Id, TaxYear, StrFiscalRegime.IrpefOrdinaria, null);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            sut.AssignRegimeAsync(orgId, properties[2].Id, TaxYear, StrFiscalRegime.CedolareSecca26, null));

        Assert.Equal("fiscal_short_stay_threshold_exceeded", ex.Code);
        Assert.Null((await sut.GetRegimeAsync(orgId, TaxYear)).Properties.Single(p => p.PropertyId == properties[2].Id).RecommendedRegime);
        // Changing the regime of an apartment already declared does not add one.
        var changed = await sut.AssignRegimeAsync(orgId, properties[1].Id, TaxYear, StrFiscalRegime.CedolareSecca26, null);
        Assert.Equal(StrFiscalRegime.CedolareSecca26, changed.AssignedRegime);
    }

    [Fact]
    public async Task AssignRegime_Cedolare26_WhenOnlyOneApartment_IsAllowedAtGeneralRate()
    {
        // fiscale.md C2: 26% is the general rate; the 21% unit is chosen by the taxpayer in the tax return (it may be elsewhere).
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Solo");
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var row = await sut.AssignRegimeAsync(orgId, property.Id, TaxYear, StrFiscalRegime.CedolareSecca26, false);

        Assert.Equal(StrFiscalRegime.CedolareSecca26, row.AssignedRegime);
        Assert.False(row.IsPrimaryForCedolare);
        Assert.Equal(0.26m, row.CedolareRate);
    }

    [Fact]
    public async Task AssignRegime_Cedolare21_OnAnotherUnitOfSameTaxpayer_MovesPreviousUnitTo26()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var first = SeedProperty(db, orgId, "Casa Uno");
        var second = SeedProperty(db, orgId, "Casa Due");
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        await sut.AssignRegimeAsync(orgId, first.Id, TaxYear, StrFiscalRegime.CedolareSecca21, true);
        await sut.AssignRegimeAsync(orgId, second.Id, TaxYear, StrFiscalRegime.CedolareSecca26, false);
        var snapshot = await sut.GetRegimeAsync(orgId, TaxYear);
        Assert.Equal(0.21m, snapshot.Properties.Single(p => p.PropertyId == first.Id).CedolareRate);
        Assert.Equal(0.26m, snapshot.Properties.Single(p => p.PropertyId == second.Id).CedolareRate);
        Assert.Equal(first.Id, Assert.Single(snapshot.Taxpayers).ReducedRatePropertyId);

        var designated = await sut.AssignRegimeAsync(orgId, second.Id, TaxYear, StrFiscalRegime.CedolareSecca21, true);

        Assert.Equal(0.21m, designated.CedolareRate);
        var previous = await db.PropertyFiscalYears.AsNoTracking().SingleAsync(y => y.PropertyId == first.Id);
        Assert.Equal(StrFiscalRegime.CedolareSecca26, previous.Regime);
        Assert.False(previous.IsPrimaryForCedolare);
    }

    [Fact]
    public async Task AssignRegime_CedolareRates_ComeFromConfiguration()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Casa");
        await db.SaveChangesAsync();
        var sut = CreateService(db, new ShortStayFiscalOptions { CedolareReducedRate = 0.20m });

        var row = await sut.AssignRegimeAsync(orgId, property.Id, TaxYear, StrFiscalRegime.CedolareSecca21, true);

        Assert.Equal(0.20m, row.CedolareRate);
    }

    [Fact]
    public async Task AssignRegime_IrpefOrdinariaWithoutPartitaIva_IsSelectableAndNotComputed()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, hasPartitaIva: false);
        var property = SeedProperty(db, orgId, "Casa IRPEF");
        SeedStay(db, property, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), nights: 7);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var row = await sut.AssignRegimeAsync(orgId, property.Id, TaxYear, StrFiscalRegime.IrpefOrdinaria, null);

        Assert.Equal(StrFiscalRegime.IrpefOrdinaria, row.AssignedRegime);
        Assert.False(row.IsPrimaryForCedolare);
        Assert.Null(row.CedolareRate);
        Assert.Equal(FiscalTaxNotes.IrpefOrdinariaNotComputed, row.TaxNote);
    }

    [Fact]
    public async Task AssignRegime_Cedolare21_WhenSiblingHasImpresaRegime_PreservesSiblingRegime()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, hasPartitaIva: true);
        var first = SeedProperty(db, orgId, "Casa Cedolare");
        var second = SeedProperty(db, orgId, "Casa Impresa");
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        await sut.AssignRegimeAsync(orgId, second.Id, TaxYear, StrFiscalRegime.RegimeForfettario, false);
        await sut.AssignRegimeAsync(orgId, first.Id, TaxYear, StrFiscalRegime.CedolareSecca21, true);

        var sibling = await db.PropertyFiscalYears.SingleAsync(y => y.PropertyId == second.Id && y.TaxYear == TaxYear);
        Assert.Equal(StrFiscalRegime.RegimeForfettario, sibling.Regime);
        Assert.False(sibling.IsPrimaryForCedolare);
    }

    [Fact]
    public async Task SetPropertyTaxpayer_ValidCode_NormalizesAndReturnsMasked()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Casa");
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var result = await sut.SetPropertyTaxpayerAsync(orgId, property.Id, " rssmra80a01 h501u ");

        Assert.Equal("************501U", result.FiscalCodeMasked);
        Assert.Equal(TaxpayerA, (await db.Properties.AsNoTracking().SingleAsync(p => p.Id == property.Id)).TaxpayerFiscalCode);

        var cleared = await sut.SetPropertyTaxpayerAsync(orgId, property.Id, null);
        Assert.Null(cleared.FiscalCodeMasked);
        Assert.Null((await db.Properties.AsNoTracking().SingleAsync(p => p.Id == property.Id)).TaxpayerFiscalCode);
    }

    [Theory]
    [InlineData("RSSMRA80A01H501")]
    [InlineData("12345678901")]
    [InlineData("RSSMRA80A01H501U9")]
    [InlineData("RSSMRA80-01H501U")]
    public async Task SetPropertyTaxpayer_InvalidCode_ThrowsDomainRule(string fiscalCode)
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Casa");
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => sut.SetPropertyTaxpayerAsync(orgId, property.Id, fiscalCode));

        Assert.Equal("invalid_taxpayer_fiscal_code", ex.Code);
    }

    [Fact]
    public async Task SetPropertyTaxpayer_ForeignProperty_ThrowsNotFound()
    {
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        SeedOrg(db, otherOrgId);
        var foreign = SeedProperty(db, otherOrgId, "Altrui");
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.SetPropertyTaxpayerAsync(orgId, foreign.Id, TaxpayerA));
    }

    [Fact]
    public async Task SetPropertyTaxpayer_WhenNewTaxpayerAlreadyHasReducedRateUnit_Conflicts()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var first = SeedProperty(db, orgId, "Casa A");
        first.TaxpayerFiscalCode = TaxpayerA;
        var second = SeedProperty(db, orgId, "Casa B");
        second.TaxpayerFiscalCode = TaxpayerB;
        await db.SaveChangesAsync();
        var sut = CreateService(db);
        await sut.AssignRegimeAsync(orgId, first.Id, TaxYear, StrFiscalRegime.CedolareSecca21, true);
        await sut.AssignRegimeAsync(orgId, second.Id, TaxYear, StrFiscalRegime.CedolareSecca21, true);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => sut.SetPropertyTaxpayerAsync(orgId, second.Id, TaxpayerA));

        Assert.Equal("fiscal_reduced_rate_unit_taken", ex.Code);
        Assert.Equal(TaxpayerB, (await db.Properties.AsNoTracking().SingleAsync(p => p.Id == second.Id)).TaxpayerFiscalCode);
    }

    [Fact]
    public async Task GetRegime_TaxpayerEqualToOrgFiscalCode_IsTheOrgTaxProfile()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, fiscalCode: TaxpayerA);
        var withCode = SeedProperty(db, orgId, "Con codice");
        withCode.TaxpayerFiscalCode = TaxpayerA;
        var withoutCode = SeedProperty(db, orgId, "Senza codice");
        var third = SeedProperty(db, orgId, "Terzo");
        foreach (var property in new[] { withCode, withoutCode, third })
            SeedStay(db, property, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), nights: 2);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var snapshot = await sut.GetRegimeAsync(orgId, TaxYear);

        var taxpayer = Assert.Single(snapshot.Taxpayers);
        Assert.True(taxpayer.IsOrgTaxProfile);
        Assert.Equal(3, taxpayer.ShortStayApartmentCount);
        Assert.True(snapshot.RequiresPartitaIva);
    }

    [Fact]
    public async Task ApplyWithholding_OtaAuto_DirectSkipped()
    {
        var sut = CreateService(CreateDb());
        var otaPayment = new Payment { Amount = 100m };
        var otaBooking = new Booking { Source = BookingSource.Airbnb };
        await sut.ApplyWithholdingOnCreateAsync(otaPayment, otaBooking, null, null);
        Assert.Equal(21m, otaPayment.OtaWithholdingTax);
        Assert.True(otaPayment.WithholdingTaxApplied);
        Assert.Equal(WithholdingSource.AutoOta, otaPayment.WithholdingSource);

        var directPayment = new Payment { Amount = 100m };
        var directBooking = new Booking { Source = BookingSource.Direct };
        await sut.ApplyWithholdingOnCreateAsync(directPayment, directBooking, null, null);
        Assert.Equal(0m, directPayment.OtaWithholdingTax);
        Assert.False(directPayment.WithholdingTaxApplied);
    }

    [Theory]
    [InlineData(StrFiscalRegime.RegimeOrdinario)]
    [InlineData(StrFiscalRegime.RegimeForfettario)]
    public async Task ApplyWithholding_PartitaIvaWithImpresaRegime_NoWithholding(StrFiscalRegime regime)
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, hasPartitaIva: true);
        var property = SeedProperty(db, orgId, "Casa Impresa");
        var booking = SeedStay(db, property, new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc), nights: 3, source: BookingSource.BookingCom);
        await db.SaveChangesAsync();
        var sut = CreateService(db);
        await sut.AssignRegimeAsync(orgId, property.Id, TaxYear, regime, null);
        var payment = new Payment { Amount = 300m };

        await sut.ApplyWithholdingOnCreateAsync(payment, booking, null, null);

        Assert.Equal(0m, payment.OtaWithholdingTax);
        Assert.False(payment.WithholdingTaxApplied);
        Assert.Equal(300m, payment.NetAmountAfterWithholding);
        Assert.Equal(WithholdingSource.None, payment.WithholdingSource);
    }

    [Fact]
    public async Task ApplyWithholding_PartitaIvaButShortRentalRegime_StillWithholds()
    {
        // fiscale.md C10: the criterion is the business regime of the rental, not holding a partita IVA for other work.
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId, hasPartitaIva: true);
        var property = SeedProperty(db, orgId, "Casa Professionista");
        var booking = SeedStay(db, property, new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc), nights: 3, source: BookingSource.Airbnb);
        await db.SaveChangesAsync();
        var sut = CreateService(db);
        await sut.AssignRegimeAsync(orgId, property.Id, TaxYear, StrFiscalRegime.CedolareSecca21, true);
        var payment = new Payment { Amount = 300m };

        await sut.ApplyWithholdingOnCreateAsync(payment, booking, null, null);

        Assert.Equal(63m, payment.OtaWithholdingTax);
        Assert.Equal(WithholdingSource.AutoOta, payment.WithholdingSource);
    }

    [Fact]
    public async Task ApplyWithholding_TaxpayerOverThreshold_NoWithholdingByDefault_ManualOrExplicitStillRecorded()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var bookings = Enumerable.Range(1, 3)
            .Select(i => SeedStay(db, SeedProperty(db, orgId, $"Casa {i}"), new DateTime(2026, 6, i, 0, 0, 0, DateTimeKind.Utc), nights: 2, source: BookingSource.Airbnb))
            .ToList();
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var byDefault = new Payment { Amount = 200m };
        await sut.ApplyWithholdingOnCreateAsync(byDefault, bookings[0], null, null);
        Assert.Equal(0m, byDefault.OtaWithholdingTax);
        Assert.Equal(WithholdingSource.None, byDefault.WithholdingSource);

        var manual = new Payment { Amount = 200m };
        await sut.ApplyWithholdingOnCreateAsync(manual, bookings[0], null, 42m);
        Assert.Equal(42m, manual.OtaWithholdingTax);
        Assert.Equal(WithholdingSource.Manual, manual.WithholdingSource);

        var explicitAuto = new Payment { Amount = 200m };
        await sut.ApplyWithholdingOnCreateAsync(explicitAuto, bookings[0], true, null);
        Assert.Equal(42m, explicitAuto.OtaWithholdingTax);
        Assert.Equal(WithholdingSource.AutoOta, explicitAuto.WithholdingSource);
    }

    [Fact]
    public async Task ApplyWithholding_PropertyManagerOwnersWithOneApartmentEach_Withholds()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var bookings = new[] { TaxpayerA, TaxpayerB, TaxpayerC }
            .Select((taxpayer, i) =>
            {
                var property = SeedProperty(db, orgId, $"Casa {i}");
                property.TaxpayerFiscalCode = taxpayer;
                return SeedStay(db, property, new DateTime(2026, 6, i + 1, 0, 0, 0, DateTimeKind.Utc), nights: 2, source: BookingSource.Airbnb);
            })
            .ToList();
        await db.SaveChangesAsync();
        var sut = CreateService(db);
        var payment = new Payment { Amount = 100m };

        await sut.ApplyWithholdingOnCreateAsync(payment, bookings[2], null, null);

        Assert.Equal(21m, payment.OtaWithholdingTax);
    }

    [Fact]
    public async Task ApplyWithholding_StayLongerThanShortRental_NoWithholding()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Casa Mese");
        var booking = SeedStay(db, property, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), nights: 31, source: BookingSource.Airbnb);
        await db.SaveChangesAsync();
        var sut = CreateService(db);
        var payment = new Payment { Amount = 1000m };

        await sut.ApplyWithholdingOnCreateAsync(payment, booking, null, null);

        Assert.Equal(0m, payment.OtaWithholdingTax);
    }

    [Fact]
    public void ToPdf_LongReport_A4PagesWithEveryLine()
    {
        // LT-09 (A7-14): no more single Letter page cut at 4000 characters.
        using var db = CreateDb();
        var sut = CreateService(db);
        var body = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"Riga {i:D3} lordo 1.000,00 ritenuta 210,00 netto 790,00"));

        var pdf = sut.ToPdf("Redditi 2026 – riepilogo", body);

        var pages = PdfTestReader.Pages(pdf);
        Assert.True(pages.Count > 1, $"{pages.Count} pages");
        Assert.All(pages, page =>
        {
            Assert.Equal(595, Math.Round(page.Width));
            Assert.Equal(842, Math.Round(page.Height));
        });
        var words = PdfTestReader.BodyWords(pdf);
        var lines = words.Select((w, i) => (w, i)).Where(x => x.w == "Riga").Select(x => words[x.i + 1]);
        Assert.Equal(Enumerable.Range(1, 400).Select(i => $"{i:D3}"), lines);
        Assert.Contains("Redditi 2026 – riepilogo", PdfTestReader.Text(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_ExcludeUnsettledPayments_AndUseProcessedAtTaxYear()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Casa Report");
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            Property = property,
            CheckInDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc),
            Source = BookingSource.Airbnb,
        };
        var pendingPayment = new Payment
        {
            OrgId = orgId,
            BookingId = booking.Id,
            Booking = booking,
            Amount = 100m,
            Status = PaymentStatus.Pending,
            OtaWithholdingTax = 21m,
            WithholdingTaxApplied = true,
            NetAmountAfterWithholding = 79m,
            CreatedAt = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
        };
        var completedPayment = new Payment
        {
            OrgId = orgId,
            BookingId = booking.Id,
            Booking = booking,
            Amount = 200m,
            Status = PaymentStatus.Completed,
            OtaWithholdingTax = 42m,
            WithholdingTaxApplied = true,
            NetAmountAfterWithholding = 158m,
            CreatedAt = new DateTime(2025, 12, 28, 0, 0, 0, DateTimeKind.Utc),
            ProcessedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
        };
        var partiallyRefundedPayment = new Payment
        {
            OrgId = orgId,
            BookingId = booking.Id,
            Booking = booking,
            Amount = 100m,
            RefundedAmount = 40m,
            Status = PaymentStatus.PartiallyRefunded,
            OtaWithholdingTax = 21m,
            WithholdingTaxApplied = true,
            NetAmountAfterWithholding = 79m,
            CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            ProcessedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        var refundedPayment = new Payment
        {
            OrgId = orgId,
            BookingId = booking.Id,
            Booking = booking,
            Amount = 50m,
            RefundedAmount = 50m,
            Status = PaymentStatus.Refunded,
            OtaWithholdingTax = 10.50m,
            WithholdingTaxApplied = true,
            NetAmountAfterWithholding = 39.50m,
            CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            ProcessedAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Bookings.Add(booking);
        db.Payments.AddRange(pendingPayment, completedPayment, partiallyRefundedPayment, refundedPayment);
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var annual = await sut.GetAnnualReportAsync(orgId, TaxYear);
        var annualLine = Assert.Single(annual.Properties);
        Assert.Equal(260m, annualLine.GrossIncome);
        Assert.Equal(54.60m, annualLine.Withholding);
        Assert.Equal(205.40m, annualLine.Net);

        var withholding = await sut.GetWithholdingReportAsync(orgId, TaxYear);
        Assert.Equal(2, withholding.Lines.Count);
        var withholdingLine = Assert.Single(withholding.Lines, l => l.PaymentId == completedPayment.Id);
        Assert.Equal(completedPayment.Id, withholdingLine.PaymentId);
        Assert.Equal(completedPayment.ProcessedAt!.Value, withholdingLine.PaidAt);
        var bucket = Assert.Single(withholding.ByOta);
        Assert.Equal(260m, bucket.Gross);
        Assert.Equal(54.60m, bucket.Withholding);
        Assert.Equal(205.40m, bucket.Net);
        Assert.Equal(2, bucket.PayoutCount);
    }

    private static FiscalService CreateService(AppDbContext db, ShortStayFiscalOptions? options = null) =>
        new(db, new MigraDocPdfDocumentRenderer(), Options.Create(options ?? new ShortStayFiscalOptions()));

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options, NullTenantContext.Instance);
    }

    private static void SeedOrg(AppDbContext db, Guid orgId, bool hasPartitaIva = false, string? fiscalCode = null)
    {
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
    }

    private static Property SeedProperty(AppDbContext db, Guid orgId, string name)
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = "auth0|host",
            Name = name,
            Address = "Via Test 1",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
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
        BookingStatus status = BookingStatus.Confirmed,
        BookingSource source = BookingSource.Direct)
    {
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(nights),
            Status = status,
            Source = source,
        };
        db.Bookings.Add(booking);
        return booking;
    }

    private static void SeedLease(AppDbContext db, Guid orgId, Property property) =>
        db.LeaseContracts.Add(new LeaseContract
        {
            OrgId = orgId,
            PropertyId = property.Id,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 800m,
            RegistrationDeadline = DateTime.UtcNow.AddDays(30),
        });
}
