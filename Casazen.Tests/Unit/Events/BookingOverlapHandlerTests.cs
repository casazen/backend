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
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Events;

public class BookingOverlapHandlerTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Overlap_ABookingFromTheChannelManagerOnTakenNights_RaisesOneUrgentItemAndOneAlert_OnceWhateverTheEvents()
    {
        var booking = await ChannelBookingAsync(_world.Granted, 12, 15);

        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingConfirmed, booking), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingConfirmed, booking), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, booking), Ct);

        var item = Assert.Single(await TodosAsync(booking.Id));
        Assert.Equal(nameof(ActivityType.BookingOverlap), item.ActivityType);
        Assert.Equal(ActivityAction.ReviewBooking, item.Action);
        Assert.Equal(InternalActivity.BookingEntityType, item.EntityType);
        Assert.Equal(_world.Granted.Id, item.PropertyId);
        Assert.Equal(_world.OrgId, item.OrgId);
        Assert.Equal(ActivityUrgency.High, item.Urgency);
        Assert.Equal(RomeCalendar.StartOfDayUtc(HostScopeScenario.Day(2026, 10, 12)), item.Deadline);
        Assert.Equal("Trullo: la prenotazione arriva su notti già occupate, dal 12/10/2026 al 13/10/2026", item.Reason);
        Assert.Null(item.CompletedAt);

        var alert = Assert.Single(await AlertsAsync(booking.Id));
        Assert.Equal(_world.OwnerUserId, alert.UserId);
        Assert.Equal(AlertType.BookingNeedsReview, alert.AlertType);
        Assert.Equal(PopupAlert.BookingEntityType, alert.EntityType);
        Assert.Equal(AlertUrgency.High, alert.Urgency);
        Assert.Equal(item.Deadline, alert.Deadline);
    }

    [Fact]
    public async Task Overlap_ACalendarBlockTakesTheNightsToo_AndTheReasonNamesTheFirstSharedRange()
    {
        var booking = await ChannelBookingAsync(_world.Hidden, 13, 16);
        _db.CalendarBlocks.Add(new CalendarBlock
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Hidden.Id,
            Source = CalendarBlockSource.Manual,
            StartUtc = HostScopeScenario.Day(2026, 10, 14),
            EndUtc = HostScopeScenario.Day(2026, 10, 17),
            Summary = "Lavori in casa",
        });
        await _db.SaveChangesAsync();

        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking), Ct);

        var item = Assert.Single(await TodosAsync(booking.Id));
        Assert.Equal("Casa Bianca: la prenotazione arriva su notti già occupate, dal 14/10/2026 al 16/10/2026", item.Reason);
        Assert.Equal(RomeCalendar.StartOfDayUtc(HostScopeScenario.Day(2026, 10, 14)), item.Deadline);
    }

    [Fact]
    public async Task Overlap_NothingIsRaised_ForAnOtherOrigin_ForFreeNights_ForABookingThatTakesNoNight_OrForAnOtherOrg()
    {
        var overlapping = await ChannelBookingAsync(_world.Granted, 12, 15);
        var turnover = await ChannelBookingAsync(_world.Hidden, 13, 15);
        var cancelled = await ChannelBookingAsync(_world.Granted, 11, 14, BookingStatus.Cancelled);

        foreach (var origin in new[] { BookingEventOrigin.Host, BookingEventOrigin.Guest, BookingEventOrigin.System })
        {
            await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, overlapping, origin), Ct);
            await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, overlapping, origin), Ct);
        }

        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, turnover), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, cancelled), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCancelled, cancelled), Ct);

        var otherOrg = (await BookedAsync(DomainEventType.BookingCreated, overlapping)) with { OrgId = _world.OtherOrgId };
        await Handler().HandleAsync(otherOrg, Ct);
        await Handler().HandleAsync(otherOrg with { EntityId = Guid.NewGuid() }, Ct);

        var created = await BookedAsync(DomainEventType.BookingCreated, overlapping);
        await Handler().HandleAsync(created with { PayloadJson = "not json" }, Ct);
        await Handler().HandleAsync(created with { PayloadJson = "[]" }, Ct);
        await Handler().HandleAsync(created with { PayloadJson = "{}" }, Ct);
        foreach (var type in Enum.GetValues<DomainEventType>().Where(t => t is not (DomainEventType.BookingCreated
                     or DomainEventType.BookingConfirmed
                     or DomainEventType.BookingModified
                     or DomainEventType.BookingCancelled)))
        {
            await Handler().HandleAsync(created with { Type = type }, Ct);
        }

        _db.ChangeTracker.Clear();
        Assert.Empty(await _db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync());
        Assert.Empty(await _db.PopupAlerts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Overlap_TheNightsTheChannelManagerHoldsClosed_AreItsOwnReservation_AndNotAConflict_ButTheBlockOfTheHostIs()
    {
        var withTheEcho = await ChannelBookingAsync(_world.Granted, 14, 17);
        var withTheHostBlock = await ChannelBookingAsync(_world.Hidden, 14, 17);
        _db.CalendarBlocks.AddRange(
            new CalendarBlock
            {
                OrgId = _world.OrgId,
                PropertyId = _world.Granted.Id,
                Source = CalendarBlockSource.Octorate,
                ExternalUid = "octorate-2026-10-14",
                StartUtc = HostScopeScenario.Day(2026, 10, 14),
                EndUtc = HostScopeScenario.Day(2026, 10, 17),
                Summary = "Closed on the channel manager",
            },
            new CalendarBlock
            {
                OrgId = _world.OrgId,
                PropertyId = _world.Hidden.Id,
                Source = CalendarBlockSource.Manual,
                StartUtc = HostScopeScenario.Day(2026, 10, 14),
                EndUtc = HostScopeScenario.Day(2026, 10, 17),
                Summary = "Lavori in casa",
            });
        await _db.SaveChangesAsync();

        foreach (var type in new[] { DomainEventType.BookingCreated, DomainEventType.BookingConfirmed, DomainEventType.BookingModified })
        {
            await Handler().HandleAsync(await BookedAsync(type, withTheEcho), Ct);
            await Handler().HandleAsync(await BookedAsync(type, withTheHostBlock), Ct);
        }

        Assert.Empty(await TodosAsync(withTheEcho.Id));
        Assert.Empty(await AlertsAsync(withTheEcho.Id));

        Assert.Null(Assert.Single(await TodosAsync(withTheHostBlock.Id)).CompletedAt);
        Assert.NotEmpty(await AlertsAsync(withTheHostBlock.Id));
    }

    [Fact]
    public async Task BookingOverlapActivity_NoGuestData_Test()
    {
        var booking = await ChannelBookingAsync(_world.Granted, 12, 15);

        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking), Ct);

        var item = Assert.Single(await TodosAsync(booking.Id));
        var alert = Assert.Single(await AlertsAsync(booking.Id));
        var forbidden = new[] { "Giulietta", "Capuleti", "giulietta.capuleti", "+39333", "Anna", "Allergica", "gatti", "cane" };
        var said = string.Join('|', item.Reason, item.EntityType, item.EntityId, item.StaffUserId, item.Notes, alert.EntityType, alert.AlertType, alert.UserId);
        foreach (var word in forbidden)
            Assert.DoesNotContain(word, said, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Trullo", item.Reason, StringComparison.Ordinal);
        Assert.Contains("12/10/2026", item.Reason, StringComparison.Ordinal);

        var conflict = new CalendarAlert(
            "conflict-test",
            CalendarAlertKind.Conflict,
            CalendarAlertSeverity.High,
            _world.Granted.Id,
            "Trullo",
            new DateOnly(2026, 10, 12),
            new DateOnly(2026, 10, 13),
            []);
        var italian = BookingOverlapHandler.ReasonOf(EmailTemplates.DefaultCulture, "Trullo", conflict);
        var english = BookingOverlapHandler.ReasonOf(CultureInfo.GetCultureInfo("en"), "Trullo", conflict);
        Assert.Equal("Trullo: la prenotazione arriva su notti già occupate, dal 12/10/2026 al 13/10/2026", italian);
        Assert.Equal("Trullo: the booking arrives on nights that are already taken, from 12 October 2026 to 13 October 2026", english);
        foreach (var text in new[] { italian, english })
        {
            Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            foreach (var word in forbidden)
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task BookingOverlapAlert_GoesOnlyToWhoReachesTheProperty_Test()
    {
        await ChangePropertyAsync(_world.Granted.Id, p => p.ResponsibleUserId = _world.CollaboratorId);
        await ChangePropertyAsync(_world.Hidden.Id, p => p.ResponsibleUserId = _world.CollaboratorId);
        var onGranted = await ChannelBookingAsync(_world.Granted, 12, 15);
        var onHidden = await ChannelBookingAsync(_world.Hidden, 12, 15);

        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, onGranted), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, onHidden), Ct);

        var reachingGranted = (await AlertsAsync(onGranted.Id)).Select(a => a.UserId).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { _world.OwnerUserId, _world.CollaboratorId }.Order(StringComparer.Ordinal).ToList(), reachingGranted);

        var reachingHidden = Assert.Single(await AlertsAsync(onHidden.Id));
        Assert.Equal(_world.OwnerUserId, reachingHidden.UserId);
        Assert.Single(await TodosAsync(onHidden.Id));
    }

    [Fact]
    public async Task Overlap_TheItemClosesWhenTheOtherBookingIsCancelled_AndItsOwnCancellationClosesIt_AndNothingBringsItBack()
    {
        var booking = await ChannelBookingAsync(_world.Granted, 12, 15);
        var other = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking), Ct);
        Assert.Null(Assert.Single(await TodosAsync(booking.Id)).CompletedAt);

        await ChangeAsync(other.Id, b => b.Status = BookingStatus.Cancelled);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCancelled, other, BookingEventOrigin.Host), Ct);

        var closed = Assert.Single(await TodosAsync(booking.Id));
        Assert.Equal(InternalActivity.StatusCompleted, closed.Status);
        Assert.Equal(InternalActivity.ClosedBySystem, closed.ClosedBy);
        Assert.NotNull(closed.CompletedAt);

        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCancelled, other, BookingEventOrigin.Host), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingConfirmed, booking), Ct);
        Assert.Single(await TodosAsync(booking.Id));

        var second = await ChannelBookingAsync(_world.Hidden, 12, 15);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, second), Ct);
        Assert.Null(Assert.Single(await TodosAsync(second.Id)).CompletedAt);
        await ChangeAsync(second.Id, b => b.Status = BookingStatus.Cancelled);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCancelled, second), Ct);
        Assert.Equal(InternalActivity.ClosedBySystem, Assert.Single(await TodosAsync(second.Id)).ClosedBy);
    }

    [Fact]
    public async Task Overlap_ABookingThatMovesAway_ClosesTheItem_AndOneThatMovesOnOtherTakenNights_GetsAnItemThatSaysThem()
    {
        var movesAway = await ChannelBookingAsync(_world.Granted, 12, 15);
        var movesOver = await ChannelBookingAsync(_world.Hidden, 12, 15);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, movesAway), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, movesOver), Ct);

        await ChangeAsync(movesAway.Id, b => (b.CheckInDate, b.CheckOutDate) = (HostScopeScenario.Day(2026, 10, 20), HostScopeScenario.Day(2026, 10, 22)));
        await ChangeAsync(movesOver.Id, b => (b.CheckInDate, b.CheckOutDate) = (HostScopeScenario.Day(2026, 10, 11), HostScopeScenario.Day(2026, 10, 13)));
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, movesAway), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, movesOver), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, movesOver), Ct);

        Assert.Equal(InternalActivity.ClosedBySystem, Assert.Single(await TodosAsync(movesAway.Id)).ClosedBy);

        var items = await TodosAsync(movesOver.Id);
        Assert.Equal(2, items.Count);
        var old = Assert.Single(items, i => i.CompletedAt != null);
        Assert.Equal(InternalActivity.ClosedBySystem, old.ClosedBy);
        Assert.Equal("Casa Bianca: la prenotazione arriva su notti già occupate, dal 12/10/2026 al 13/10/2026", old.Reason);
        var current = Assert.Single(items, i => i.CompletedAt == null);
        Assert.Equal("Casa Bianca: la prenotazione arriva su notti già occupate, dal 11/10/2026 al 13/10/2026", current.Reason);
        Assert.Equal(RomeCalendar.StartOfDayUtc(HostScopeScenario.Day(2026, 10, 11)), current.Deadline);

        Assert.Single(await AlertsAsync(movesOver.Id));
    }

    [Fact]
    public async Task Overlap_AnItemTheHostClosedByHand_IsNotRaisedAgainForTheSameNights_ButIsForOtherNights()
    {
        var booking = await ChannelBookingAsync(_world.Granted, 12, 15);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking), Ct);
        await Activities().CloseAsync(Assert.Single(await TodosAsync(booking.Id)).Id, _world.OwnerUserId, Ct);

        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, booking), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingConfirmed, booking), Ct);

        Assert.Single(await TodosAsync(booking.Id));
        Assert.Single(await AlertsAsync(booking.Id));

        await ChangeAsync(booking.Id, b => b.CheckInDate = HostScopeScenario.Day(2026, 10, 11));
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, booking), Ct);

        var items = await TodosAsync(booking.Id);
        Assert.Equal(2, items.Count);
        Assert.Single(items, i => i.CompletedAt == null && i.Reason!.Contains("11/10/2026", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overlap_TheItemOfDatesTheChannelManagerRefused_IsNotClosedBecauseCasaZenSeesNoOverlap_UntilTheBookingIsCancelled()
    {
        var stay = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        await Activities().CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.BookingOverlap,
                Action = ActivityAction.ReviewBooking,
                EntityType = InternalActivity.BookingEntityType,
                EntityId = stay.Id,
                Urgency = ActivityUrgency.High,
                PropertyId = _world.Granted.Id,
                Reason = EmailTexts.Get("TodoReason_OctorateDatesTaken", EmailTemplates.DefaultCulture),
            },
            Ct);
        _db.OctorateBookingSyncs.Add(new OctorateBookingSync
        {
            OrgId = _world.OrgId,
            BookingId = stay.Id,
            PropertyId = _world.Granted.Id,
            State = OctorateSyncState.Conflict,
            ErrorCode = "octorate_dates_unavailable",
        });
        await _db.SaveChangesAsync();

        var other = await ChannelBookingAsync(_world.Granted, 20, 22);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, other, BookingEventOrigin.Host), Ct);
        Assert.Null(Assert.Single(await TodosAsync(stay.Id)).CompletedAt);

        await ChangeAsync(stay.Id, b => b.Status = BookingStatus.Cancelled);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCancelled, stay, BookingEventOrigin.Host), Ct);
        Assert.Equal(InternalActivity.ClosedBySystem, Assert.Single(await TodosAsync(stay.Id)).ClosedBy);
    }

    private InternalActivityService Activities() => new(_db, NullLogger<InternalActivityService>.Instance, _clock);

    private BookingOverlapHandler Handler() =>
        new(
            _db,
            Activities(),
            new AlertRouter(_db, new NoOpPopupAlertPublisher(), _clock, NullLogger<AlertRouter>.Instance),
            new ConfigurationBuilder().Build(),
            _clock,
            NullLogger<BookingOverlapHandler>.Instance);

    private async Task<List<InternalActivity>> TodosAsync(Guid bookingId)
    {
        _db.ChangeTracker.Clear();
        return await _db.InternalActivities.AsNoTracking().Where(a => a.Action != null && a.EntityId == bookingId).ToListAsync();
    }

    private async Task<List<PopupAlert>> AlertsAsync(Guid bookingId)
    {
        _db.ChangeTracker.Clear();
        return await _db.PopupAlerts.AsNoTracking().Where(a => a.EntityId == bookingId).ToListAsync();
    }

    private async Task<Booking> StayAsync(Property property, BookingStatus status)
    {
        _db.ChangeTracker.Clear();
        return await _db.Bookings.AsNoTracking()
            .SingleAsync(b => b.PropertyId == property.Id && b.Source == BookingSource.Manual && b.Status == status);
    }

    private async Task<Booking> ChannelBookingAsync(Property property, int fromDay, int toDay, BookingStatus status = BookingStatus.Confirmed)
    {
        var guest = new Guest
        {
            OrgId = _world.OrgId,
            FirstName = "Giulietta",
            LastName = "Capuleti",
            Email = "giulietta.capuleti@example.com",
            PhoneNumber = "+393331112233",
        };
        var booking = new Booking
        {
            OrgId = _world.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = HostScopeScenario.Day(2026, 10, fromDay),
            CheckOutDate = HostScopeScenario.Day(2026, 10, toDay),
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Airbnb,
            BasePrice = 200m,
            TotalPrice = 200m,
            SpecialRequests = "Allergica ai gatti, arriva con un cane",
        };
        _db.Guests.Add(guest);
        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return booking;
    }

    private async Task ChangeAsync(Guid bookingId, Action<Booking> change)
    {
        _db.ChangeTracker.Clear();
        change(await _db.Bookings.SingleAsync(b => b.Id == bookingId));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task ChangePropertyAsync(Guid propertyId, Action<Property> change)
    {
        _db.ChangeTracker.Clear();
        change(await _db.Properties.SingleAsync(p => p.Id == propertyId));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<DomainEvent> BookedAsync(DomainEventType type, Booking booking, BookingEventOrigin origin = BookingEventOrigin.Octorate)
    {
        _db.ChangeTracker.Clear();
        var current = await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id);
        return new DomainEvent(
            Guid.NewGuid(),
            _world.OrgId,
            type,
            current.Id,
            HostScopeScenario.Now.UtcDateTime,
            JsonSerializer.Serialize(BookingEventPayload.Of(current, origin), DomainEventRecorder.PayloadJsonOptions));
    }
}
