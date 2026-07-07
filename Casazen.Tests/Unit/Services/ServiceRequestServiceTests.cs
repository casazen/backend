using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ServiceRequestServiceTests
{
    [Fact]
    public async Task CreateAsync_WithBookingId_Throws()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, Guid.NewGuid(), supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false)));
    }

    [Fact]
    public async Task CreateAsync_ChargeToGuest_Throws()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, true)));
    }

    [Fact]
    public async Task CreateAsync_ValidRequest_CreatesRichiesto()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var result = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, "Turnover", false));

        Assert.Equal(ServiceRequestStatus.Richiesto, result.Status);
        Assert.Equal("cleaning", result.Category);
    }

    [Fact]
    public async Task CreateAsync_InactiveSupplier_Throws()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Pending);
        var service = CreateService(db);

        await Assert.ThrowsAsync<ServiceRequestStateException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false)));
    }

    [Fact]
    public async Task CreateAsync_SupplierOutsideComune_Throws()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active, supplierComune: "F205");
        var service = CreateService(db);

        await Assert.ThrowsAsync<ServiceRequestStateException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false)));
    }

    [Fact]
    public async Task TakeAsync_ValidTransition_SetsPresoInCarico()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        var taken = await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        Assert.NotNull(taken.TakenAt);
    }

    [Fact]
    public async Task TakeAsync_WrongSupplier_Throws()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.TakeAsync(created.Id, Guid.NewGuid(), "other"));
    }

    [Fact]
    public async Task TakeAsync_InvalidState_Throws()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");

        await Assert.ThrowsAsync<ServiceRequestStateException>(() =>
            service.TakeAsync(created.Id, supplierOrgId, "supplier-user"));
    }

    [Fact]
    public async Task CompleteAsync_FromPresoInCarico_SetsCompletato()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");

        var completed = await service.CompleteAsync(created.Id, supplierOrgId, "Done");

        Assert.Equal(ServiceRequestStatus.Completato, completed.Status);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public async Task MarkPaidAsync_FromCompletato_SetsPagato()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
        await service.CompleteAsync(created.Id, supplierOrgId, null);

        var paid = await service.MarkPaidAsync(created.Id, hostOrgId, TestAuthHandler.DefaultUserId);

        Assert.Equal(ServiceRequestStatus.Pagato, paid.Status);
        Assert.NotNull(paid.PaidAt);
    }

    [Fact]
    public async Task RejectAsync_FromRichiesto_SetsRifiutato()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        var rejected = await service.RejectAsync(created.Id, supplierOrgId, "Non disponibile");

        Assert.Equal(ServiceRequestStatus.Rifiutato, rejected.Status);
        Assert.Equal("Non disponibile", rejected.RejectionReason);
    }

    private static ServiceRequestService CreateService(AppDbContext db)
    {
        var repo = new ServiceRequestRepository(db);
        var propertyAuth = new PropertyAuthorizationService(new PropertyRepository(db));
        var email = new Mock<IEmailService>();
        email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));

        var config = new ConfigurationBuilder().Build();
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Testing");

        return new ServiceRequestService(
            db, repo, propertyAuth, email.Object, config, env.Object, NullLogger<ServiceRequestService>.Instance);
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(Guid HostOrgId, Guid PropertyId, Guid SupplierOrgId)> SeedHostAndSupplierAsync(
        AppDbContext db,
        string propertyCity,
        SupplierStatus supplierStatus,
        string supplierComune = "H501")
    {
        var hostOrg = new Casazen.Core.Entities.Org
        {
            Name = "Host Org",
            Slug = $"host-{Guid.NewGuid():N}"[..20],
            DisplayName = "Host Org",
            ContactEmail = "host@test.com",
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(hostOrg);

        var property = new Property
        {
            OwnerId = TestAuthHandler.DefaultUserId,
            OrgId = hostOrg.Id,
            Name = "Test Property",
            Address = "Via Test 1",
            City = propertyCity,
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT-ABC123-DEF456",
        };
        db.Properties.Add(property);

        var supplierOrg = new Casazen.Core.Entities.Org
        {
            Name = "Supplier Org",
            Slug = $"sup-{Guid.NewGuid():N}"[..20],
            DisplayName = "Supplier Org",
            ContactEmail = "supplier@test.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(supplierOrg);

        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = supplierOrg.Id,
            Email = "supplier@test.com",
            LegalName = "Supplier Srl",
            Phone = "+39 06 123456",
            Status = supplierStatus,
            ComuniJson = $"[\"{supplierComune}\"]",
            CategoriesJson = "[\"cleaning\"]",
        });

        await db.SaveChangesAsync();
        return (hostOrg.Id, property.Id, supplierOrg.Id);
    }
}
