using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SupplierMatchServiceTests
{
    [Fact]
    public async Task MatchAsync_WithActiveSupplier_ReturnsRecommended()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, supplierOrgId) = await SeedAsync(db);

        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), Mock.Of<IAiProvider>(), aiEnabled: true);

        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.NotNull(result.Recommended);
        Assert.Equal(supplierOrgId, result.Recommended!.OrgId);
        Assert.False(result.UsedExternalFallback);
    }

    [Fact]
    public async Task MatchAsync_FlagOff_RanksWithoutAnyAiCall()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, supplierOrgId) = await SeedAsync(db);
        var aiProvider = new Mock<IAiProvider>();
        var discovery = new Mock<IAiSupplierDiscoveryService>();
        var service = CreateService(db, discovery.Object, aiProvider.Object, aiEnabled: false);

        var withSupplier = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Emergency);
        var withoutSupplier = await service.MatchAsync(orgId, propertyId, "laundry", ServiceRequestUrgency.Normal);

        Assert.Equal(supplierOrgId, withSupplier.Recommended!.OrgId);
        Assert.Contains("Clean Co Srl", withSupplier.Recommended.MatchReason);
        Assert.Null(withoutSupplier.Recommended);
        Assert.Empty(withoutSupplier.ExternalSuggestions);
        Assert.False(withoutSupplier.UsedExternalFallback);
        aiProvider.Verify(
            p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        discovery.Verify(d => d.SearchNearbyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MatchAsync_FlagOn_PromptHasOnlyCategoryUrgencyAndLoad()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, _) = await SeedAsync(db);
        string? sentPrompt = null;
        var aiProvider = new Mock<IAiProvider>();
        aiProvider
            .Setup(p => p.GenerateAsync(It.IsAny<string>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, AiModelTier, string, CancellationToken>((prompt, _, _, _) => sentPrompt = prompt)
            .ReturnsAsync(new AiGenerationResult("Scelta adatta.", 30, 5, AiModelTier.Economy, false));
        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), aiProvider.Object, aiEnabled: true);

        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.High);

        Assert.Equal("Scelta adatta.", result.Recommended!.MatchReason);
        Assert.Equal(SupplierMatchService.BuildMatchReasonPrompt("cleaning", ServiceRequestUrgency.High, 0), sentPrompt);
        Assert.DoesNotContain("Clean Co", sentPrompt);
        Assert.DoesNotContain("Flat", sentPrompt);
        Assert.DoesNotContain("Roma", sentPrompt);
    }

    [Fact]
    public async Task MatchAsync_FlagOnBudgetExhausted_FallsBackToStaticReason()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, _) = await SeedAsync(db);
        var aiProvider = new Mock<IAiProvider>();
        aiProvider
            .Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());
        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), aiProvider.Object, aiEnabled: true);

        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.Contains("Clean Co Srl", result.Recommended!.MatchReason);
    }

    private static async Task<(Guid OrgId, Guid PropertyId, Guid SupplierOrgId)> SeedAsync(AppDbContext db)
    {
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var supplierOrgId = Guid.NewGuid();

        db.Orgs.Add(new Casazen.Core.Entities.Org { Id = orgId, Name = "Host", Slug = "host", DisplayName = "Host", ContactEmail = "h@x.it" });
        db.Orgs.Add(new Casazen.Core.Entities.Org { Id = supplierOrgId, Name = "Clean Co", Slug = "clean", DisplayName = "Clean Co", ContactEmail = "c@x.it", OrgType = OrgType.Supplier });
        db.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            Name = "Flat",
            City = "Roma",
            PostalCode = "00100",
            OwnerId = "owner-1",
        });
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = supplierOrgId,
            Status = SupplierStatus.Active,
            LegalName = "Clean Co Srl",
            Phone = "+39061234567",
            Email = "clean@x.it",
            CategoriesJson = """["cleaning"]""",
            ComuniJson = """["Roma"]""",
        });
        await db.SaveChangesAsync();
        return (orgId, propertyId, supplierOrgId);
    }

    private static SupplierMatchService CreateService(
        AppDbContext db,
        IAiSupplierDiscoveryService discovery,
        IAiProvider aiProvider,
        bool aiEnabled)
    {
        var supplierService = new SupplierService(
            db,
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            Mock.Of<ISafeExternalHttpClient>(),
            Options.Create(new SupplierRegistrationOptions()),
            Mock.Of<ILogger<SupplierService>>());
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.AiSupplierDiscovery)).Returns(aiEnabled);

        return new SupplierMatchService(
            db,
            supplierService,
            discovery,
            aiProvider,
            flags.Object,
            Mock.Of<ILogger<SupplierMatchService>>());
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
