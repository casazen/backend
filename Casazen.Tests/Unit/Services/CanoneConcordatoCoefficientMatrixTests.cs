using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LT-15 (A7-29): the matrix of the concordato rent range over band x sub-fascia x zone x term, against an oracle typed
/// by hand from the verified tables of <c>.claude/context/regulations/canone_concordato.md</c> (agreement MB, F1 allegati
/// 1/U and 2/S, class U). It is deliberately independent of <see cref="CanoneConcordatoMbSeed"/>: a typo in the seed or a
/// change of the formula breaks a cell. Seveso and Cesano Maderno stay <c>Partial</c> (see "Esito" in the context file),
/// so every numeric range is indicative.
/// Surfaces are chosen inside their band and away from the optional surface coefficients (not under 40, not in 50-60
/// and not above 120 sqm), which have their own tests in <see cref="CanoneConcordatoEligibilityServiceTests"/>.
/// </summary>
public class CanoneConcordatoCoefficientMatrixTests
{
    /// <summary>Verified €/mq annui as (min, max) of sub-fascia 1, 2, 3 per surface band, per zone.</summary>
    private static readonly Dictionary<(string City, string Zone, int Band), decimal[]> Tables = new()
    {
        // Seveso, allegato 2/S (F1 p. 63).
        [("Seveso", "Unica", 0)] = [20, 57, 58, 91, 92, 109],
        [("Seveso", "Unica", 1)] = [20, 52, 53, 85, 86, 100],
        [("Seveso", "Unica", 2)] = [20, 45, 46, 71, 72, 86],
        [("Seveso", "Unica", 3)] = [20, 41, 42, 62, 63, 76],
        // Cesano Maderno, allegato 1/U (F1 p. 39).
        [("Cesano Maderno", "Centrale", 0)] = [20, 65, 66, 102, 103, 120],
        [("Cesano Maderno", "Centrale", 1)] = [20, 60, 61, 94, 95, 110],
        [("Cesano Maderno", "Centrale", 2)] = [20, 50, 51, 80, 81, 95],
        [("Cesano Maderno", "Centrale", 3)] = [20, 45, 46, 70, 71, 85],
        [("Cesano Maderno", "Semi periferica", 0)] = [20, 55, 56, 90, 91, 105],
        [("Cesano Maderno", "Semi periferica", 1)] = [20, 50, 51, 85, 86, 100],
        [("Cesano Maderno", "Semi periferica", 2)] = [20, 45, 46, 70, 71, 83],
        [("Cesano Maderno", "Semi periferica", 3)] = [20, 40, 41, 60, 61, 73],
    };

    /// <summary>One surface per band, none touching an optional surface coefficient: "Fino a 50", 51-74, 75-99, "Oltre 100".</summary>
    private static readonly decimal[] BandSqm = [45m, 65m, 85m, 110m];

    /// <summary>Term uplift of the agreement for 3, 4, 5 and 6 years (F1 p. 9): none, 3, 5 and 6 per cent.</summary>
    private static readonly (string End, decimal Factor)[] Terms =
    [
        ("2029-08-31", 1.00m),
        ("2030-08-31", 1.03m),
        ("2031-08-31", 1.05m),
        ("2032-08-31", 1.06m),
    ];

    public static IEnumerable<object[]> Cells()
    {
        foreach (var ((city, zone, band), _) in Tables)
            foreach (var subFascia in new[] { 1, 2, 3 })
                foreach (var (end, _) in Terms)
                    yield return [city, zone, band, subFascia, end];
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Calculate_VerifiedCell_MatchesTheHandTypedOracle(string city, string zone, int band, int subFascia, string end)
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, city);
        db.TerritorialRentAgreements.AddRange(CanoneConcordatoMbSeed.BuildAgreements());
        db.HighTensionAreaComuni.AddRange(CanoneConcordatoMbSeed.BuildAtaCandidates());
        await db.SaveChangesAsync();
        var sut = CreateSut(db);
        var sqm = BandSqm[band];
        var factor = Terms.Single(t => t.End == end).Factor;

        var result = await sut.CalculateAsync(
            property.Id, CharacteristicsOf(subFascia, sqm, city == "Seveso" ? null : zone), Term("2026-09-01", end));

        var row = Tables[(city, zone, band)];
        Assert.NotNull(result);
        Assert.True(result.Available);
        Assert.Equal(subFascia, result.SubFascia);
        Assert.Equal(zone, result.Zone);
        // The parties may go down to the sub-fascia 1 minimum and up to the maximum of the unit's sub-fascia.
        Assert.Equal(Math.Round(row[0] * sqm * factor, 2), result.CanoneMinAnnuo);
        Assert.Equal(Math.Round(row[subFascia * 2 - 1] * sqm * factor, 2), result.CanoneMaxAnnuo);
        // Seveso and Cesano are Partial in the verified data: the range is a hint, not a bound.
        Assert.Equal(DataCompleteness.Partial, result.DataCompleteness);
        Assert.True(result.Indicative);
        Assert.Contains(CanoneConcordatoWarningCodes.PartialData, result.Warnings);
    }

    [Fact]
    public void Oracle_CoversEveryNumericCellOfTheSeed()
    {
        // 24 values of Seveso + 48 of Cesano = 72 (context file, "Tabelle dei canoni"): the oracle has all of them.
        Assert.Equal(12, Tables.Count);
        Assert.Equal(72, Tables.Values.Sum(v => v.Length));
        var seeded = CanoneConcordatoMbSeed.BuildAgreements().Where(a => a.Bands.Count > 0).SelectMany(a => a.Bands).ToList();
        Assert.Equal(12, seeded.Count);
    }

    [Theory]
    [InlineData("Misinto")]
    [InlineData("Monza")]
    public async Task Calculate_ComuneCoveredButNotVerified_HasNoNumericRange(string city)
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, city);
        db.TerritorialRentAgreements.AddRange(CanoneConcordatoMbSeed.BuildAgreements());
        db.HighTensionAreaComuni.AddRange(CanoneConcordatoMbSeed.BuildAtaCandidates());
        await db.SaveChangesAsync();

        var result = await CreateSut(db).CalculateAsync(property.Id, CharacteristicsOf(2, 65m, null), Term("2026-09-01", "2029-08-31"));

        Assert.NotNull(result);
        Assert.False(result.Available);
        Assert.Null(result.CanoneMinAnnuo);
        Assert.Null(result.CanoneMaxAnnuo);
    }

    [Theory]
    // Optional coefficients are cumulative and add (default CoefficientCombination): furniture +15 % and air conditioning
    // +5 % on the maximum only, on top of the 3-year term (no uplift). Seveso, sub-fascia 2, 65 sqm: 85 €/mq.
    [InlineData(false, false, 5525.00)]
    [InlineData(true, false, 6353.75)]   // 85 x 65 x 1,15
    [InlineData(false, true, 5801.25)]   // 85 x 65 x 1,05
    [InlineData(true, true, 6630.00)]    // 85 x 65 x 1,20
    public async Task Calculate_FurnitureAndAirConditioning_AddOnTheMaximumOnly(bool furnished, bool airConditioning, double expectedMax)
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        db.TerritorialRentAgreements.AddRange(CanoneConcordatoMbSeed.BuildAgreements());
        await db.SaveChangesAsync();
        var characteristics = CharacteristicsOf(2, 65m, null) with { IsFurnished = furnished, AirConditioning = airConditioning };

        var result = await CreateSut(db).CalculateAsync(property.Id, characteristics, Term("2026-09-01", "2029-08-31"));

        Assert.True(result!.Available);
        Assert.Equal(1300.00m, result.CanoneMinAnnuo);
        Assert.Equal((decimal)expectedMax, result.CanoneMaxAnnuo);
    }

    private static RentBandCharacteristics CharacteristicsOf(int subFascia, decimal sqm, string? zone) => subFascia switch
    {
        // Missing one A element: sub-fascia 1 whatever the rest.
        1 => new RentBandCharacteristics { Sqm = sqm, TypeAElementCount = 1, TypeBElementCount = 3, ZoneName = zone },
        // All A and 3 B, fewer than 3 C: sub-fascia 2.
        2 => new RentBandCharacteristics { Sqm = sqm, TypeAElementCount = 2, TypeBElementCount = 3, ZoneName = zone },
        // All A, 3 B, 3 C and 2 qualifying D: sub-fascia 3.
        _ => new RentBandCharacteristics
        {
            Sqm = sqm,
            TypeAElementCount = 2,
            TypeBElementCount = 3,
            TypeCElementCount = 3,
            TypeDElementCount = 2,
            QualifyingTypeDElementCount = 2,
            ZoneName = zone,
        },
    };

    private static readonly DateTimeOffset Today = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static ICanoneConcordatoEligibilityService CreateSut(AppDbContext db) =>
        new CanoneConcordatoEligibilityService(
            new TerritorialRentAgreementRepository(db),
            new HighTensionAreaComuneRepository(db),
            new PropertyRepository(db),
            new FakeTimeProvider(Today));

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, NullTenantContext.Instance);

    private static LeaseTerm Term(string start, string end) =>
        LeaseTerm.Between(
            DateTime.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
            DateTime.Parse(end, System.Globalization.CultureInfo.InvariantCulture))!.Value;

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
            OwnerId = "auth0|host",
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
}
