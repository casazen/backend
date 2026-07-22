using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PushNotificationServiceTests
{
    [Fact]
    public async Task SendServiceRequestUpdateAsync_WhenCalledUnderSupplierTenant_LoadsHostProperty()
    {
        var hostOrgId = Guid.NewGuid();
        var supplierOrgId = Guid.NewGuid();
        await using var db = CreateDb(new AuthenticatedTenantContext(supplierOrgId));

        var property = new Property
        {
            OrgId = hostOrgId,
            OwnerId = "auth0|host",
            Name = "Host Property",
            Address = "Via Test 1",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            CinCode = "IT-ABC123-DEF456",
        };

        db.Orgs.AddRange(
            new OrgEntity
            {
                Id = hostOrgId,
                Name = "Host Org",
                Slug = $"host-{Guid.NewGuid():N}"[..20],
                DisplayName = "Host Org",
                ContactEmail = "host@example.com",
                PlanTier = PlanTier.Starter,
            },
            new OrgEntity
            {
                Id = supplierOrgId,
                Name = "Supplier Org",
                Slug = $"supplier-{Guid.NewGuid():N}"[..20],
                DisplayName = "Supplier Org",
                ContactEmail = "supplier@example.com",
                OrgType = OrgType.Supplier,
                PlanTier = PlanTier.Starter,
            });
        db.Properties.Add(property);
        db.Users.Add(new User
        {
            Id = "auth0|host",
            Email = "host@example.com",
            OrgId = hostOrgId,
            IsActive = true,
        });

        var request = new ServiceRequest
        {
            OrgId = hostOrgId,
            Property = property,
            SupplierOrgId = supplierOrgId,
            Category = "cleaning",
            Status = ServiceRequestStatus.PresoInCarico,
        };
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var service = new PushNotificationService(
            db,
            httpClientFactory.Object,
            NullLogger<PushNotificationService>.Instance);

        await service.SendServiceRequestUpdateAsync(request.Id, "presa in carico");
    }

    private static AppDbContext CreateDb(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options, tenantContext);
    }

    private sealed class AuthenticatedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId => orgId;
        public bool FilterEnabled => true;
    }
}
