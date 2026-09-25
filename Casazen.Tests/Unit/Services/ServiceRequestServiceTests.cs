using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
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
    // ─── D2 (SU-07): short-rent requests are for a stay, long-rent requests for the property ───

    [Fact]
    public async Task CreateAsync_ShortRentWithoutBooking_ThrowsBookingRequiredWithoutCreatingOrEmailing()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, _) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false, ServiceRequestRentalContext.ShortRent)));

        Assert.Equal(ServiceRequestErrorCodes.BookingRequired, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.BookingRequiredMessageKey, ex.MessageKey);
        Assert.Empty(db.ServiceRequests);
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public async Task CreateAsync_ShortRentWithUnknownBooking_ThrowsBookingMismatch()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, _) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, Guid.NewGuid(), supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false)));

        Assert.Equal(ServiceRequestErrorCodes.BookingMismatch, ex.Code);
        Assert.Empty(db.ServiceRequests);
    }

    [Fact]
    public async Task CreateAsync_ShortRentWithBookingOfAnotherProperty_ThrowsBookingMismatch()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, _) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var otherProperty = NewProperty(hostOrgId, "Other Property");
        db.Properties.Add(otherProperty);
        var otherStay = NewBooking(hostOrgId, otherProperty.Id);
        db.Bookings.Add(otherStay);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, otherStay.Id, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false)));

        Assert.Equal(ServiceRequestErrorCodes.BookingMismatch, ex.Code);
        Assert.Empty(db.ServiceRequests);
    }

    [Fact]
    public async Task CreateAsync_ShortRentWithBookingOfAnotherOrgOnSameProperty_ThrowsBookingMismatch()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, _) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        // A row that claims the property but belongs to another org is never accepted as a stay of this host.
        var foreignStay = NewBooking(Guid.NewGuid(), propertyId);
        db.Bookings.Add(foreignStay);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, foreignStay.Id, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false)));

        Assert.Equal(ServiceRequestErrorCodes.BookingMismatch, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_ShortRentWithStayOfTheProperty_StoresBookingAndShortRent()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        var saved = await db.ServiceRequests.AsNoTracking().SingleAsync(r => r.Id == created.Id);
        Assert.Equal(bookingId, saved.BookingId);
        Assert.Equal(ServiceRequestRentalContext.ShortRent, saved.RentalContext);
    }

    [Fact]
    public async Task CreateAsync_LongRentForProperty_StoresLongRentWithoutBooking()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, _) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "plumbing", ServiceRequestUrgency.High, "Perdita in cucina", false, ServiceRequestRentalContext.LongRent));

        var saved = await db.ServiceRequests.AsNoTracking().SingleAsync(r => r.Id == created.Id);
        Assert.Null(saved.BookingId);
        Assert.Equal(ServiceRequestRentalContext.LongRent, saved.RentalContext);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Single(queue.Queued);
    }

    [Fact]
    public async Task CreateAsync_LongRentWithBooking_ThrowsBookingNotAllowed()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false, ServiceRequestRentalContext.LongRent)));

        Assert.Equal(ServiceRequestErrorCodes.BookingNotAllowed, ex.Code);
        Assert.Empty(db.ServiceRequests);
    }

    [Fact]
    public async Task ListAndGetForHost_EachRentalContext_ReachesOnlyItsOwnRequests()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var stay = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        var lease = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "plumbing", ServiceRequestUrgency.Normal, null, false, ServiceRequestRentalContext.LongRent));
        var scope = new HostScope(hostOrgId, null);

        var (shortItems, _) = await service.ListForHostAsync(scope, ServiceRequestRentalContext.ShortRent, null, propertyId, null, 1, 20);
        var (longItems, _) = await service.ListForHostAsync(scope, ServiceRequestRentalContext.LongRent, null, propertyId, null, 1, 20);

        Assert.Equal(stay.Id, Assert.Single(shortItems).Id);
        Assert.Equal(lease.Id, Assert.Single(longItems).Id);
        Assert.Null(await service.GetByIdForHostAsync(lease.Id, scope, ServiceRequestRentalContext.ShortRent));
        Assert.Null(await service.GetByIdForHostAsync(stay.Id, scope, ServiceRequestRentalContext.LongRent));
        Assert.NotNull(await service.GetByIdForHostAsync(lease.Id, scope, ServiceRequestRentalContext.LongRent));
    }

    [Fact]
    public async Task CreateAsync_ValidRequest_CreatesRichiesto()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var result = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, "Turnover", false));

        Assert.Equal(ServiceRequestStatus.Richiesto, result.Status);
        Assert.Equal("cleaning", result.Category);
    }

    [Fact]
    public async Task CreateAsync_InactiveSupplier_ThrowsSupplierInactive()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Pending);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false)));

        Assert.Equal(ServiceRequestErrorCodes.SupplierInactive, ex.Code);
        Assert.Empty(db.ServiceRequests);
    }

    [Fact]
    public async Task CreateAsync_SupplierOutsideComune_ThrowsSupplierOutsideComune()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active, supplierComune: "F205");
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false)));

        Assert.Equal(ServiceRequestErrorCodes.SupplierOutsideComune, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_UnknownSupplier_ThrowsSupplierNotFound()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, _, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, Guid.NewGuid(),
                "cleaning", ServiceRequestUrgency.Normal, null, false)));

        Assert.Equal(ServiceRequestErrorCodes.SupplierNotFound, ex.Code);
        Assert.Empty(db.ServiceRequests);
    }

    [Fact]
    public async Task CreateAsync_PropertyOfAnotherOrg_ThrowsPropertyNotFound()
    {
        await using var db = CreateDb();
        var (_, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                Guid.NewGuid(), TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, false)));

        Assert.Equal(ServiceRequestErrorCodes.PropertyNotFound, ex.Code);
        Assert.Empty(db.ServiceRequests);
    }

    [Fact]
    public async Task CreateAsync_ChargeToGuest_ThrowsChargeToGuestNotAllowed()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
                "cleaning", ServiceRequestUrgency.Normal, null, ChargeToGuest: true)));

        Assert.Equal(ServiceRequestErrorCodes.ChargeToGuestNotAllowed, ex.Code);
        Assert.Empty(db.ServiceRequests);
    }

    [Fact]
    public async Task TakeAsync_ValidTransition_SetsPresoInCarico()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        var taken = await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        Assert.NotNull(taken.TakenAt);
    }

    [Fact]
    public async Task TakeAsync_WrongSupplier_Throws()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.TakeAsync(created.Id, Guid.NewGuid(), "other"));
    }

    [Fact]
    public async Task TakeAsync_AlreadyTaken_ThrowsInvalidTransition()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.TakeAsync(created.Id, supplierOrgId, "supplier-user"));

        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotTakeMessageKey, ex.MessageKey);
    }

    [Fact]
    public async Task TakeAsync_UnknownRequest_ThrowsNotFoundWithCode()
    {
        await using var db = CreateDb();
        var (_, _, supplierOrgId, _) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.TakeAsync(Guid.NewGuid(), supplierOrgId, "supplier-user"));

        Assert.Equal(ServiceRequestErrorCodes.NotFound, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.NotFoundMessageKey, ex.MessageKey);
    }

    [Fact]
    public async Task CompleteAsync_FromPresoInCarico_SetsCompletato()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
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
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
        await service.CompleteAsync(created.Id, supplierOrgId, null);

        var paid = await service.MarkPaidAsync(created.Id, hostOrgId);

        Assert.Equal(ServiceRequestStatus.Pagato, paid.Status);
        Assert.NotNull(paid.PaidAt);
    }

    [Fact]
    public async Task MarkPaidAsync_BeforeCompletion_ThrowsInvalidTransitionAndLeavesItTaken()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => service.MarkPaidAsync(created.Id, hostOrgId));

        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotMarkPaidMessageKey, ex.MessageKey);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await db.ServiceRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task CompleteAsync_NewRequest_ThrowsInvalidTransition()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => service.CompleteAsync(created.Id, supplierOrgId, "Fatto"));

        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotCompleteMessageKey, ex.MessageKey);
        var stored = await db.ServiceRequests.SingleAsync();
        Assert.Equal(ServiceRequestStatus.Richiesto, stored.Status);
        Assert.Null(stored.CompletedAt);
    }

    [Fact]
    public async Task RejectAsync_TakenRequest_ThrowsInvalidTransitionWithoutEmailingTheHost()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
        var queuedBefore = queue.Snapshot().Count;

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.RejectAsync(created.Id, supplierOrgId, "Non disponibile"));

        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotRejectMessageKey, ex.MessageKey);
        Assert.Equal(queuedBefore, queue.Snapshot().Count);
        var stored = await db.ServiceRequests.SingleAsync();
        Assert.Equal(ServiceRequestStatus.PresoInCarico, stored.Status);
        Assert.Null(stored.RejectionReason);
    }

    [Fact]
    public async Task RejectAsync_ReasonWithSpaces_StoresItTrimmed()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        var rejected = await service.RejectAsync(created.Id, supplierOrgId, "  Non disponibile  ");

        Assert.Equal(ServiceRequestStatus.Rifiutato, rejected.Status);
        Assert.Equal("Non disponibile", rejected.RejectionReason);
    }

    [Fact]
    public async Task MarkPaidAsync_UnknownRequest_ThrowsNotFoundExceptionWithCode()
    {
        await using var db = CreateDb();
        var (hostOrgId, _, _, _) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
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
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
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
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
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
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        var colleaguesStay = NewBooking(hostOrgId, otherProperty.Id);
        db.Bookings.Add(colleaguesStay);
        await db.SaveChangesAsync();
        var colleagues = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, "auth0|colleague", otherProperty.Id, colleaguesStay.Id, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        var (ownerItems, ownerTotal) = await service.ListForHostAsync(
            new HostScope(hostOrgId, TestAuthHandler.DefaultUserId), ServiceRequestRentalContext.ShortRent, null, null, null, 1, 20);
        var (orgItems, orgTotal) = await service.ListForHostAsync(
            new HostScope(hostOrgId, null), ServiceRequestRentalContext.ShortRent, null, null, null, 1, 20);
        var (otherOrgItems, _) = await service.ListForHostAsync(
            new HostScope(Guid.NewGuid(), null), ServiceRequestRentalContext.ShortRent, null, null, null, 1, 20);

        Assert.Equal(own.Id, Assert.Single(ownerItems).Id);
        Assert.Equal(1, ownerTotal);
        Assert.Equal(2, orgTotal);
        Assert.Contains(orgItems, r => r.Id == colleagues.Id);
        Assert.Empty(otherOrgItems);
        Assert.Null(await service.GetByIdForHostAsync(
            colleagues.Id, new HostScope(hostOrgId, TestAuthHandler.DefaultUserId), ServiceRequestRentalContext.ShortRent));
        Assert.NotNull(await service.GetByIdForHostAsync(
            colleagues.Id, new HostScope(hostOrgId, null), ServiceRequestRentalContext.ShortRent));
    }

    [Fact]
    public async Task ListForHostAsync_WhenBookingIdProvided_ReturnsOnlyThatStaysRequests()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var nextStay = NewBooking(hostOrgId, propertyId);
        db.Bookings.Add(nextStay);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var matched = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, "matched", false));
        var other = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, nextStay.Id, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, "other", false));
        var scope = new HostScope(hostOrgId, TestAuthHandler.DefaultUserId);

        var (items, total) = await service.ListForHostAsync(
            scope, ServiceRequestRentalContext.ShortRent, status: null, propertyId: null, bookingId, page: 1, pageSize: 20);
        var (propertyItems, propertyTotal) = await service.ListForHostAsync(
            scope, ServiceRequestRentalContext.ShortRent, status: null, propertyId, bookingId: null, page: 1, pageSize: 20);

        Assert.Equal(1, total);
        Assert.Equal(matched.Id, Assert.Single(items).Id);
        Assert.Equal(2, propertyTotal);
        Assert.Equal(new[] { matched.Id, other.Id }.Order(), propertyItems.Select(r => r.Id).Order());
    }

    [Fact]
    public async Task RejectAsync_FromRichiesto_SetsRifiutato()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
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
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
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
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
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
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            category, ServiceRequestUrgency.Normal, null, false)));

        Assert.Equal(ServiceCategories.InvalidCategoryCode, ex.Code);
        Assert.Empty(db.ServiceRequests);
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public async Task CreateAsync_CodeWithDifferentCase_StoresNormalizedCode()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);

        var result = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            " Linen ", ServiceRequestUrgency.Normal, null, false));

        Assert.Equal(ServiceCategories.Linen, result.Category);
    }

    [Fact]
    public async Task CreateAsync_PublicSiteBaseUrlMissing_ThrowsConfigurationErrorWithoutCreatingRequest()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue, publicSiteBaseUrl: null);

        await Assert.ThrowsAsync<EmailConfigurationException>(() =>
            service.CreateAsync(new CreateServiceRequestCommand(
                hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
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
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
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
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var created = await CreateService(db).CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
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

    private static async Task<(Guid HostOrgId, Guid PropertyId, Guid SupplierOrgId, Guid BookingId)> SeedHostAndSupplierAsync(
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

        var property = NewProperty(hostOrg.Id, "Test Property", propertyCity);
        db.Properties.Add(property);

        // The stay short-rent requests are for (D2).
        var booking = NewBooking(hostOrg.Id, property.Id);
        db.Bookings.Add(booking);

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
        return (hostOrg.Id, property.Id, supplierOrg.Id, booking.Id);
    }

    private static Property NewProperty(Guid orgId, string name, string city = "H501") => new()
    {
        OwnerId = TestAuthHandler.DefaultUserId,
        OrgId = orgId,
        Name = name,
        Address = "Via Test 1",
        City = city,
        PostalCode = "00100",
        Bedrooms = 2,
        Bathrooms = 1,
        MaxGuests = 4,
        NightlyRate = 100m,
        CinCode = "IT058091C27G5FFZDZ",
    };

    private static Booking NewBooking(Guid orgId, Guid propertyId) => new()
    {
        OrgId = orgId,
        PropertyId = propertyId,
        GuestId = Guid.NewGuid(),
        CheckInDate = TimeProvider.System.TodayInRome().AddDays(3),
        CheckOutDate = TimeProvider.System.TodayInRome().AddDays(5),
        NumberOfGuests = 2,
        Status = BookingStatus.Confirmed,
    };
}
