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
using Casazen.Tests.Unit.Push;
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
    public async Task TakeAsync_SupplierAcceptedAnOlderTermsVersion_RefusesUntilItAcceptsTheCurrentOne()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        var profile = await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == supplierOrgId);
        profile.TosAcceptedAt = DateTime.UtcNow;
        profile.TosVersion = "2025-01-v1";
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.TakeAsync(created.Id, supplierOrgId, "auth0|supplier-member"));

        Assert.Equal(SupplierActivation.TosReacceptanceRequiredCode, ex.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await db.ServiceRequests.AsNoTracking().SingleAsync(r => r.Id == created.Id)).Status);

        profile.TosVersion = LegalTestServices.TosVersion;
        await db.SaveChangesAsync();
        var taken = await service.TakeAsync(created.Id, supplierOrgId, "auth0|supplier-member");
        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
    }

    [Fact]
    public async Task TakeAsync_SupplierAcceptedBeforeVersionsWereRecorded_IsNotBlocked()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var service = CreateService(db);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        (await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == supplierOrgId)).TosAcceptedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var taken = await service.TakeAsync(created.Id, supplierOrgId, "auth0|supplier-member");

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
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

        var completed = await service.CompleteAsync(created.Id, supplierOrgId, new CompleteServiceRequestCommand("Done"));

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
        Assert.Equal(ServiceRequestActorParty.Host, paid.PaidBy);
    }

    // ─── SU-09: the supplier is told when the host marks a request as paid ───

    [Fact]
    public async Task MarkPaidAsync_FromCompletato_QueuesTheEmailAndOnePushToTheSupplier()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var push = new RecordingPushQueue();
        var service = CreateService(db, queue, push: push);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
        await service.CompleteAsync(created.Id, supplierOrgId, null);
        queue.Queued.Clear();
        var pushesBefore = push.Queued.Count;

        await service.MarkPaidAsync(created.Id, hostOrgId);

        var (to, content, template) = Assert.Single(queue.Queued);
        Assert.Equal("supplier@test.com", to);
        Assert.Equal(EmailTemplates.Names.ServiceRequestPaid, template);
        Assert.Equal("Richiesta fornitore segnata come pagata — Test Property", content.Subject);
        Assert.Contains("Supplier Srl", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<strong>Pulizie</strong>", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains($"{EmailTestHelpers.PublicSiteBaseUrl}/app/supplier/inbox", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("fuori da CasaZen", content.HtmlBody, StringComparison.Ordinal);
        var toSupplier = Assert.Single(push.Queued.Skip(pushesBefore));
        Assert.Equal(PushDeliveryKeys.ServiceRequestStatus(created.Id, ServiceRequestStatus.Pagato), toSupplier.DeliveryKey);
        Assert.Equal(PushAudience.SupplierOrg(supplierOrgId), toSupplier.Audience);
        Assert.Equal(PushTypes.ServiceRequestPaid, toSupplier.Payload.Type);
        Assert.Equal("Richiesta segnata come pagata", toSupplier.Payload.Title);
        Assert.Equal("Pulizie presso Test Property: l'host ha segnato il servizio come pagato.", toSupplier.Payload.Body);
        // The stay is the host's: the supplier's push carries no booking and opens a screen the app has.
        Assert.Null(toSupplier.Payload.BookingId);
        Assert.Equal(PushRoutes.Properties, toSupplier.Payload.Route);
        Assert.Equal(created.Id, toSupplier.Payload.ServiceRequestId);
    }

    [Fact]
    public async Task MarkPaidAsync_SupplierSuspendedAfterCompleting_StillGetsTheNotification()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
        await service.CompleteAsync(created.Id, supplierOrgId, null);
        (await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == supplierOrgId)).Status = SupplierStatus.Suspended;
        await db.SaveChangesAsync();
        queue.Queued.Clear();

        var paid = await service.MarkPaidAsync(created.Id, hostOrgId);

        // Paying is the host's action and a suspended supplier is still owed what it completed (SU-12).
        Assert.Equal(ServiceRequestStatus.Pagato, paid.Status);
        Assert.Equal(EmailTemplates.Names.ServiceRequestPaid, Assert.Single(queue.Queued).Template);
    }

    [Fact]
    public async Task MarkPaidAsync_BeforeCompletion_ThrowsAndNotifiesNobody()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var push = new RecordingPushQueue();
        var service = CreateService(db, queue, push: push);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        queue.Queued.Clear();
        var pushesBefore = push.Queued.Count;

        await Assert.ThrowsAsync<DomainRuleException>(() => service.MarkPaidAsync(created.Id, hostOrgId));

        Assert.Empty(queue.Queued);
        Assert.Equal(pushesBefore, push.Queued.Count);
    }

    [Fact]
    public async Task MarkPaidAsync_NotificationCannotBeQueued_StillMarksTheRequestPaid()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var push = new Mock<IPushNotificationService>();
        var service = CreateService(db, push: push.Object);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
        await service.CompleteAsync(created.Id, supplierOrgId, null);
        push.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
            .Throws(new InvalidOperationException("queue down"));

        var paid = await service.MarkPaidAsync(created.Id, hostOrgId);

        // The status is already saved: a notification failure is logged, never an error for the host (A4-20).
        Assert.Equal(ServiceRequestStatus.Pagato, paid.Status);
        Assert.Equal(ServiceRequestStatus.Pagato, (await db.ServiceRequests.SingleAsync(r => r.Id == created.Id)).Status);
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

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => service.CompleteAsync(created.Id, supplierOrgId, new CompleteServiceRequestCommand("Fatto")));

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
        Assert.Equal("Nuova richiesta di servizio — H501", content.Subject);
        Assert.Contains($"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/app/supplier/inbox\"", content.HtmlBody);
        // Decision D9 (SP-04): the supplier gets the comune, not the name of the property nor the host's notes.
        Assert.DoesNotContain("Test Property", content.HtmlBody);
        Assert.DoesNotContain("Turnover", content.HtmlBody);
        Assert.DoesNotContain("casazen.it", content.HtmlBody);
    }

    [Fact]
    public async Task CreateAsync_NotesWithMarkup_NeverReachTheSupplierEmailBeforeTheTake()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal,
            "<a href=\"https://phish.example\">Conferma IBAN</a><script>alert(1)</script>", false));

        // Decision D9 (SP-04): the host's notes are shown to the supplier only after the take, so the email does not carry them.
        var html = Assert.Single(queue.Queued).Content.HtmlBody;
        Assert.DoesNotContain("phish.example", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("Conferma IBAN", html);
        Assert.Contains("richiesta di <strong>Pulizie</strong>", html);
    }

    [Fact]
    public async Task CreateAsync_ComuneWithMarkup_IsHtmlEncodedInSupplierEmail()
    {
        // The comune is text the host typed: it is the one host-written value the supplier's email still carries.
        const string comune = "<script>alert(1)</script>";
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, comune, SupplierStatus.Active, comune);
        var queue = new RecordingEmailQueue();
        var service = CreateService(db, queue);

        await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        var html = Assert.Single(queue.Queued).Content.HtmlBody;
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
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
        push.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
            .Throws(new InvalidOperationException("job storage down"));
        var service = CreateService(db, queue.Object, push: push.Object);

        var taken = await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        var saved = await db.ServiceRequests.AsNoTracking().SingleAsync(r => r.Id == created.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, saved.Status);
        push.Verify(
            p => p.Enqueue(
                PushDeliveryKeys.ServiceRequestStatus(created.Id, ServiceRequestStatus.PresoInCarico),
                PushAudience.PropertyHosts(propertyId),
                It.IsAny<PushNotificationPayload>()),
            Times.Once);
    }

    // ─── MO-04 (A6-08): pushes of the supplier decisions and of a new request ───

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico, "service-request-taken", "Richiesta presa in carico", "Pulizie presso Test Property: il fornitore ha preso in carico la richiesta.")]
    [InlineData(ServiceRequestStatus.Completato, "service-request-completed", "Servizio completato", "Pulizie presso Test Property: il fornitore ha completato il servizio.")]
    [InlineData(ServiceRequestStatus.Rifiutato, "service-request-rejected", "Richiesta rifiutata dal fornitore", "Pulizie presso Test Property: il fornitore ha rifiutato la richiesta. Scegli un altro fornitore.")]
    public async Task SupplierStatusChange_ValidTransition_QueuesOneHostPushWithItsOwnText(
        ServiceRequestStatus target,
        string type,
        string title,
        string body)
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var push = new RecordingPushQueue();
        var service = CreateService(db, push: push);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));

        switch (target)
        {
            case ServiceRequestStatus.PresoInCarico:
                await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
                break;
            case ServiceRequestStatus.Completato:
                await service.TakeAsync(created.Id, supplierOrgId, "supplier-user");
                await service.CompleteAsync(created.Id, supplierOrgId, null);
                break;
            default:
                await service.RejectAsync(created.Id, supplierOrgId, "Non disponibile");
                break;
        }

        var toHost = Assert.Single(push.Queued, q => q.DeliveryKey == PushDeliveryKeys.ServiceRequestStatus(created.Id, target));
        Assert.Equal(PushAudience.PropertyHosts(propertyId), toHost.Audience);
        Assert.Equal(type, toHost.Payload.Type);
        Assert.Equal(title, toHost.Payload.Title);
        Assert.Equal(body, toHost.Payload.Body);
        Assert.Equal(PushRoutes.Booking(bookingId), toHost.Payload.Route);
        Assert.Equal(bookingId, toHost.Payload.BookingId);
        Assert.Equal(created.Id, toHost.Payload.ServiceRequestId);
        // The rejection reason is the supplier's free text: email only, never on the lock screen.
        Assert.DoesNotContain("Non disponibile", toHost.Payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectAsync_WithoutStay_QueuesHostPushOpeningThePropertyList()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, _) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var push = new RecordingPushQueue();
        var service = CreateService(db, push: push);
        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, null, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false, ServiceRequestRentalContext.LongRent));

        await service.RejectAsync(created.Id, supplierOrgId, "Non disponibile");

        var rejected = Assert.Single(push.Queued, q => q.Payload.Type == PushTypes.ServiceRequestRejected);
        Assert.Equal(PushRoutes.Properties, rejected.Payload.Route);
        Assert.Null(rejected.Payload.BookingId);
    }

    [Fact]
    public async Task CreateAsync_NewRequest_QueuesOnePushToTheSupplierOrg()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        var push = new RecordingPushQueue();

        var created = await CreateService(db, push: push).CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, "Chiavi in portineria", false));

        var toSupplier = Assert.Single(push.Queued);
        Assert.Equal(PushDeliveryKeys.ServiceRequestCreated(created.Id), toSupplier.DeliveryKey);
        Assert.Equal(PushAudience.SupplierOrg(supplierOrgId), toSupplier.Audience);
        Assert.Equal(PushTypes.ServiceRequestCreated, toSupplier.Payload.Type);
        Assert.Equal("Nuova richiesta di servizio", toSupplier.Payload.Title);
        Assert.Equal("Pulizie a H501: accetta o rifiuta la richiesta dalla tua area fornitore.", toSupplier.Payload.Body);
        // The stay is the host's: the supplier's push carries no booking and opens a screen the app has.
        Assert.Null(toSupplier.Payload.BookingId);
        Assert.Equal(PushRoutes.Properties, toSupplier.Payload.Route);
        Assert.Equal(created.Id, toSupplier.Payload.ServiceRequestId);
    }

    [Fact]
    public async Task CreateAsync_RequestRefused_QueuesNoPush()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Suspended);
        var push = new RecordingPushQueue();

        await Assert.ThrowsAsync<DomainRuleException>(() => CreateService(db, push: push).CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false)));

        Assert.Empty(push.Queued);
    }

    [Fact]
    public async Task CreateThenReject_WithTheRealQueueAndJob_SupplierThenHostDevicesGetOnePushEach()
    {
        await using var db = CreateDb();
        var (hostOrgId, propertyId, supplierOrgId, bookingId) = await SeedHostAndSupplierAsync(db, "H501", SupplierStatus.Active);
        await SeedDevicesAsync(db, hostOrgId, supplierOrgId);
        var pipeline = new PushPipeline();
        var service = CreateService(db, push: pipeline.Queue);

        var created = await service.CreateAsync(new CreateServiceRequestCommand(
            hostOrgId, TestAuthHandler.DefaultUserId, propertyId, bookingId, supplierOrgId,
            "cleaning", ServiceRequestUrgency.Normal, null, false));
        await service.RejectAsync(created.Id, supplierOrgId, "Non disponibile");

        // Nothing is sent by the service itself: only jobs are queued.
        Assert.Empty(pipeline.Expo.SendRequests);
        Assert.Equal(2, pipeline.Jobs.Count);

        // The Hangfire worker runs the jobs, then runs them again (retry): one message per device and event.
        await pipeline.RunQueuedJobsAsync(db);
        await pipeline.RunQueuedJobsAsync(db);

        var messages = pipeline.Expo.Messages;
        Assert.Equal(2, messages.Count);
        var toSupplier = Assert.Single(messages, m => m.Data["type"] == PushTypes.ServiceRequestCreated);
        Assert.Equal("ExponentPushToken[supplier]", toSupplier.To);
        var toHost = Assert.Single(messages, m => m.Data["type"] == PushTypes.ServiceRequestRejected);
        Assert.Equal("ExponentPushToken[host]", toHost.To);
        Assert.Equal("Richiesta rifiutata dal fornitore", toHost.Title);
        Assert.Equal(PushRoutes.Booking(bookingId), toHost.Data["route"]);
    }

    /// <summary>The property owner (host) and a member of the supplier org, one phone each.</summary>
    private static async Task SeedDevicesAsync(AppDbContext db, Guid hostOrgId, Guid supplierOrgId)
    {
        db.Users.AddRange(
            new User { Id = TestAuthHandler.DefaultUserId, Email = "host@test.com", OrgId = hostOrgId, Role = UserRole.PropertyOwner, IsActive = true },
            new User { Id = "auth0|supplier-member", Email = "sup@test.com", OrgId = supplierOrgId, SupplierOrgId = supplierOrgId, Role = UserRole.Supplier, IsActive = true });
        db.DeviceRegistrations.AddRange(
            new DeviceRegistration { UserId = TestAuthHandler.DefaultUserId, OrgId = hostOrgId, Platform = "ios", PushToken = "ExponentPushToken[host]", DeviceId = Guid.NewGuid().ToString() },
            new DeviceRegistration { UserId = "auth0|supplier-member", OrgId = supplierOrgId, Platform = "android", PushToken = "ExponentPushToken[supplier]", DeviceId = Guid.NewGuid().ToString() });
        await db.SaveChangesAsync();
    }

    private static ServiceRequestService CreateService(
        AppDbContext db,
        IEmailQueue? queue = null,
        string? publicSiteBaseUrl = EmailTestHelpers.PublicSiteBaseUrl,
        IPushNotificationService? push = null) =>
        new ServiceRequestTestKit(db, queue, push, publicSiteBaseUrl: publicSiteBaseUrl).Service;

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
