using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
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
        discovery.Verify(d => d.SearchNearbyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

    // SE-05 (A8-27): the client shows the AI notice only next to a reason an AI model really wrote.
    [Fact]
    public async Task MatchAsync_FlagOnAndProviderAnswers_ReasonIsMarkedAsAiGenerated()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, _) = await SeedAsync(db);
        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), ProviderAnswering("Scelta adatta.", providerConfigured: true).Object, aiEnabled: true);

        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.True(result.Recommended!.ReasonGeneratedByAi);
        Assert.Equal("Scelta adatta.", result.Recommended.MatchReason);
        Assert.All(result.Alternatives, a => Assert.False(a.ReasonGeneratedByAi));
    }

    [Fact]
    public async Task MatchAsync_FlagOff_ReasonIsStaticAndNotMarkedAsAiGenerated()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, _) = await SeedAsync(db);
        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), Mock.Of<IAiProvider>(), aiEnabled: false);

        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.False(result.Recommended!.ReasonGeneratedByAi);
    }

    [Fact]
    public async Task MatchAsync_FlagOnButProviderNotConfigured_UsesStaticReasonNotTheStubPlaceholder()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, _) = await SeedAsync(db);
        var service = CreateService(
            db, Mock.Of<IAiSupplierDiscoveryService>(), ProviderAnswering("<article>placeholder</article>", providerConfigured: false).Object, aiEnabled: true);

        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.False(result.Recommended!.ReasonGeneratedByAi);
        Assert.Contains("Clean Co Srl", result.Recommended.MatchReason);
        Assert.DoesNotContain("placeholder", result.Recommended.MatchReason);
    }

    [Fact]
    public async Task MatchAsync_FlagOnProviderFails_ReasonIsStaticAndNotMarkedAsAiGenerated()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, _) = await SeedAsync(db);
        var aiProvider = new Mock<IAiProvider>();
        aiProvider
            .Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());
        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), aiProvider.Object, aiEnabled: true);

        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.False(result.Recommended!.ReasonGeneratedByAi);
    }

    // A8-25: the answer is cached per org, so a second match of the same org pays nothing and another org never reads it.
    [Fact]
    public async Task MatchAsync_SameOrgTwice_ReusesTheCachedReasonAndAnotherOrgAsksTheProvider()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, _) = await SeedAsync(db);
        var otherOrgId = Guid.NewGuid();
        db.Orgs.Add(new Casazen.Core.Entities.Org { Id = otherOrgId, Name = "Altro", Slug = "altro", DisplayName = "Altro", ContactEmail = "a@x.it" });
        db.Properties.Add(new Property { Id = Guid.NewGuid(), OrgId = otherOrgId, Name = "Altra", City = "Roma", PostalCode = "00100", OwnerId = "owner-2" });
        await db.SaveChangesAsync();
        var otherPropertyId = await db.Properties.IgnoreQueryFilters().Where(p => p.OrgId == otherOrgId).Select(p => p.Id).SingleAsync();
        var aiProvider = ProviderAnswering("Scelta adatta.", providerConfigured: true);
        var cache = new AiResponseCache(Options.Create(new AiCacheOptions()));
        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), aiProvider.Object, aiEnabled: true, cache);

        await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);
        await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);
        aiProvider.Verify(
            p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var other = await service.MatchAsync(otherOrgId, otherPropertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.True(other.Recommended!.ReasonGeneratedByAi);
        aiProvider.Verify(
            p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    private static Mock<IAiProvider> ProviderAnswering(string content, bool providerConfigured)
    {
        var aiProvider = new Mock<IAiProvider>();
        aiProvider
            .Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(content, 30, 5, AiModelTier.Economy, false, providerConfigured));
        return aiProvider;
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
        bool aiEnabled,
        IAiResponseCache? cache = null)
    {
        var supplierService = new SupplierService(
            db,
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            Mock.Of<ISafeExternalHttpClient>(),
            ComuneTestServices.Pilots(db),
            ComuneTestServices.Directory(db),
            ComuneTestServices.Matcher(db),
            LegalTestServices.Legal(),
            Mock.Of<ILogger<SupplierService>>());
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.AiSupplierDiscovery)).Returns(aiEnabled);

        return new SupplierMatchService(
            db,
            supplierService,
            discovery,
            aiProvider,
            flags.Object,
            Mock.Of<ILogger<SupplierMatchService>>(),
            cache);
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
