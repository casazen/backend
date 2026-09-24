using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ServiceRequestServiceTests
{
    [Fact]
    public async Task CreateAsync_WithInvalidBookingId_Throws()
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

        var paid = await service.MarkPaidAsync(created.Id, hostOrgId);

        Assert.Equal(ServiceRequestStatus.Pagato, paid.Status);
        Assert.NotNull(paid.PaidAt);
    }

    [Fact]
    public async Task MarkPaidAsync_UnknownRequest_ThrowsNotFoundExceptionWithCode()
    {
        await using var db = CreateDb();
        var (hostOrgId, _, _) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.MarkPaidAsync(Guid.NewGuid(), hostOrgId));

        Assert.Equal("service_request_not_found", ex.Code);
        Assert.Equal("ServiceRequestNotFound", ex.MessageKey);
    }

    [Fact]
    public async Task MarkPaidAsync_RequestOfAnotherOrg_ThrowsNotFoundAndLeavesItUnpaid()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
        await service.CompleteAsync(created.Id, supplierOrgId, null);

        await Assert.ThrowsAsync<NotFoundException>(() => service.MarkPaidAsync(created.Id, Guid.NewGuid()));

        Assert.Equal(ServiceRequestStatus.Completato, (await db.ServiceRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task ListForHostAsync_OwnerScope_ReturnsOnlyRequestsOnOwnedProperties()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var otherProperty = new Property
        {
            OwnerId = "auth0|colleague",
            OrgId = hostOrgId,
            Name = "Colleague Property",
            Address = "Via Test 2",
            City = "H501",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 80m,
            CinCode = "IT058091C27G5FFZDZ",
        };
        db.Properties.Add(otherProperty);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var own = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        var colleagues = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, "auth0|colleague", otherProperty.Id, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        var (ownerItems, ownerTotal) = await service.ListForHostAsync(
            new HostScope(hostOrgId, TestAuthHandler.DefaultUserId), null, null, null, 1, 20);
        var (orgItems, orgTotal) = await service.ListForHostAsync(
            new HostScope(hostOrgId, null), null, null, null, 1, 20);
        var (otherOrgItems, _) = await service.ListForHostAsync(
            new HostScope(Guid.NewGuid(), null), null, null, null, 1, 20);

        Assert.Equal(own.Id, Assert.Single(ownerItems).Id);
        Assert.Equal(1, ownerTotal);
        Assert.Equal(2, orgTotal);
        Assert.Contains(orgItems, r => r.Id == colleagues.Id);
        Assert.Empty(otherOrgItems);
        Assert.Null(await service.GetByIdForHostAsync(colleagues.Id, new HostScope(hostOrgId, TestAuthHandler.DefaultUserId)));
        Assert.NotNull(await service.GetByIdForHostAsync(colleagues.Id, new HostScope(hostOrgId, null)));
    }

    [Fact]
    public async Task ListForHostAsync_WhenBookingIdProvided_ReturnsOnlyMatchingRequests()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var matched = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, "matched", false));
        await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, "other", false));

        var bookingId = Guid.NewGuid();
        matched.BookingId = bookingId;
        await db.SaveChangesAsync();

        var (items, total) = await service.ListForHostAsync(
            new HostScope(hostOrgId, TestAuthHandler.DefaultUserId),
            status: null,
            propertyId: null,
            bookingId,
            page: 1,
            pageSize: 20);

        Assert.Equal(1, total);
        Assert.Equal(matched.Id, Assert.Single(items).Id);
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

    // ─── FD-13: notifications rendered from templates, links from config, queued outside the request ───

    [Fact]
    public async Task CreateAsync_ValidRequest_QueuesSupplierEmailWithInboxLinkFromPublicSiteBaseUrl()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, "Turnover", false));

        var (to, content, template) = Assert.Single(queue.Queued);
        Assert.Equal("supplier@test.com", to);
        Assert.Equal(EmailTemplates.Names.ServiceRequestCreated, template);
        Assert.Equal("Nuova richiesta di servizio — Test Property", content.Subject);
        Assert.Contains($"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/app/supplier/inbox\"", content.HtmlBody);
        Assert.Contains("Turnover", content.HtmlBody);
        Assert.DoesNotContain("casazen.it", content.HtmlBody);
    }

    [Fact]
    public async Task CreateAsync_NotesWithMarkup_AreHtmlEncodedInSupplierEmail()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal,
            "<a href=\"https://phish.example\">Conferma IBAN</a><script>alert(1)</script>", false));

        var html = Assert.Single(queue.Queued).Content.HtmlBody;
        Assert.DoesNotContain("<a href=\"https://phish.example\"", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;a href=&quot;https://phish.example&quot;&gt;Conferma IBAN&lt;/a&gt;", html);
        Assert.Contains("richiesta di <strong>Pulizie</strong>", html);
    }

    [Theory]
    [InlineData("Pulizie")]
    [InlineData("cleaning<script>alert(1)</script>")]
    [InlineData("")]
    public async Task CreateAsync_CategoryNotACode_ThrowsInvalidServiceCategoryWithoutCreatingOrEmailing(string category)
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            category, ServiceRequestUrgency.Normal, null, false)));

        Assert.Equal(ServiceCategories.InvalidCategoryCode, ex.Code);
        Assert.Empty(db.ServiceRequests);
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public async Task CreateAsync_CodeWithDifferentCase_StoresNormalizedCode()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var result = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            " Linen ", ServiceRequestUrgency.Normal, null, false));

        Assert.Equal(ServiceCategories.Linen, result.Category);
    }

    [Fact]
    public async Task CreateAsync_PublicSiteBaseUrlMissing_ThrowsConfigurationErrorWithoutCreatingRequest()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue, publicSiteBaseUrl: null);

        await Assert.ThrowsAsync<EmailConfigurationException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false)));

        Assert.Empty(db.ServiceRequests);
        Assert.Empty(queue.Queued);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico, "Richiesta fornitore presa in carico — Test Property")]
    [InlineData(ServiceRequestStatus.Completato, "Richiesta fornitore completata — Test Property")]
    [InlineData(ServiceRequestStatus.Rifiutato, "Richiesta fornitore rifiutata — Test Property")]
    public async Task SupplierStatusChange_ValidTransition_QueuesHostEmail(ServiceRequestStatus target, string expectedSubject)
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        queue.Queued.Clear();

        switch (target)
        {
            case ServiceRequestStatus.PresoInCarico:
                await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
                break;
            case ServiceRequestStatus.Completato:
                await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
                queue.Queued.Clear();
                await service.CompleteAsync(created.Id, supplierOrgId, null);
                break;
            default:
                await service.RejectAsync(created.Id, supplierOrgId, "<b>Non disponibile</b>");
                break;
        }

        var (to, content, template) = Assert.Single(queue.Queued);
        Assert.Equal("host@test.com", to);
        Assert.Equal(EmailTemplates.Names.ServiceRequestStatusChanged, template);
        Assert.Equal(expectedSubject, content.Subject);
        if (target == ServiceRequestStatus.Rifiutato)
            Assert.Contains("&lt;b&gt;Non disponibile&lt;/b&gt;", content.HtmlBody);
    }

    [Fact]
    public async Task TakeAsync_PushAndEmailQueueThrow_ReturnsTakenRequestWithStatusSaved()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var created = await CreateService(db).CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        var queue = new Mock<IEmailQueue>();
        queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("storage down"));
        var push = new Mock<IPushNotificationService>();
        push.Setup(p => p.SendServiceRequestUpdateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("push provider timeout"));
        var service = CreateService(db, queue.Object, push: push.Object);

        var taken = await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        var saved = await db.ServiceRequests.AsNoTracking().SingleAsync(r => r.Id == created.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, saved.Status);
        push.Verify(p => p.SendServiceRequestUpdateAsync(created.Id, "presa in carico", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ServiceRequestService CreateService(
        AppDbContext db,
        IEmailQueue? queue = null,
        string? publicSiteBaseUrl = EmailTestHelpers.PublicSiteBaseUrl,
        IPushNotificationService? push = null)
    {
        var repo = new ServiceRequestRepository(db);

        return new ServiceRequestService(
            db,
            repo,
            queue ?? new RecordingEmailQueue(),
            EmailTestHelpers.Links(publicSiteBaseUrl),
            push ?? Mock.Of<IPushNotificationService>(),
            NullLogger<ServiceRequestService>.Instance);
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
