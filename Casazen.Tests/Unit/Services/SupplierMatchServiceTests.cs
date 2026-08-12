using System.Collections.Concurrent;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SupplierMatchServiceTests
{
    [Fact]
    public async Task MatchAsync_WithActiveSupplier_ReturnsRecommended()
    {
        await using var db = CreateDb();
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

        var supplierService = new SupplierService(
            db,
            Mock.Of<IEmailService>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IHostEnvironment>(),
            Mock.Of<ILogger<SupplierService>>());

        var auth = new Mock<IPropertyAuthorizationService>();
        auth.Setup(a => a.CanAccessPropertyAsync("user-1", propertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);

        var service = new SupplierMatchService(
            db,
            supplierService,
            Mock.Of<IAiSupplierDiscoveryService>(),
            Mock.Of<IAiProvider>(),
            auth.Object,
            Mock.Of<ILogger<SupplierMatchService>>());

        var result = await service.MatchAsync(orgId, "user-1", propertyId, "cleaning", ServiceRequestUrgency.Normal, null);

        Assert.NotNull(result.Recommended);
        Assert.Equal(supplierOrgId, result.Recommended!.OrgId);
        Assert.False(result.UsedExternalFallback);
    }

    [Fact]
    public async Task MatchAsync_DoesNotReuseAiReasonAcrossDifferentHostNotes()
    {
        await using var db = CreateDb();
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

        var supplierService = new SupplierService(
            db,
            Mock.Of<IEmailService>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IHostEnvironment>(),
            Mock.Of<ILogger<SupplierService>>());

        var auth = new Mock<IPropertyAuthorizationService>();
        auth.Setup(a => a.CanAccessPropertyAsync("user-1", propertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);

        var aiProvider = new CachingAiProvider();
        var service = new SupplierMatchService(
            db,
            supplierService,
            Mock.Of<IAiSupplierDiscoveryService>(),
            aiProvider,
            auth.Object,
            Mock.Of<ILogger<SupplierMatchService>>());

        var first = await service.MatchAsync(
            orgId,
            "user-1",
            propertyId,
            "cleaning",
            ServiceRequestUrgency.Normal,
            "Use entry code alpha");
        var second = await service.MatchAsync(
            orgId,
            "user-1",
            propertyId,
            "cleaning",
            ServiceRequestUrgency.Normal,
            "Use entry code beta");

        Assert.Contains("alpha", first.Recommended!.MatchReason);
        Assert.Contains("beta", second.Recommended!.MatchReason);
        Assert.Equal(2, aiProvider.CacheKeys.Count);
        Assert.All(aiProvider.CacheKeys, key =>
        {
            Assert.DoesNotContain("alpha", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("beta", key, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private sealed class CachingAiProvider : IAiProvider
    {
        private readonly ConcurrentDictionary<string, AiGenerationResult> _cache = new();

        public IReadOnlyCollection<string> CacheKeys => _cache.Keys.ToArray();

        public Task<AiGenerationResult> GenerateAsync(
            string prompt,
            AiModelTier tier,
            string cacheKey,
            CancellationToken cancellationToken = default)
        {
            var result = _cache.GetOrAdd(cacheKey, _ =>
            {
                var reason = prompt.Contains("alpha", StringComparison.OrdinalIgnoreCase)
                    ? "Reason includes alpha"
                    : "Reason includes beta";
                return new AiGenerationResult(reason, 0, 0, tier, false);
            });

            return Task.FromResult(result);
        }
    }
}
