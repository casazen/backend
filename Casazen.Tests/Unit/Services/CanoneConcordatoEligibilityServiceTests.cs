using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CanoneConcordatoEligibilityServiceTests
{
    private const string OwnerId = "auth0|host";

    [Fact]
    public async Task Calculate_Seveso65Sqm_SubFascia2_RangeFromSubFascia1MinToSubFascia2Max()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, typeA: 2, typeB: 3, typeC: 0, typeD: 0), ThreeYears);

        Assert.NotNull(result);
        Assert.True(result.Available);
        Assert.Equal(2, result.SubFascia);
        Assert.Equal("Unica", result.Zone);
        // RS-8 (A7-11, #6): the parties may choose a lower sub-fascia, so the minimum is the sub-fascia 1 minimum (20 €/mq).
        Assert.Equal(1300.00m, result.CanoneMinAnnuo);
        Assert.Equal(5525.00m, result.CanoneMaxAnnuo);
        // Monthly bounds rounded inwards: twelve months never exceed the annual maximum.
        Assert.Equal(108.34m, result.CanoneMinMensile);
        Assert.Equal(460.41m, result.CanoneMaxMensile);
        Assert.True(result.ImuAppliesTheoretical);
        Assert.True(result.AttestationRequired);
        Assert.False(result.AtaApplies);
        Assert.Contains("informativa", result.Disclaimer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DataCompleteness.Partial, result.DataCompleteness);
        // A7-23: Partial data make the range indicative, with the source and the verification date.
        Assert.True(result.Indicative);
        Assert.Contains(CanoneConcordatoWarningCodes.PartialData, result.Warnings);
        Assert.Equal(CanoneConcordatoMbSeed.SourceUrl, result.SourceUrl);
        Assert.Equal(new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc), result.LastVerifiedAt);
        Assert.Equal(3, result.ContractYears);
        Assert.Equal(65m, result.UsableSqm);
        Assert.Equal(50, result.BandMinSqm);
        Assert.Equal(74, result.BandMaxSqm);
    }

    [Fact]
    public async Task Calculate_TwoTypeB_IsSubFascia1_NotFascia2()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, typeA: 2, typeB: 2, typeC: 0, typeD: 0), ThreeYears);

        Assert.NotNull(result);
        Assert.Equal(1, result.SubFascia);
        Assert.Equal(1300.00m, result.CanoneMinAnnuo);
        Assert.Equal(3380.00m, result.CanoneMaxAnnuo);
    }

    [Theory]
    // A7-10: the integer bands of the agreement left decimals without a band; the bands are contiguous half-open
    // intervals looked up on the usable square metres rounded to the whole metre.
    [InlineData(50.5, 50, 74, 4721.75)]   // (50,74]: 85 €/mq × min(50,5 × 1,10; 60) = 85 × 55,55
    [InlineData(74.5, 74, 99, 5289.50)]   // (74,99]: 71 €/mq × 74,5
    [InlineData(99.5, 99, null, 6169.00)] // (99,∞): 62 €/mq × 99,5
    [InlineData(50, 0, 50, 4550.00)]      // (0,50]: 91 €/mq × 50, no +10% at exactly 50 mq
    [InlineData(100, 99, null, 6200.00)]  // 100 mq is "Oltre 100"
    public async Task Calculate_DecimalAndBoundarySurfaces_FallInTheRightBand(
        decimal sqm, int expectedBandMin, int? expectedBandMax, decimal expectedMax)
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(sqm, 2, 3, 0, 0), ThreeYears);

        Assert.True(result!.Available);
        Assert.Equal(expectedBandMin, result.BandMinSqm);
        Assert.Equal(expectedBandMax, result.BandMaxSqm);
        Assert.Equal(expectedMax, result.CanoneMaxAnnuo);
    }

    [Theory]
    // RS-8 examples (Seveso, sub-fascia 2, 3 years): surface uplifts capped, reduction floored (A7-11).
    [InlineData(35, 700.00, 3640.00)]   // 91 × min(35 × 1,20; 40) = 91 × 40
    [InlineData(58, 1160.00, 5100.00)]  // 85 × min(58 × 1,10; 60) = 85 × 60
    [InlineData(130, 2400.00, 7440.00)] // 62 × max(130 × 0,80; 120) = 62 × 120; minimum 20 × 120
    [InlineData(30, 600.00, 3276.00)]   // 91 × 30 × 1,20 = 91 × 36, under the cap
    [InlineData(160, 2560.00, 7936.00)] // 62 × 160 × 0,80 = 62 × 128, above the floor
    public async Task Calculate_SurfaceCoefficients_ApplyCapsAndFloors(decimal sqm, decimal expectedMin, decimal expectedMax)
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(sqm, 2, 3, 0, 0), ThreeYears);

        Assert.True(result!.Available);
        Assert.Equal(expectedMin, result.CanoneMinAnnuo);
        Assert.Equal(expectedMax, result.CanoneMaxAnnuo);
    }

    [Theory]
    // Term from the dates: 4, 5, 6 years raise minimum and maximum by 3, 5, 6 %; none above 6 years.
    [InlineData("2030-08-31", 4, 1339.00, 5690.75, false)]
    [InlineData("2031-08-31", 5, 1365.00, 5801.25, false)]
    [InlineData("2032-08-31", 6, 1378.00, 5856.50, false)]
    [InlineData("2033-02-28", 6, 1300.00, 5525.00, true)]
    [InlineData("2033-08-31", 7, 1300.00, 5525.00, true)]
    public async Task Calculate_TermUplift_ComesFromTheDates(
        string endDate, int expectedYears, decimal expectedMin, decimal expectedMax, bool overSixYears)
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(
            property.Id, Characteristics(65, 2, 3, 0, 0), Term("2026-09-01", endDate));

        Assert.True(result!.Available);
        Assert.Equal(expectedYears, result.ContractYears);
        Assert.Equal(expectedMin, result.CanoneMinAnnuo);
        Assert.Equal(expectedMax, result.CanoneMaxAnnuo);
        Assert.Equal(overSixYears, result.Warnings.Contains(CanoneConcordatoWarningCodes.NoDurationUpliftOverSixYears));
    }

    [Theory]
    [InlineData("2027-08-31")]
    [InlineData("2029-08-30")]
    public async Task Calculate_TermShorterThanThreeYears_IsUnavailable(string endDate)
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), Term("2026-09-01", endDate));

        Assert.False(result!.Available);
        Assert.Equal(CanoneConcordatoReasonCodes.TermTooShort, result.ReasonCode);
        Assert.Null(result.CanoneMaxAnnuo);
    }

    [Fact]
    public async Task Calculate_FurnishedAndAirConditioning_AddToTheMaximumOnly()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(
            property.Id, Characteristics(65, 2, 3, 0, 0) with { IsFurnished = true, AirConditioning = true }, ThreeYears);

        // 85 × 65 × (1 + 15% + 5%): the optional uplifts raise the maximum, the minimum keeps the mandatory ones only.
        Assert.Equal(6630.00m, result!.CanoneMaxAnnuo);
        Assert.Equal(1300.00m, result.CanoneMinAnnuo);
    }

    [Fact]
    public async Task Calculate_MultiplicativeCombination_IsConfigurablePerAgreement()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        db.TerritorialRentAgreements.Single(a => a.Comune == "Seveso").CoefficientCombination =
            CoefficientCombination.Multiplicative;
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(
            property.Id, Characteristics(65, 2, 3, 0, 0) with { IsFurnished = true, AirConditioning = true },
            Term("2026-09-01", "2030-08-31"));

        // 85 × 65 × 1,15 × 1,05 × 1,03 instead of × (1 + 15% + 5% + 3%).
        Assert.Equal(6871.58m, result!.CanoneMaxAnnuo);
    }

    [Fact]
    public async Task Calculate_Appurtenances_AddToTheUsableSurfaceAtTheAgreementPercentages()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(
            property.Id,
            Characteristics(60, 2, 3, 0, 0) with
            {
                GarageSqm = 10m,
                BalconySqm = 10m,
                OtherAppurtenanceSqm = 4m,
                PrivateGreenSqm = 10m,
            },
            ThreeYears);

        // 60 + 10 × 50% + 10 × 30% + 4 × 25% + 10 × 10% = 70 mq utili, band (50,74], 85 €/mq.
        Assert.Equal(70m, result!.UsableSqm);
        Assert.Equal(5950.00m, result.CanoneMaxAnnuo);
    }

    [Theory]
    [InlineData(3, 1)] // stoves with fewer than 4 B-elements: sub-fascia 1
    [InlineData(4, 2)] // stoves with at least 4 B-elements: the stove rule does not apply
    public async Task Calculate_StoveHeating_PutsTheUnitInSubFascia1UnlessFourTypeB(int typeB, int expectedSubFascia)
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(
            property.Id, Characteristics(65, 2, typeB, 0, 0) with { StoveHeating = true }, ThreeYears);

        Assert.Equal(expectedSubFascia, result!.SubFascia);
    }

    [Theory]
    // Sub-fascia 3: 3 C-elements and at least 2 of D1, D2, D4, D6, D7, D9; the maximum of sub-fascia 3 needs 4 D.
    [InlineData(5, 1, 2, false)]
    [InlineData(2, 2, 3, true)]
    [InlineData(4, 2, 3, false)]
    public async Task Calculate_SubFascia3_NeedsQualifyingTypeDAndFlagsTheMaximumBelowFourD(
        int typeD, int qualifyingD, int expectedSubFascia, bool expectedWarning)
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(
            property.Id,
            Characteristics(65, 2, 3, 3, typeD) with { QualifyingTypeDElementCount = qualifyingD },
            ThreeYears);

        Assert.Equal(expectedSubFascia, result!.SubFascia);
        Assert.Equal(expectedWarning, result.Warnings.Contains(CanoneConcordatoWarningCodes.SubFascia3MaxNeedsMoreTypeD));
        Assert.Equal(CanoneConcordatoMbSeed.SubFascia3QualifyingTypeDElements, result.SubFascia3QualifyingTypeDElements);
    }

    [Fact]
    public async Task Calculate_MoreQualifyingThanTypeD_IsUnavailable()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(
            property.Id, Characteristics(65, 2, 3, 3, 1) with { QualifyingTypeDElementCount = 2 }, ThreeYears);

        Assert.False(result!.Available);
        Assert.Equal(CanoneConcordatoReasonCodes.InvalidElementCounts, result.ReasonCode);
    }

    [Fact]
    public async Task Calculate_CompleteData_IsNotIndicative()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        db.TerritorialRentAgreements.Single(a => a.Comune == "Seveso").DataCompleteness = DataCompleteness.Complete;
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), ThreeYears);

        Assert.False(result!.Indicative);
        Assert.DoesNotContain(CanoneConcordatoWarningCodes.PartialData, result.Warnings);
    }

    [Fact]
    public async Task Calculate_TodayPastAgreementExpiry_FlagsAgreementExpiredAndExposesItsFields()
    {
        // Reality today (RS-8, class U): the MB agreement's 18-month formal term ran out on 2025-11-01, but art. 14
        // keeps it in force until a new one is signed — a warning, never a block (LT-13, A7-22).
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), ThreeYears);

        Assert.True(result!.Available);
        Assert.Contains(CanoneConcordatoWarningCodes.AgreementExpired, result.Warnings);
        Assert.Equal(CanoneConcordatoMbSeed.ExpiresAt, result.AgreementExpiresAt);
        Assert.True(result.AgreementRemainsInForceUntilReplaced);
    }

    [Fact]
    public async Task Calculate_TodayBeforeAgreementExpiry_NoAgreementExpiredWarning()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db, new FakeTimeProvider(new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero)));

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), ThreeYears);

        Assert.DoesNotContain(CanoneConcordatoWarningCodes.AgreementExpired, result!.Warnings);
    }

    [Fact]
    public async Task GetZonesAsync_Cesano_ReturnsZonesWithCadastralSheets()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Cesano Maderno");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.GetZonesAsync(property.Id);

        Assert.NotNull(result);
        Assert.True(result.Available);
        Assert.Equal("Cesano Maderno", result.Comune);
        Assert.Equal(DataCompleteness.Partial, result.DataCompleteness);
        var zoneNames = result.Zones.Select(z => z.Name).ToList();
        Assert.Contains("Centrale", zoneNames);
        Assert.Contains("Semi periferica", zoneNames);
        var centrale = result.Zones.Single(z => z.Name == "Centrale");
        Assert.Contains("1", centrale.CadastralSheets);
        Assert.Contains("33", centrale.CadastralSheets);
    }

    [Fact]
    public async Task GetZonesAsync_SevesoSingleZoneWithoutSheets_ReturnsEmptySheets()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.GetZonesAsync(property.Id);

        Assert.NotNull(result);
        var zone = Assert.Single(result.Zones);
        Assert.Equal("Unica", zone.Name);
        Assert.Empty(zone.CadastralSheets);
    }

    [Fact]
    public async Task GetZonesAsync_ComuneWithoutUsableAgreement_NotAvailableWithNoZones()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Monza");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.GetZonesAsync(property.Id);

        Assert.NotNull(result);
        Assert.False(result.Available);
        Assert.Empty(result.Zones);
    }

    [Fact]
    public async Task GetZonesAsync_PropertyNotVisible_ReturnsNull()
    {
        await using var db = CreateDb();
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.GetZonesAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task Calculate_SevesoWithUnknownZone_ReturnsZoneNotFound()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(
            property.Id, Characteristics(65, 2, 3, 0, 0, zone: "Centrale"), ThreeYears);

        Assert.False(result!.Available);
        Assert.Equal(CanoneConcordatoReasonCodes.ZoneNotFound, result.ReasonCode);
        Assert.Equal(CanoneConcordatoCopy.ReasonZoneNotFound, result.Reason);
        Assert.Null(result.CanoneMinAnnuo);
        Assert.Null(result.CanoneMaxAnnuo);
    }

    [Fact]
    public async Task Calculate_SevesoWithAnySheet_UsesTheSingleZone()
    {
        // Seveso is one zone over the whole comune: every cadastral sheet is in it.
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0, foglio: "999"), ThreeYears);

        Assert.True(result!.Available);
        Assert.Equal("Unica", result.Zone);
    }

    [Fact]
    public async Task Calculate_AtaApplies_OnlyWhenVerifiedDirectly()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var ata = db.HighTensionAreaComuni.Single(c => c.Comune == "Seveso");
        var sut = CreateSut(db);
        var unverified = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), ThreeYears);
        Assert.False(unverified!.AtaApplies);

        ata.VerifiedDirectly = true;
        await db.SaveChangesAsync();
        var verified = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), ThreeYears);
        Assert.True(verified!.AtaApplies);
    }

    [Fact]
    public async Task Calculate_DoesNotTreatAgreementCoverageAsAta()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        db.HighTensionAreaComuni.RemoveRange(db.HighTensionAreaComuni);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), ThreeYears);

        Assert.True(result!.Available);
        Assert.False(result.AtaApplies);
    }

    [Fact]
    public async Task Calculate_MissingComune_NoNumericRange()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Monza");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), ThreeYears);

        Assert.False(result!.Available);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Null(result.CanoneMinAnnuo);
        Assert.Null(result.CanoneMaxAnnuo);
        Assert.Equal(DataCompleteness.Missing, result.DataCompleteness);
        Assert.Equal(CanoneConcordatoReasonCodes.DataUnavailable, result.ReasonCode);
    }

    [Fact]
    public async Task Calculate_CesanoWithoutZone_NoBlendedRange()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Cesano Maderno");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), ThreeYears);

        Assert.False(result!.Available);
        Assert.Equal(CanoneConcordatoCopy.ReasonZoneRequired, result.Reason);
        Assert.Equal(CanoneConcordatoReasonCodes.ZoneRequired, result.ReasonCode);
        Assert.Null(result.CanoneMinAnnuo);
    }

    [Fact]
    public async Task Calculate_CesanoWithZone_ReturnsThatZoneOnly()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Cesano Maderno");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(
            property.Id, Characteristics(65, 2, 3, 0, 0, zone: "Centrale"), ThreeYears);

        Assert.True(result!.Available);
        Assert.Equal("Centrale", result.Zone);
        Assert.Equal(1300.00m, result.CanoneMinAnnuo);
        Assert.Equal(6110.00m, result.CanoneMaxAnnuo);
    }

    [Fact]
    public async Task Calculate_CesanoWithoutZone_UsesThePropertySheet()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Cesano Maderno");
        property.CadastralSheet = "2";
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), ThreeYears);

        Assert.True(result!.Available);
        Assert.Equal("Semi periferica", result.Zone);
        Assert.Equal(5525.00m, result.CanoneMaxAnnuo);
    }

    [Fact]
    public async Task Calculate_CesanoWithConflictingZoneAndFoglio_ReturnsUnavailable()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Cesano Maderno");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(
            property.Id, Characteristics(65, 2, 3, 0, 0, zone: "Centrale", foglio: "2"), ThreeYears);

        Assert.False(result!.Available);
        Assert.Equal(CanoneConcordatoReasonCodes.ZoneNotFound, result.ReasonCode);
        Assert.Null(result.CanoneMinAnnuo);
        Assert.Null(result.CanoneMaxAnnuo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    public async Task Calculate_InvalidSqm_ReturnsUnavailableWithoutNumericRange(decimal sqm)
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(sqm, 2, 3, 0, 0), ThreeYears);

        Assert.False(result!.Available);
        Assert.Equal(CanoneConcordatoCopy.ReasonInvalidSqm, result.Reason);
        Assert.Equal(CanoneConcordatoReasonCodes.InvalidSurface, result.ReasonCode);
        Assert.Null(result.CanoneMinAnnuo);
        Assert.Null(result.CanoneMaxAnnuo);
    }

    [Fact]
    public async Task Calculate_UnknownProperty_ReturnsNull()
    {
        await using var db = CreateDb();
        SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        // Who may see the property is decided by the controller (TN-3); the service only needs it to exist.
        var result = await sut.CalculateAsync(Guid.NewGuid(), Characteristics(65, 2, 3, 0, 0), ThreeYears);

        Assert.Null(result);
    }

    [Fact]
    public async Task Calculate_UnknownCity_Unavailable()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Milano");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, Characteristics(65, 2, 3, 0, 0), ThreeYears);

        Assert.False(result!.Available);
        Assert.Null(result.CanoneMinAnnuo);
    }

    [Fact]
    public void EligibilityService_HasNoHardcodedCanoneLiterals()
    {
        var path = Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..",
            "Casazen.Infrastructure", "Services", "CanoneConcordatoEligibilityService.cs");
        var source = File.ReadAllText(Path.GetFullPath(path));
        Assert.DoesNotContain("3445", source);
        Assert.DoesNotContain("5525", source);
        Assert.DoesNotContain("53m", source);
        Assert.DoesNotContain("85m", source);
    }

    [Fact]
    public void MbSeed_MissingComuni_HaveNoBands()
    {
        var missing = CanoneConcordatoMbSeed.BuildAgreements()
            .Where(a => a.DataCompleteness == DataCompleteness.Missing)
            .ToList();

        // RS-8 (#1): the agreement covers 55 comuni, Misinto included.
        Assert.Equal(53, missing.Count);
        Assert.All(missing, a => Assert.Empty(a.Bands));
        Assert.Equal(55, CanoneConcordatoMbSeed.ProvinceComuni.Length);
        Assert.Contains("Misinto", CanoneConcordatoMbSeed.MissingComuni);
    }

    [Fact]
    public void MbSeed_PilotBands_AreContiguousHalfOpenIntervals()
    {
        foreach (var agreement in CanoneConcordatoMbSeed.BuildAgreements().Where(a => a.Bands.Count > 0))
        {
            foreach (var zone in agreement.Bands.GroupBy(b => b.ZoneName))
            {
                var bands = zone.OrderBy(b => b.MinSqm).ToList();
                Assert.Equal(0, bands[0].MinSqm);
                Assert.Null(bands[^1].MaxSqm);
                for (var i = 1; i < bands.Count; i++)
                    Assert.Equal(bands[i - 1].MaxSqm, bands[i].MinSqm);
                Assert.Equal(new[] { 0, 50, 74, 99 }, bands.Select(b => b.MinSqm));
            }
        }
    }

    [Fact]
    public void MbSeed_PilotSignatories_AreTheElevenOfTheAgreement()
    {
        foreach (var agreement in CanoneConcordatoMbSeed.BuildAgreements().Where(a => a.Bands.Count > 0))
        {
            // RS-8 (#14): 4 tenant and 7 owner organizations; an attestation needs one of each (F1 art. 12).
            Assert.Equal(11, agreement.Signatories.Count);
            Assert.Equal(4, agreement.Signatories.Count(s => s.Role == SignatoryRole.Inquilini));
            Assert.Equal(7, agreement.Signatories.Count(s => s.Role == SignatoryRole.Proprieta));
            Assert.DoesNotContain(agreement.Signatories, s => s.Contact.Contains('@'));
            Assert.Equal(new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc), agreement.LastVerifiedAt);
            Assert.Equal(DataCompleteness.Partial, agreement.DataCompleteness);
        }
    }

    [Fact]
    public void MbSeed_AtaCandidates_AreUnverified()
    {
        Assert.All(CanoneConcordatoMbSeed.BuildAtaCandidates(), c => Assert.False(c.VerifiedDirectly));
    }

    [Fact]
    public async Task Attestation_ReturnsSignatories_WithoutHttpClient()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = new AttestationGuidanceService(
            new TerritorialRentAgreementRepository(db),
            new PropertyRepository(db));

        var result = await sut.GetSignatoryOrganizationsAsync(property.Id);

        Assert.NotNull(result);
        Assert.True(result.Organizations.Count >= 1);
        Assert.All(result.Organizations, o =>
        {
            Assert.False(string.IsNullOrWhiteSpace(o.Name));
            Assert.False(string.IsNullOrWhiteSpace(o.Contact));
        });
        Assert.Null(typeof(AttestationGuidanceService).GetField(
            "_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        Assert.DoesNotContain(typeof(AttestationGuidanceService).GetConstructors()[0].GetParameters(),
            p => p.ParameterType.Name.Contains("Http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Controller_Eligibility_PropertyNotVisible_Returns404PropertyNotFound()
    {
        var eligibility = new Mock<ICanoneConcordatoEligibilityService>();
        var controller = CreateController(eligibility.Object, resource: null, authorized: true);

        var result = await controller.GetEligibility(Guid.NewGuid(), Query(), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        eligibility.Verify(
            s => s.CalculateAsync(It.IsAny<Guid>(), It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Controller_Eligibility_PropertyNotAllowed_Returns403WithoutCalculating()
    {
        var eligibility = new Mock<ICanoneConcordatoEligibilityService>();
        var controller = CreateController(
            eligibility.Object, new HostResource(Guid.NewGuid(), "auth0|other"), authorized: false);

        var result = await controller.GetEligibility(Guid.NewGuid(), Query(), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        eligibility.Verify(
            s => s.CalculateAsync(It.IsAny<Guid>(), It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Controller_Eligibility_AuthorizedProperty_ReturnsDtoShape()
    {
        var dto = new CanoneConcordatoEligibilityDto(
            true, null, "Seveso", "Unica", 2, 3445m, 5525m, 287.08m, 460.42m,
            DataCompleteness.Partial, true, false, true, CanoneConcordatoCopy.Disclaimer)
        {
            Indicative = true,
            Warnings = [CanoneConcordatoWarningCodes.PartialData],
        };
        LeaseTerm? usedTerm = null;
        var eligibility = new Mock<ICanoneConcordatoEligibilityService>();
        eligibility
            .Setup(s => s.CalculateAsync(It.IsAny<Guid>(), It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, RentBandCharacteristics _, LeaseTerm term, CancellationToken _) => usedTerm = term)
            .ReturnsAsync(dto);
        var controller = CreateController(
            eligibility.Object, new HostResource(Guid.NewGuid(), OwnerId), authorized: true);

        var result = await controller.GetEligibility(Guid.NewGuid(), Query(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<CanoneConcordatoEligibilityDto>(ok.Value);
        Assert.Equal("Seveso", body.Comune);
        Assert.Equal("Unica", body.Zone);
        Assert.Equal(2, body.SubFascia);
        Assert.Equal(3445m, body.CanoneMinAnnuo);
        Assert.Equal(5525m, body.CanoneMaxAnnuo);
        Assert.Equal(287.08m, body.CanoneMinMensile);
        Assert.Equal(460.42m, body.CanoneMaxMensile);
        Assert.Equal(DataCompleteness.Partial, body.DataCompleteness);
        Assert.True(body.ImuAppliesTheoretical);
        Assert.False(body.AtaApplies);
        Assert.True(body.AttestationRequired);
        Assert.Equal(CanoneConcordatoCopy.Disclaimer, body.Disclaimer);
        Assert.True(body.Indicative);
        // A7-12: the term comes from the dates of the query (1/9/2026-31/8/2030 = 4 years).
        Assert.Equal(new LeaseTerm(48, 0), usedTerm);
    }

    [Fact]
    public async Task Controller_Eligibility_EndBeforeStart_Returns422WithoutCalculating()
    {
        var eligibility = new Mock<ICanoneConcordatoEligibilityService>();
        var controller = CreateController(
            eligibility.Object, new HostResource(Guid.NewGuid(), OwnerId), authorized: true);
        var query = Query();
        query.EndDate = query.StartDate;

        var result = await controller.GetEligibility(Guid.NewGuid(), query, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
        eligibility.Verify(
            s => s.CalculateAsync(It.IsAny<Guid>(), It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Controller_Zones_AuthorizedProperty_ReturnsZonesDto()
    {
        var dto = new CanoneConcordatoZonesDto("Cesano Maderno", true, DataCompleteness.Partial,
            [new ConcordatoZoneDto("Centrale", ["1", "12"]), new ConcordatoZoneDto("Semi periferica", [])]);
        var eligibility = new Mock<ICanoneConcordatoEligibilityService>();
        eligibility.Setup(s => s.GetZonesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(dto);
        var controller = CreateController(
            eligibility.Object, new HostResource(Guid.NewGuid(), OwnerId), authorized: true);

        var result = await controller.GetZones(Guid.NewGuid(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<CanoneConcordatoZonesDto>(ok.Value);
        Assert.Equal("Cesano Maderno", body.Comune);
        Assert.Equal(2, body.Zones.Count);
    }

    [Fact]
    public async Task Controller_Zones_PropertyNotVisible_Returns404WithoutCalling()
    {
        var eligibility = new Mock<ICanoneConcordatoEligibilityService>();
        var controller = CreateController(eligibility.Object, resource: null, authorized: true);

        var result = await controller.GetZones(Guid.NewGuid(), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        eligibility.Verify(s => s.GetZonesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static CanoneConcordatoController CreateController(
        ICanoneConcordatoEligibilityService eligibility,
        HostResource? resource,
        bool authorized)
    {
        var hostResources = new Mock<IHostResourceLookup>();
        hostResources
            .Setup(h => h.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        return new CanoneConcordatoController(
            eligibility, Mock.Of<IAttestationGuidanceService>(), hostResources.Object, authorization.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", OwnerId)], "test")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    /// <summary>
    /// Fixed "today" for every test, after the MB agreement's formal 18-month expiry (2025-11-01): the reality today
    /// (RS-8, agreement not replaced), so <c>agreement_expired</c> is expected on the pilot comuni's warnings.
    /// </summary>
    private static readonly DateTimeOffset Today = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static ICanoneConcordatoEligibilityService CreateSut(AppDbContext db, TimeProvider? clock = null) =>
        new CanoneConcordatoEligibilityService(
            new TerritorialRentAgreementRepository(db),
            new HighTensionAreaComuneRepository(db),
            new PropertyRepository(db),
            clock ?? new FakeTimeProvider(Today));

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options, NullTenantContext.Instance);
    }

    private static void SeedReference(AppDbContext db)
    {
        db.TerritorialRentAgreements.AddRange(CanoneConcordatoMbSeed.BuildAgreements());
        db.HighTensionAreaComuni.AddRange(CanoneConcordatoMbSeed.BuildAtaCandidates());
    }

    private static Property SeedProperty(AppDbContext db, string city)
    {
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Host",
            Slug = $"org-{orgId:N}"[..20],
            DisplayName = "Host",
            ContactEmail = "h@example.com",
        });
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = OwnerId,
            Name = "Alloggio",
            Address = "Via Test 1",
            City = city,
            PostalCode = "20822",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 0m,
            CinCode = $"IT-{Guid.NewGuid():N}"[..16],
            IsActive = true,
        };
        db.Properties.Add(property);
        return property;
    }

    private static readonly LeaseTerm ThreeYears = Term("2026-09-01", "2029-08-31");

    private static LeaseTerm Term(string start, string end) =>
        LeaseTerm.Between(
            DateTime.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
            DateTime.Parse(end, System.Globalization.CultureInfo.InvariantCulture))!.Value;

    private static CanoneConcordatoRangeQuery Query() => new()
    {
        Sqm = 65,
        TypeACount = 2,
        TypeBCount = 3,
        Zone = "Unica",
        StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
    };

    private static RentBandCharacteristics Characteristics(
        decimal sqm, int typeA, int typeB, int typeC, int typeD,
        bool furnished = false, string? zone = null, string? foglio = null) =>
        new()
        {
            Sqm = sqm,
            TypeAElementCount = typeA,
            TypeBElementCount = typeB,
            TypeCElementCount = typeC,
            TypeDElementCount = typeD,
            QualifyingTypeDElementCount = typeD,
            IsFurnished = furnished,
            ZoneName = zone,
            CadastralSheet = foglio,
        };
}
