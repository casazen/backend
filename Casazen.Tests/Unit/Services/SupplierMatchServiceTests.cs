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
using Microsoft.Extensions.Logging.Abstractions;
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

    // ─── SP-REL: the reliability of the suppliers is part of the score ───

    [Fact]
    public async Task MatchAsync_WithNoHistory_KeepsTheScoreOfBefore_AndTheSuppliersAreOrderedByName()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, first) = await SeedAsync(db);
        var second = await AddSupplierAsync(db, "Beta Srl", bio: "Pulizie dal 1999");
        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), Mock.Of<IAiProvider>(), aiEnabled: false, reliability: Reliability(db));

        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        // A supplier with a bio scores 5 more, as it always did.
        Assert.Equal(second, result.Recommended!.OrgId);
        Assert.Equal(75, result.Recommended.MatchScore);
        var alternative = Assert.Single(result.Alternatives);
        Assert.Equal(first, alternative.OrgId);
        Assert.Equal(70, alternative.MatchScore);
    }

    [Fact]
    public async Task MatchAsync_ASupplierThatAnswersInTimeAndDoesItsJobs_OutranksAnEqualOne()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, clean) = await SeedAsync(db);
        var reliable = await AddSupplierAsync(db, "Zeta Srl");
        var reliability = Reliability(db);
        // "Zeta" comes after "Clean Co" by name, so only its history can put it first.
        for (var i = 0; i < 3; i++)
        {
            await reliability.RecordAsync(reliable, SupplierReliabilityEventKind.RepliedInTime, Guid.NewGuid(), DateTime.UtcNow);
            await reliability.RecordAsync(reliable, SupplierReliabilityEventKind.JobCompletedClean, Guid.NewGuid(), DateTime.UtcNow);
        }

        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), Mock.Of<IAiProvider>(), aiEnabled: false, reliability: reliability);
        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.Equal(reliable, result.Recommended!.OrgId);
        Assert.Equal(70 + 3 + 6, result.Recommended.MatchScore);
        Assert.Equal(clean, Assert.Single(result.Alternatives).OrgId);
    }

    [Fact]
    public async Task MatchAsync_ASupplierThatMissedDeadlinesOrWasDisputed_FallsBelowAnEqualOne()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, missed) = await SeedAsync(db);
        var other = await AddSupplierAsync(db, "Zeta Srl");
        var reliability = Reliability(db);
        // "Clean Co" would come first by name: two missed deadlines and a dispute put it behind.
        await reliability.RecordAsync(missed, SupplierReliabilityEventKind.NoReplyInTime, Guid.NewGuid(), DateTime.UtcNow);
        await reliability.RecordAsync(missed, SupplierReliabilityEventKind.NoReplyInTime, Guid.NewGuid(), DateTime.UtcNow);
        await reliability.RecordAsync(missed, SupplierReliabilityEventKind.DisputeOpened, Guid.NewGuid(), DateTime.UtcNow);

        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), Mock.Of<IAiProvider>(), aiEnabled: false, reliability: reliability);
        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.Equal(other, result.Recommended!.OrgId);
        var alternative = Assert.Single(result.Alternatives);
        Assert.Equal(missed, alternative.OrgId);
        Assert.Equal(70 - 20 - 12, alternative.MatchScore);
    }

    [Fact]
    public async Task MatchAsync_WithTheReliabilityOff_IgnoresTheHistory()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, first) = await SeedAsync(db);
        await AddSupplierAsync(db, "Zeta Srl");
        var off = new SupplierRankingOptions { ReliabilityEnabled = false };
        var reliability = Reliability(db, off);
        // The event is still written (the weights can be turned on later), it just does not move anybody.
        await reliability.RecordAsync(first, SupplierReliabilityEventKind.NoReplyInTime, Guid.NewGuid(), DateTime.UtcNow);
        var service = CreateService(
            db, Mock.Of<IAiSupplierDiscoveryService>(), Mock.Of<IAiProvider>(), aiEnabled: false, reliability: reliability, ranking: off);

        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.Equal(first, result.Recommended!.OrgId);
        Assert.Equal(70, result.Recommended.MatchScore);
    }

    [Fact]
    public async Task ScoresAsync_IsTheScoreTheMatchRanksBy_ForEverySupplierOfTheList()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, first) = await SeedAsync(db);
        var second = await AddSupplierAsync(db, "Beta Srl", bio: "Pulizie dal 1999");
        var reliability = Reliability(db);
        await reliability.RecordAsync(first, SupplierReliabilityEventKind.JobCompletedClean, Guid.NewGuid(), DateTime.UtcNow);
        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), Mock.Of<IAiProvider>(), aiEnabled: false, reliability: reliability);

        var match = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.High);
        var profiles = await db.SupplierProfiles.AsNoTracking().ToListAsync();
        var scores = await service.ScoresAsync(profiles, ServiceRequestUrgency.High);

        Assert.Equal(2, scores.Count);
        Assert.Equal(scores[match.Recommended!.OrgId], match.Recommended.MatchScore);
        Assert.Equal(scores[match.Alternatives[0].OrgId], match.Alternatives[0].MatchScore);
        Assert.Equal(70 + 8 + 2, scores[first]);
        Assert.Equal(70 + 8 + 5, scores[second]);
        Assert.Empty(await service.ScoresAsync([], ServiceRequestUrgency.Normal));
    }

    [Fact]
    public async Task MatchAsync_TheOpenWorkOfASupplierStillCounts_NextToItsReliability()
    {
        await using var db = CreateDb();
        var (orgId, propertyId, busy) = await SeedAsync(db);
        var free = await AddSupplierAsync(db, "Zeta Srl");
        db.ServiceRequests.Add(new ServiceRequest
        {
            OrgId = orgId,
            PropertyId = propertyId,
            SupplierOrgId = busy,
            Category = "cleaning",
            Status = ServiceRequestStatus.PresoInCarico,
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, Mock.Of<IAiSupplierDiscoveryService>(), Mock.Of<IAiProvider>(), aiEnabled: false, reliability: Reliability(db));

        var result = await service.MatchAsync(orgId, propertyId, "cleaning", ServiceRequestUrgency.Normal);

        Assert.Equal(free, result.Recommended!.OrgId);
        Assert.Equal(70 - 8, Assert.Single(result.Alternatives).MatchScore);
    }

    // The match only reads the points (and the tests write events): the agenda is used to excuse a missed deadline, which they do not record.
    private static SupplierReliabilityService Reliability(AppDbContext db, SupplierRankingOptions? ranking = null) =>
        new(db, Mock.Of<ISupplierAgendaService>(), Options.Create(ranking ?? new SupplierRankingOptions()), NullLogger<SupplierReliabilityService>.Instance);

    /// <summary>Another active supplier of Roma and cleaning, to rank against the one <see cref="SeedAsync"/> adds.</summary>
    private static async Task<Guid> AddSupplierAsync(AppDbContext db, string legalName, string? bio = null)
    {
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new Casazen.Core.Entities.Org
        {
            Id = orgId,
            Name = legalName,
            Slug = $"s-{orgId:N}"[..16],
            DisplayName = legalName,
            ContactEmail = $"{orgId:N}@x.it",
            OrgType = OrgType.Supplier,
        });
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = orgId,
            Status = SupplierStatus.Active,
            LegalName = legalName,
            Phone = "+39061234568",
            Email = $"{orgId:N}@x.it",
            Bio = bio,
            CategoriesJson = """["cleaning"]""",
            ComuniJson = """["Roma"]""",
        });
        await db.SaveChangesAsync();
        return orgId;
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
        IAiResponseCache? cache = null,
        ISupplierReliabilityService? reliability = null,
        SupplierRankingOptions? ranking = null)
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
            cache,
            reliability,
            ranking is null ? null : Options.Create(ranking));
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
