using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Events;

/// <summary>
/// E08/F06/US01/T02 (NF-79, ADR-022-R6, R7, ADR-026-R3): what the booking events start by themselves. A stay that ends without a cleaning
/// request raises a cleaning «To do», a booking with a special request raises another, a booking from a channel pushes the host; each is
/// raised once for an event delivered again, holds no guest data and works for the org of the event. On EF InMemory, on the world of
/// <see cref="HostScopeScenario"/>: per property a stay checked out (Oct 1-4, its check-out record open), an upcoming confirmed stay
/// (Oct 10-13, with a cleaning request) and a request waiting for the host (Nov 20-23, pending).
/// </summary>
public class BookingEventConsumersTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private readonly RecordingPushQueue _pushes = new();
    private readonly RecordingEmailQueue _emails = new();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // --- Cleaning «To do» ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cleaning_AStayThatEndsWithNoCleaningRequest_RaisesOneItemTiedToThePropertyAndTheNextArrival()
    {
        var ended = await StayAsync(_world.Granted, BookingStatus.CheckedOut);

        await Cleaning().HandleAsync(CheckOutCompleted(ended.Id), Ct);
        // Delivered again, and as another event of the same stay: still one item (ADR-022-R7).
        await Cleaning().HandleAsync(CheckOutCompleted(ended.Id), Ct);

        var item = Assert.Single(await TodosAsync(ended.Id));
        Assert.Equal(nameof(ActivityType.CleaningDue), item.ActivityType);
        Assert.Equal(ActivityAction.CompleteCheckOut, item.Action);
        Assert.Equal(InternalActivity.BookingEntityType, item.EntityType);
        Assert.Equal(_world.Granted.Id, item.PropertyId);
        Assert.Equal(_world.OrgId, item.OrgId);
        // The next arrival of the property is the stay of Oct 10: it starts 12 hours from the clock of the scenario, within 48 hours.
        Assert.Equal(ActivityUrgency.High, item.Urgency);
        Assert.Equal(RomeCalendar.StartOfDayUtc(HostScopeScenario.Day(2026, 10, 10)), item.Deadline);
        Assert.Equal(EmailTexts.Get(BookingTodoItems.CleaningDueReason, EmailTemplates.DefaultCulture), item.Reason);
        Assert.Null(item.CompletedAt);
    }

    [Fact]
    public async Task Cleaning_TheItemClosesWhenThePropertyIsDeclaredReady_AndNeverComesBackForARedeliveredEvent()
    {
        var ended = await StayAsync(_world.Granted, BookingStatus.CheckedOut);
        await Cleaning().HandleAsync(CheckOutCompleted(ended.Id), Ct);
        var close = new ActivityAutoCloseHandler(_db, Activities(), NullLogger<ActivityAutoCloseHandler>.Instance);

        await close.HandleAsync(Event(DomainEventType.PropertyReadyDeclared, ended.Id), Ct);
        await Cleaning().HandleAsync(CheckOutCompleted(ended.Id), Ct);

        var item = Assert.Single(await TodosAsync(ended.Id));
        Assert.Equal(InternalActivity.StatusCompleted, item.Status);
        Assert.Equal(InternalActivity.ClosedBySystem, item.ClosedBy);
        Assert.NotNull(item.CompletedAt);
        Assert.Empty(await Activities().GetPendingAsync(_world.OrgWide, Ct));
    }

    [Fact]
    public async Task Cleaning_AnItemTheHostClosedByHandIsNotRaisedAgainByARedeliveredEvent()
    {
        var ended = await StayAsync(_world.Granted, BookingStatus.CheckedOut);
        await Cleaning().HandleAsync(CheckOutCompleted(ended.Id), Ct);
        await Activities().CloseAsync(Assert.Single(await TodosAsync(ended.Id)).Id, _world.OwnerUserId, Ct);

        await Cleaning().HandleAsync(CheckOutCompleted(ended.Id), Ct);

        Assert.Single(await TodosAsync(ended.Id));
    }

    [Fact]
    public async Task Cleaning_NothingIsRaised_WhenThePropertyIsReadyOrACleaningIsRequested()
    {
        var ready = await StayAsync(_world.Granted, BookingStatus.CheckedOut);
        _db.ChangeTracker.Clear();
        (await _db.StayCheckouts.SingleAsync(c => c.BookingId == ready.Id)).PropertyReadyAt = HostScopeScenario.Now.UtcDateTime;
        await _db.SaveChangesAsync();

        // The stay of Oct 10 has a cleaning request of its own (the scenario's): it is requested, not due.
        var requested = await StayAsync(_world.Granted, BookingStatus.Confirmed);

        await Cleaning().HandleAsync(CheckOutCompleted(ready.Id), Ct);
        await Cleaning().HandleAsync(CheckOutCompleted(requested.Id), Ct);

        Assert.Empty(await TodosAsync(ready.Id));
        Assert.Empty(await TodosAsync(requested.Id));

        // A request the host cancelled, or the supplier declined, cleans nothing: the item is raised.
        (await _db.ServiceRequests.SingleAsync(r => r.BookingId == requested.Id)).Status = ServiceRequestStatus.Annullato;
        await _db.SaveChangesAsync();
        await Cleaning().HandleAsync(CheckOutCompleted(requested.Id), Ct);
        Assert.Single(await TodosAsync(requested.Id));
    }

    [Fact]
    public async Task Cleaning_WithNoArrivalBooked_IsLowAndHasNoDeadline()
    {
        var ended = await StayAsync(_world.Granted, BookingStatus.CheckedOut);
        var upcoming = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        await ChangeAsync(upcoming.Id, b => b.Status = BookingStatus.Cancelled);

        await Cleaning().HandleAsync(CheckOutCompleted(ended.Id), Ct);

        var item = Assert.Single(await TodosAsync(ended.Id));
        Assert.Equal(ActivityUrgency.Low, item.Urgency);
        Assert.Null(item.Deadline);
    }

    [Fact]
    public void Cleaning_IsUrgentWithin48HoursOfTheNextArrival()
    {
        var now = HostScopeScenario.Now.UtcDateTime;

        Assert.Equal(ActivityUrgency.Low, BookingCleaningActivityHandler.UrgencyOf(null, now));
        Assert.Equal(ActivityUrgency.High, BookingCleaningActivityHandler.UrgencyOf(now.AddHours(-3), now));
        Assert.Equal(ActivityUrgency.High, BookingCleaningActivityHandler.UrgencyOf(now.AddHours(48), now));
        Assert.Equal(ActivityUrgency.Medium, BookingCleaningActivityHandler.UrgencyOf(now.AddHours(48).AddMinutes(1), now));
    }

    // --- Special-request «To do» ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SpecialRequest_ABookingWithARequest_RaisesOneItemForTheTwoEventsAndTheirRedelivery()
    {
        var booking = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        await ChangeAsync(booking.Id, b => b.SpecialRequests = "Allergica ai gatti");

        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingCreated, booking.Id), Ct);
        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingConfirmed, booking.Id), Ct);
        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingConfirmed, booking.Id), Ct);

        var item = Assert.Single(await TodosAsync(booking.Id));
        Assert.Equal(nameof(ActivityType.BookingSpecialRequest), item.ActivityType);
        Assert.Equal(ActivityAction.ReviewBooking, item.Action);
        Assert.Equal(InternalActivity.BookingEntityType, item.EntityType);
        Assert.Equal(_world.Granted.Id, item.PropertyId);
        Assert.Equal(ActivityUrgency.Medium, item.Urgency);
        Assert.Equal(RomeCalendar.StartOfDayUtc(HostScopeScenario.Day(2026, 10, 10)), item.Deadline);
        Assert.Equal(EmailTexts.Get(BookingTodoItems.SpecialRequestReason, EmailTemplates.DefaultCulture), item.Reason);
    }

    [Fact]
    public async Task SpecialRequest_NothingIsRaised_WithoutARequest_OrForAHoldThatIsNotConfirmed()
    {
        var plain = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        var hold = await StayAsync(_world.Granted, BookingStatus.Pending);
        await ChangeAsync(plain.Id, b => b.SpecialRequests = "   ");
        await ChangeAsync(hold.Id, b => b.SpecialRequests = "Culla in camera");

        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingCreated, plain.Id), Ct);
        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingCreated, hold.Id), Ct);

        Assert.Empty(await TodosAsync(plain.Id));
        Assert.Empty(await TodosAsync(hold.Id));

        // The hold is paid: the booking is confirmed, and now there is something to read.
        await ChangeAsync(hold.Id, b => b.Status = BookingStatus.Confirmed);
        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingConfirmed, hold.Id), Ct);
        Assert.Single(await TodosAsync(hold.Id));
    }

    [Fact]
    public async Task SpecialRequest_TheItemClosesAtTheCheckInOrWhenTheBookingIsCancelled_AndIsNotRaisedAgain()
    {
        var checkedIn = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        var cancelled = await StayAsync(_world.Hidden, BookingStatus.Confirmed);
        await ChangeAsync(checkedIn.Id, b => b.SpecialRequests = "Allergica ai gatti");
        await ChangeAsync(cancelled.Id, b => b.SpecialRequests = "Arrivo tardi");
        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingConfirmed, checkedIn.Id), Ct);
        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingConfirmed, cancelled.Id), Ct);
        var close = new ActivityAutoCloseHandler(_db, Activities(), NullLogger<ActivityAutoCloseHandler>.Instance);

        await close.HandleAsync(Event(DomainEventType.CheckInCompleted, checkedIn.Id), Ct);
        await close.HandleAsync(Event(DomainEventType.BookingCancelled, cancelled.Id), Ct);
        // The two events are delivered again after the items were closed.
        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingConfirmed, checkedIn.Id), Ct);
        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingConfirmed, cancelled.Id), Ct);

        foreach (var booking in new[] { checkedIn, cancelled })
        {
            var item = Assert.Single(await TodosAsync(booking.Id));
            Assert.Equal(InternalActivity.StatusCompleted, item.Status);
            Assert.Equal(InternalActivity.ClosedBySystem, item.ClosedBy);
        }
    }

    // --- Both items: no guest data, the org's language, the org of the event ------------------------------------------------------

    /// <summary>
    /// Proof (privacy; ADR-026-R3): the reason of the cleaning item and of the special-request item names the fact and nobody: nothing of
    /// the guest (name, email, phone) and nothing of the request they wrote, in the item or in the texts, in Italian and in English.
    /// </summary>
    [Fact]
    public async Task BookingActivities_NoGuestData_Test()
    {
        var guest = new Guest
        {
            OrgId = _world.OrgId,
            FirstName = "Giulietta",
            LastName = "Capuleti",
            Email = "giulietta.capuleti@example.com",
            PhoneNumber = "+393331112233",
        };
        _db.Guests.Add(guest);
        await _db.SaveChangesAsync();
        var withRequest = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        var ended = await StayAsync(_world.Granted, BookingStatus.CheckedOut);
        await ChangeAsync(withRequest.Id, b =>
        {
            b.GuestId = guest.Id;
            b.SpecialRequests = "Allergica ai gatti, arriva con un cane";
        });
        await ChangeAsync(ended.Id, b => b.GuestId = guest.Id);

        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingConfirmed, withRequest.Id), Ct);
        await Cleaning().HandleAsync(CheckOutCompleted(ended.Id), Ct);

        _db.ChangeTracker.Clear();
        var items = await _db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        Assert.Equal(
            [nameof(ActivityType.BookingSpecialRequest), nameof(ActivityType.CleaningDue)],
            items.Select(i => i.ActivityType).OrderBy(t => t, StringComparer.Ordinal).ToList());
        var forbidden = new[] { "Giulietta", "Capuleti", "giulietta.capuleti", "+39333", "Allergica", "gatti", "cane" };
        foreach (var item in items)
        {
            // Everything the item says, reason included, and the staff fields the list never sends: none holds the guest.
            var said = string.Join('|', item.Reason, item.EntityType, item.EntityId, item.StaffUserId, item.Notes);
            foreach (var word in forbidden)
                Assert.DoesNotContain(word, said, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var key in new[] { BookingTodoItems.CleaningDueReason, BookingTodoItems.SpecialRequestReason })
        {
            foreach (var culture in new[] { EmailTemplates.DefaultCulture, CultureInfo.GetCultureInfo("en") })
            {
                var text = EmailTexts.Get(key, culture);
                Assert.DoesNotContain("{", text, StringComparison.Ordinal);
                foreach (var word in forbidden)
                    Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Reason_FollowsTheLanguageOfTheOrg()
    {
        _db.ChangeTracker.Clear();
        (await _db.Orgs.SingleAsync(o => o.Id == _world.OrgId)).DefaultLanguage = "en";
        await _db.SaveChangesAsync();
        var booking = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        await ChangeAsync(booking.Id, b => b.SpecialRequests = "Culla in camera");

        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingConfirmed, booking.Id), Ct);

        Assert.Equal("New booking with a special request", Assert.Single(await TodosAsync(booking.Id)).Reason);
    }

    [Fact]
    public async Task Consumers_WorkForTheOrgOfTheEventOnly()
    {
        var booking = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        var ended = await StayAsync(_world.Granted, BookingStatus.CheckedOut);
        await ChangeAsync(booking.Id, b => b.SpecialRequests = "Culla in camera");

        // The booking exists, but not in the org the event names: no item, as for a booking that does not exist.
        var now = HostScopeScenario.Now.UtcDateTime;
        await SpecialRequest().HandleAsync(
            new DomainEvent(Guid.NewGuid(), _world.OtherOrgId, DomainEventType.BookingConfirmed, booking.Id, now, "{}"), Ct);
        await Cleaning().HandleAsync(
            new DomainEvent(Guid.NewGuid(), _world.OtherOrgId, DomainEventType.CheckOutCompleted, ended.Id, now, "{}"), Ct);
        await SpecialRequest().HandleAsync(Event(DomainEventType.BookingConfirmed, Guid.NewGuid()), Ct);

        _db.ChangeTracker.Clear();
        Assert.Empty(await _db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync());
    }

    [Fact]
    public async Task Consumers_IgnoreTheEventsThatAreNotTheirs()
    {
        var booking = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        var ended = await StayAsync(_world.Granted, BookingStatus.CheckedOut);
        await ChangeAsync(booking.Id, b => b.SpecialRequests = "Culla in camera");

        foreach (var type in Enum.GetValues<DomainEventType>().Where(t => t != DomainEventType.BookingCreated && t != DomainEventType.BookingConfirmed))
            await SpecialRequest().HandleAsync(Event(type, booking.Id), Ct);
        foreach (var type in Enum.GetValues<DomainEventType>().Where(t => t != DomainEventType.CheckOutCompleted))
            await Cleaning().HandleAsync(Event(type, ended.Id), Ct);

        _db.ChangeTracker.Clear();
        Assert.Empty(await _db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync());
    }

    // --- The alert of a booking from a channel ----------------------------------------------------------------------------------

    [Fact]
    public async Task ChannelAlert_ABookingFromTheChannelManager_PushesTheHostOnceWithTheChannelName()
    {
        var booking = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        var guest = new Guest { OrgId = _world.OrgId, FirstName = "Anna", LastName = "Verdi", Email = "anna.verdi@example.com" };
        _db.Guests.Add(guest);
        await _db.SaveChangesAsync();
        await ChangeAsync(booking.Id, b =>
        {
            b.Source = BookingSource.Airbnb;
            b.GuestId = guest.Id;
        });

        // Its creation and its confirmation, and each of them delivered again: one booking, one push key.
        await ChannelAlert().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking.Id, BookingEventOrigin.Octorate), Ct);
        await ChannelAlert().HandleAsync(await BookedAsync(DomainEventType.BookingConfirmed, booking.Id, BookingEventOrigin.Octorate), Ct);
        await ChannelAlert().HandleAsync(await BookedAsync(DomainEventType.BookingConfirmed, booking.Id, BookingEventOrigin.Octorate), Ct);

        Assert.Equal(3, _pushes.Queued.Count);
        Assert.Single(_pushes.Queued.Select(q => q.DeliveryKey).Distinct());
        var push = _pushes.Queued[0];
        Assert.Equal(PushDeliveryKeys.NewBooking(booking.Id), push.DeliveryKey);
        Assert.Equal(PushAudience.BookingHosts(booking.Id), push.Audience);
        Assert.Equal(PushTypes.NewBooking, push.Payload.Type);
        Assert.Equal(PushRoutes.Booking(booking.Id), push.Payload.Route);
        Assert.Equal(booking.Id, push.Payload.BookingId);
        Assert.Equal("Nuova prenotazione da Airbnb", push.Payload.Title);
        Assert.Equal("Trullo: dal 10/10/2026 al 13/10/2026, ospiti: 2.", push.Payload.Body);
        // No email: the channel writes to its guest, and the push is the host's alert. No guest name on the lock screen.
        Assert.Empty(_emails.Snapshot());
        Assert.DoesNotContain("Anna", push.Payload.Title + push.Payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Verdi", push.Payload.Title + push.Payload.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BookingEventOrigin.Host)]
    [InlineData(BookingEventOrigin.Guest)]
    [InlineData(BookingEventOrigin.System)]
    public async Task ChannelAlert_ABookingMadeInCasaZen_HasItsOwnAlertAndGetsNoSecondOne(BookingEventOrigin origin)
    {
        var booking = await StayAsync(_world.Granted, BookingStatus.Confirmed);

        await ChannelAlert().HandleAsync(await BookedAsync(DomainEventType.BookingConfirmed, booking.Id, origin), Ct);
        await ChannelAlert().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking.Id, origin), Ct);

        Assert.Empty(_pushes.Queued);
    }

    [Fact]
    public async Task ChannelAlert_OnlyAConfirmedBookingAlerts_AndABadPayloadIsIgnored()
    {
        var booking = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        await ChangeAsync(booking.Id, b => b.Source = BookingSource.Airbnb);

        // Said pending by the event: nothing to announce yet.
        await ChannelAlert().HandleAsync(
            await BookedAsync(DomainEventType.BookingCreated, booking.Id, BookingEventOrigin.Octorate, BookingStatus.Pending), Ct);

        // Said confirmed by the event, cancelled by the time it is handled: the notifier looks at the booking and sends nothing.
        await ChangeAsync(booking.Id, b => b.Status = BookingStatus.Cancelled);
        var confirmed = await BookedAsync(DomainEventType.BookingConfirmed, booking.Id, BookingEventOrigin.Octorate, BookingStatus.Confirmed);
        await ChannelAlert().HandleAsync(confirmed, Ct);

        // A payload that is not JSON, one that is not an object, and an event of another kind.
        await ChannelAlert().HandleAsync(confirmed with { PayloadJson = "not json" }, Ct);
        await ChannelAlert().HandleAsync(confirmed with { PayloadJson = "[]" }, Ct);
        await ChannelAlert().HandleAsync(confirmed with { Type = DomainEventType.CheckOutCompleted }, Ct);

        Assert.Empty(_pushes.Queued);
    }

    // --- Registration and closing rules ---------------------------------------------------------------------------------------

    [Fact]
    public void AddCasazenBookingOperations_RegistersTheConsumersOnTheBus()
    {
        var services = new ServiceCollection();

        services.AddCasazenBookingOperations();

        var handlers = services
            .Where(d => d.ServiceType == typeof(IEventHandler<DomainEvent>))
            .Select(d => d.ImplementationType)
            .ToList();
        // In the order of registration: the three of E08/F06 (cleaning «To do», special request «To do», channel alert), then the ones
        // that write to Octorate: the booking created on confirmation (E08/F01/US01/T03), the payment recorded once paid (T04) and the
        // cancellation pushed after the local one (E08/F02/US02/T02), then the one that raises the «To do» of a booking from the channel
        // manager on taken nights (E08/F01/US02/T02).
        Assert.Equal(
            [
                typeof(BookingCleaningActivityHandler),
                typeof(BookingSpecialRequestActivityHandler),
                typeof(ChannelBookingAlertHandler),
                typeof(OctorateBookingCreateHandler),
                typeof(OctoratePaymentHandler),
                typeof(OctorateBookingCancelHandler),
                typeof(BookingOverlapHandler),
            ],
            handlers);
    }

    [Fact]
    public void AddCasazenBookingOperations_RegistersTheAvailabilityOfTheChannelManager()
    {
        var services = new ServiceCollection();

        services.AddCasazenBookingOperations();

        // BookingService needs it to write a booking (E08/F01/US02/T01, NF-80): the check that no booking takes nights the channel manager
        // refuses is the service's, and the container cannot build the service without this registration.
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IChannelManagerAvailability));
        Assert.Equal(typeof(ChannelManagerAvailabilityService), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void TheClosingRulesOfTheNewItems_AreInTheAutoCloseHandler()
    {
        Assert.Contains(ActivityType.BookingSpecialRequest, ActivityAutoCloseHandler.Rules[DomainEventType.CheckInCompleted]);
        Assert.Contains(ActivityType.BookingSpecialRequest, ActivityAutoCloseHandler.Rules[DomainEventType.BookingCancelled]);
        Assert.Contains(ActivityType.CleaningDue, ActivityAutoCloseHandler.Rules[DomainEventType.PropertyReadyDeclared]);
        // What E04 closed before is still closed.
        Assert.Contains(ActivityType.CheckInPending, ActivityAutoCloseHandler.Rules[DomainEventType.CheckInCompleted]);
        Assert.Contains(ActivityType.CheckOutPending, ActivityAutoCloseHandler.Rules[DomainEventType.CheckOutCompleted]);
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------------------

    private InternalActivityService Activities() => new(_db, NullLogger<InternalActivityService>.Instance, _clock);

    private BookingCleaningActivityHandler Cleaning() =>
        new(_db, Activities(), _clock, NullLogger<BookingCleaningActivityHandler>.Instance);

    private BookingSpecialRequestActivityHandler SpecialRequest() =>
        new(_db, Activities(), NullLogger<BookingSpecialRequestActivityHandler>.Instance);

    private ChannelBookingAlertHandler ChannelAlert() =>
        new(
            new BookingNotifier(_db, _emails, EmailTestHelpers.Links(), _pushes, NullLogger<BookingNotifier>.Instance),
            NullLogger<ChannelBookingAlertHandler>.Instance);

    /// <summary>The To do items about the booking, closed ones too.</summary>
    private async Task<List<InternalActivity>> TodosAsync(Guid bookingId)
    {
        _db.ChangeTracker.Clear();
        return await _db.InternalActivities.AsNoTracking().Where(a => a.Action != null && a.EntityId == bookingId).ToListAsync();
    }

    /// <summary>The scenario's stay of the property with that status: Oct 10-13 confirmed, Oct 1-4 checked out, Nov 20-23 pending.</summary>
    private async Task<Booking> StayAsync(Property property, BookingStatus status)
    {
        _db.ChangeTracker.Clear();
        return await _db.Bookings.AsNoTracking().SingleAsync(b => b.PropertyId == property.Id && b.Status == status);
    }

    /// <summary>Changes the booking and saves it; the context starts again clean.</summary>
    private async Task ChangeAsync(Guid bookingId, Action<Booking> change)
    {
        _db.ChangeTracker.Clear();
        change(await _db.Bookings.SingleAsync(b => b.Id == bookingId));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private DomainEvent Event(DomainEventType type, Guid entityId) =>
        new(Guid.NewGuid(), _world.OrgId, type, entityId, HostScopeScenario.Now.UtcDateTime, "{}");

    private DomainEvent CheckOutCompleted(Guid bookingId) => Event(DomainEventType.CheckOutCompleted, bookingId);

    /// <summary>The event of a booking as the services record it: the payload of <see cref="BookingEventPayload"/>.</summary>
    private async Task<DomainEvent> BookedAsync(DomainEventType type, Guid bookingId, BookingEventOrigin origin, BookingStatus? status = null)
    {
        _db.ChangeTracker.Clear();
        var booking = await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        var payload = BookingEventPayload.Of(booking, origin) with { Status = status ?? booking.Status };
        return new DomainEvent(
            Guid.NewGuid(),
            _world.OrgId,
            type,
            booking.Id,
            HostScopeScenario.Now.UtcDateTime,
            JsonSerializer.Serialize(payload, DomainEventRecorder.PayloadJsonOptions));
    }
}
