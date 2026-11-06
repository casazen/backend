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

/// <summary>
/// E08/F01/US02/T02 (NF-80, AC-NF80-2, ADR-025, ADR-026-R3): a booking that arrives from the channel manager on nights that are already taken
/// raises one urgent «To do» and one alert, once; the item says the property and the nights and nothing of the guest; it closes by itself when the
/// nights are free again. On EF InMemory, on the world of <see cref="HostScopeScenario"/>: per property a confirmed stay (Oct 10-13), a stay checked
/// out (Oct 1-4) and a request waiting for the host (Nov 20-23); the clock is Oct 9, 12:00 in Rome.
/// </summary>
public class BookingOverlapHandlerTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // --- Raising the item and the alert --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Overlap_ABookingFromTheChannelManagerOnTakenNights_RaisesOneUrgentItemAndOneAlert_OnceWhateverTheEvents()
    {
        // Oct 12-15 on a property whose stay of Oct 10-13 takes the night of Oct 12.
        var booking = await ChannelBookingAsync(_world.Granted, 12, 15);

        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingConfirmed, booking), Ct);
        // Delivered again, and as another event of the same booking: still one item and one alert (ADR-022-R7).
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

        // The person in charge of nothing: the owner of the org is the one told.
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
        // Oct 13-16 on the other property, whose stay leaves on Oct 13 (a free arrival), and a block of the host on Oct 14-17.
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
        // The check-out day of the stay of Oct 10-13 is a free arrival: no night is shared.
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

        // The booking exists, but not in the org the event names (ADR-022-R6): as for a booking that does not exist.
        var otherOrg = (await BookedAsync(DomainEventType.BookingCreated, overlapping)) with { OrgId = _world.OtherOrgId };
        await Handler().HandleAsync(otherOrg, Ct);
        await Handler().HandleAsync(otherOrg with { EntityId = Guid.NewGuid() }, Ct);

        // A payload that is not JSON, one that is not an object, one without an origin, and events of another kind.
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
        // Oct 14-17 is free on both properties. On the first the sync of the calendar of the channel manager closed those nights before the
        // booking was imported (a block of source Octorate, the echo of the reservation); on the other the host closed the same nights by hand.
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

        // The echo is nobody else taking the nights: no item and no alert, so nothing is left open when the next sync takes the block away.
        Assert.Empty(await TodosAsync(withTheEcho.Id));
        Assert.Empty(await AlertsAsync(withTheEcho.Id));

        // The block of the host is another matter: one item and its alert.
        Assert.Null(Assert.Single(await TodosAsync(withTheHostBlock.Id)).CompletedAt);
        Assert.NotEmpty(await AlertsAsync(withTheHostBlock.Id));
    }

    // --- Proofs ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Proof (privacy; ADR-026-R3): the reason of the item, and the alert, hold the property and the nights and nothing of the guest of the
    /// booking or of the booking it overlaps (name, e-mail, phone, the request they wrote), in Italian and in English. The dates are formatted
    /// from real values: <c>DateOnly</c> takes the formats of a date only, and a format with a time would throw at run time.
    /// </summary>
    [Fact]
    public async Task BookingOverlapActivity_NoGuestData_Test()
    {
        var booking = await ChannelBookingAsync(_world.Granted, 12, 15);

        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking), Ct);

        var item = Assert.Single(await TodosAsync(booking.Id));
        var alert = Assert.Single(await AlertsAsync(booking.Id));
        // The guest of the booking, the guest of the stay it overlaps (Anna) and the request they wrote.
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

    /// <summary>
    /// Proof (permission; ADR-025): the alert goes to the people who reach the property, as every host notification does
    /// (<c>HostNotificationAudience</c>): the administrators of the org and the member in charge while they still reach it. A collaborator
    /// «Solo alcuni» who was put in charge of a property and lost it is not told, whatever the property.
    /// </summary>
    [Fact]
    public async Task BookingOverlapAlert_GoesOnlyToWhoReachesTheProperty_Test()
    {
        // The collaborator was given the first property only, and both name them as the person in charge.
        await ChangePropertyAsync(_world.Granted.Id, p => p.ResponsibleUserId = _world.CollaboratorId);
        await ChangePropertyAsync(_world.Hidden.Id, p => p.ResponsibleUserId = _world.CollaboratorId);
        var onGranted = await ChannelBookingAsync(_world.Granted, 12, 15);
        var onHidden = await ChannelBookingAsync(_world.Hidden, 12, 15);

        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, onGranted), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, onHidden), Ct);

        var reachingGranted = (await AlertsAsync(onGranted.Id)).Select(a => a.UserId).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { _world.OwnerUserId, _world.CollaboratorId }.Order(StringComparer.Ordinal).ToList(), reachingGranted);

        // The one they do not reach: the owner of the org only. The item is raised all the same.
        var reachingHidden = Assert.Single(await AlertsAsync(onHidden.Id));
        Assert.Equal(_world.OwnerUserId, reachingHidden.UserId);
        Assert.Single(await TodosAsync(onHidden.Id));
    }

    // --- Closing and replacing the item -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Overlap_TheItemClosesWhenTheOtherBookingIsCancelled_AndItsOwnCancellationClosesIt_AndNothingBringsItBack()
    {
        var booking = await ChannelBookingAsync(_world.Granted, 12, 15);
        var other = await StayAsync(_world.Granted, BookingStatus.Confirmed);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking), Ct);
        Assert.Null(Assert.Single(await TodosAsync(booking.Id)).CompletedAt);

        // The stay it overlaps is cancelled, by the host in CasaZen: the nights are free.
        await ChangeAsync(other.Id, b => b.Status = BookingStatus.Cancelled);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCancelled, other, BookingEventOrigin.Host), Ct);

        var closed = Assert.Single(await TodosAsync(booking.Id));
        Assert.Equal(InternalActivity.StatusCompleted, closed.Status);
        Assert.Equal(InternalActivity.ClosedBySystem, closed.ClosedBy);
        Assert.NotNull(closed.CompletedAt);

        // The same event delivered again, and the booking confirmed again: the nights are free, so nothing is raised.
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCancelled, other, BookingEventOrigin.Host), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingConfirmed, booking), Ct);
        Assert.Single(await TodosAsync(booking.Id));

        // A booking that conflicts and is itself cancelled: its item closes with it.
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

        // Changed on the channel: the first to Oct 20-22 (free), the second to Oct 11-13 (the stay of Oct 10-13 shares two nights of it).
        await ChangeAsync(movesAway.Id, b => (b.CheckInDate, b.CheckOutDate) = (HostScopeScenario.Day(2026, 10, 20), HostScopeScenario.Day(2026, 10, 22)));
        await ChangeAsync(movesOver.Id, b => (b.CheckInDate, b.CheckOutDate) = (HostScopeScenario.Day(2026, 10, 11), HostScopeScenario.Day(2026, 10, 13)));
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, movesAway), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, movesOver), Ct);
        // Delivered again: nothing changes.
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

        // The host was told once about the booking, and is not told again for the new nights.
        Assert.Single(await AlertsAsync(movesOver.Id));
    }

    [Fact]
    public async Task Overlap_AnItemTheHostClosedByHand_IsNotRaisedAgainForTheSameNights_ButIsForOtherNights()
    {
        var booking = await ChannelBookingAsync(_world.Granted, 12, 15);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCreated, booking), Ct);
        await Activities().CloseAsync(Assert.Single(await TodosAsync(booking.Id)).Id, _world.OwnerUserId, Ct);

        // A change of the price on the channel: the same nights, the host has seen them.
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, booking), Ct);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingConfirmed, booking), Ct);

        Assert.Single(await TodosAsync(booking.Id));
        Assert.Single(await AlertsAsync(booking.Id));

        // Other nights are another item.
        await ChangeAsync(booking.Id, b => b.CheckInDate = HostScopeScenario.Day(2026, 10, 11));
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, booking), Ct);

        var items = await TodosAsync(booking.Id);
        Assert.Equal(2, items.Count);
        Assert.Single(items, i => i.CompletedAt == null && i.Reason!.Contains("11/10/2026", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overlap_TheItemOfDatesTheChannelManagerRefused_IsNotClosedBecauseCasaZenSeesNoOverlap_UntilTheBookingIsCancelled()
    {
        // A booking of CasaZen whose create on the channel manager found the dates taken there (E08/F01/US01/T03): urgent item, sync in Conflict.
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

        // Another booking of the property changes: the item is looked at again and left alone.
        var other = await ChannelBookingAsync(_world.Granted, 20, 22);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingModified, other, BookingEventOrigin.Host), Ct);
        Assert.Null(Assert.Single(await TodosAsync(stay.Id)).CompletedAt);

        // The booking itself is cancelled: there is nothing left to review.
        await ChangeAsync(stay.Id, b => b.Status = BookingStatus.Cancelled);
        await Handler().HandleAsync(await BookedAsync(DomainEventType.BookingCancelled, stay, BookingEventOrigin.Host), Ct);
        Assert.Equal(InternalActivity.ClosedBySystem, Assert.Single(await TodosAsync(stay.Id)).ClosedBy);
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------------------

    private InternalActivityService Activities() => new(_db, NullLogger<InternalActivityService>.Instance, _clock);

    private BookingOverlapHandler Handler() =>
        new(
            _db,
            Activities(),
            new AlertRouter(_db, new NoOpPopupAlertPublisher(), _clock, NullLogger<AlertRouter>.Instance),
            new ConfigurationBuilder().Build(),
            _clock,
            NullLogger<BookingOverlapHandler>.Instance);

    /// <summary>The To do items about the booking, closed ones too.</summary>
    private async Task<List<InternalActivity>> TodosAsync(Guid bookingId)
    {
        _db.ChangeTracker.Clear();
        return await _db.InternalActivities.AsNoTracking().Where(a => a.Action != null && a.EntityId == bookingId).ToListAsync();
    }

    /// <summary>The popup alerts about the booking, for whoever they are.</summary>
    private async Task<List<PopupAlert>> AlertsAsync(Guid bookingId)
    {
        _db.ChangeTracker.Clear();
        return await _db.PopupAlerts.AsNoTracking().Where(a => a.EntityId == bookingId).ToListAsync();
    }

    /// <summary>The scenario's stay of the property with that status, entered by the host (Oct 10-13 confirmed, Oct 1-4 checked out).</summary>
    private async Task<Booking> StayAsync(Property property, BookingStatus status)
    {
        _db.ChangeTracker.Clear();
        return await _db.Bookings.AsNoTracking()
            .SingleAsync(b => b.PropertyId == property.Id && b.Source == BookingSource.Manual && b.Status == status);
    }

    /// <summary>A booking of a channel on the property, in October 2026, with a guest who has a name, an e-mail, a phone and a request.</summary>
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

    /// <summary>Changes the booking and saves it; the context starts again clean.</summary>
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

    /// <summary>The event of a booking as the services record it: the payload of <see cref="BookingEventPayload"/>, as the booking is now.</summary>
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
