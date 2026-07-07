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

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
