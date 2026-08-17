using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CanoneConcordatoEligibilityServiceTests
{
    private const string OwnerId = "auth0|host";

    [Fact]
    public async Task Calculate_Seveso_AllA_AtLeast3B_SubFascia2_FromSeededBand()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, OwnerId, Characteristics(65, typeA: 2, typeB: 3, typeC: 0, typeD: 0));

        Assert.NotNull(result);
        Assert.True(result.Available);
        Assert.Equal(2, result.SubFascia);
        Assert.Equal("Unica", result.Zone);
        Assert.Equal(3445.00m, result.CanoneMinAnnuo);
        Assert.Equal(5525.00m, result.CanoneMaxAnnuo);
        Assert.Equal(287.08m, result.CanoneMinMensile);
        Assert.Equal(460.42m, result.CanoneMaxMensile);
        Assert.True(result.ImuAppliesTheoretical);
        Assert.True(result.AttestationRequired);
        Assert.False(result.AtaApplies);
        Assert.Contains("informativa", result.Disclaimer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DataCompleteness.Partial, result.DataCompleteness);
    }

    [Fact]
    public async Task Calculate_TwoTypeB_IsSubFascia1_NotFascia2()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, OwnerId, Characteristics(65, typeA: 2, typeB: 2, typeC: 0, typeD: 0));

        Assert.NotNull(result);
        Assert.Equal(1, result.SubFascia);
        Assert.Equal(1300.00m, result.CanoneMinAnnuo);
        Assert.Equal(3380.00m, result.CanoneMaxAnnuo);
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
        var unverified = await sut.CalculateAsync(property.Id, OwnerId, Characteristics(65, 2, 3, 0, 0));
        Assert.False(unverified!.AtaApplies);

        ata.VerifiedDirectly = true;
        await db.SaveChangesAsync();
        var verified = await sut.CalculateAsync(property.Id, OwnerId, Characteristics(65, 2, 3, 0, 0));
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

        var result = await sut.CalculateAsync(property.Id, OwnerId, Characteristics(65, 2, 3, 0, 0));

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

        var result = await sut.CalculateAsync(property.Id, OwnerId, Characteristics(65, 2, 3, 0, 0));

        Assert.False(result!.Available);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Null(result.CanoneMinAnnuo);
        Assert.Null(result.CanoneMaxAnnuo);
        Assert.Equal(DataCompleteness.Missing, result.DataCompleteness);
    }

    [Fact]
    public async Task Calculate_CesanoWithoutZone_NoBlendedRange()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Cesano Maderno");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, OwnerId, Characteristics(65, 2, 3, 0, 0));

        Assert.False(result!.Available);
        Assert.Equal(CanoneConcordatoCopy.ReasonZoneRequired, result.Reason);
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
            property.Id, OwnerId, Characteristics(65, 2, 3, 0, 0, zone: "Centrale"));

        Assert.True(result!.Available);
        Assert.Equal("Centrale", result.Zone);
        Assert.Equal(3965.00m, result.CanoneMinAnnuo);
        Assert.Equal(6110.00m, result.CanoneMaxAnnuo);
    }

    [Fact]
    public async Task Calculate_UnknownOwner_ReturnsNull()
    {
        await using var db = CreateDb();
        var property = SeedProperty(db, "Seveso");
        SeedReference(db);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var result = await sut.CalculateAsync(property.Id, "auth0|other", Characteristics(65, 2, 3, 0, 0));

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

        var result = await sut.CalculateAsync(property.Id, OwnerId, Characteristics(65, 2, 3, 0, 0));

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

        Assert.Equal(52, missing.Count);
        Assert.All(missing, a => Assert.Empty(a.Bands));
        Assert.Equal(54, CanoneConcordatoMbSeed.ProvinceComuni.Length);
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

        var result = await sut.GetSignatoryOrganizationsAsync(property.Id, OwnerId);

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
    public async Task Controller_Eligibility_OtherOwner_Returns404()
    {
        var eligibility = new Mock<ICanoneConcordatoEligibilityService>();
        eligibility
            .Setup(s => s.CalculateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<RentBandCharacteristics>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CanoneConcordatoEligibilityDto?)null);
        var controller = new CanoneConcordatoController(eligibility.Object, Mock.Of<IAttestationGuidanceService>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, OwnerId)], "test")),
            },
        };

        var result = await controller.GetEligibility(Guid.NewGuid(), 65, 2, 3, 0, 0, false, 3, null, null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Controller_Eligibility_OwnedProperty_ReturnsDtoShape()
    {
        var dto = new CanoneConcordatoEligibilityDto(
            true, null, "Seveso", "Unica", 2, 3445m, 5525m, 287.08m, 460.42m,
            DataCompleteness.Partial, true, false, true, CanoneConcordatoCopy.Disclaimer);
        var eligibility = new Mock<ICanoneConcordatoEligibilityService>();
        eligibility
            .Setup(s => s.CalculateAsync(It.IsAny<Guid>(), OwnerId, It.IsAny<RentBandCharacteristics>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);
        var controller = new CanoneConcordatoController(eligibility.Object, Mock.Of<IAttestationGuidanceService>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, OwnerId)], "test")),
            },
        };

        var result = await controller.GetEligibility(Guid.NewGuid(), 65, 2, 3, 0, 0, false, 3, "Unica", null, CancellationToken.None);

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
    }

    private static ICanoneConcordatoEligibilityService CreateSut(AppDbContext db) =>
        new CanoneConcordatoEligibilityService(
            new TerritorialRentAgreementRepository(db),
            new HighTensionAreaComuneRepository(db),
            new PropertyRepository(db));

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

    private static RentBandCharacteristics Characteristics(
        decimal sqm, int typeA, int typeB, int typeC, int typeD,
        bool furnished = false, int years = 3, string? zone = null, string? foglio = null) =>
        new(sqm, typeA, typeB, typeC, typeD, furnished, years, zone, foglio);
}
