using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Events;

/// <summary>
/// E08/F06/US01/T01 (NF-79, ADR-022-R1, R8, R21): the lifecycle events of a booking are recorded in the same unit of work as the
/// change, with a payload of ids, enums and dates only, once. On EF InMemory; the transaction itself is proved on PostgreSQL by
/// <see cref="OutboxEventRecorderTests"/>.
/// </summary>
public class BookingDomainEventsTests
{
    // 2026-10-10 in Rome: the day the stay of the scenario starts (HostScopeScenario: the confirmed stay of Oct 10-13).
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrgA = Guid.Parse("0f6c1a52-3b7e-4c11-9d2a-5e8b7c6d4a01");
    private static readonly Guid PropertyA = Guid.Parse("5b1f0c3e-7d2a-4e68-8a19-3c4d5e6f7a80");
    private static readonly Guid BookingA = Guid.Parse("c3a9e8d7-1b2c-4d5e-9f60-718293a4b5c6");

    // --- The payload: ids, enums and dates only (privacy rule) ---------------------------------------------------------------

    /// <summary>
    /// Proof (privacy; ADR-022-R21, AC-E08-F06-US01-02): the events of a booking made of a guest, a special request, notes and prices
    /// carry the ids, the source, the origin, the status and the dates, and nothing else: no guest field, no note, no amount.
    /// </summary>
    [Fact]
    public async Task BookingEventPayload_HasNoPersonalData_Test()
    {
        var database = NewDatabase();
        await using var db = NewDb(database);
        var guest = new Guest
        {
            OrgId = OrgA,
            FirstName = "Giulietta",
            LastName = "Capuleti",
            Email = "giulietta.capuleti@example.com",
            PhoneNumber = "+393331112233",
        };
        var booking = NewBooking();
        booking.GuestId = guest.Id;
        booking.Guest = guest;
        booking.SpecialRequests = "Allergica ai gatti, arriva con un cane";
        booking.CancellationNote = "Nota riservata dell'host";

        BookingEvents.RecordCreated(Recorder(db), booking, BookingEventOrigin.Host);
        await db.SaveChangesAsync();

        await using var read = NewDb(database);
        var rows = await read.OutboxEvents.OrderBy(e => e.EventType).ToListAsync();
        Assert.Equal([DomainEventType.BookingCreated, DomainEventType.BookingConfirmed], rows.Select(r => r.EventType));
        foreach (var row in rows)
        {
            Assert.Equal(BookingA, row.EntityId);
            Assert.Equal(OrgA, row.OrgId);

            using var json = JsonDocument.Parse(row.PayloadJson);
            var names = json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(["BookingId", "CheckIn", "CheckOut", "Origin", "PropertyId", "Source", "Status"], names);
            Assert.Equal("Host", json.RootElement.GetProperty("Origin").GetString());
            Assert.Equal("Manual", json.RootElement.GetProperty("Source").GetString());
            Assert.Equal("Confirmed", json.RootElement.GetProperty("Status").GetString());
            Assert.Equal("2026-11-10", json.RootElement.GetProperty("CheckIn").GetString());
            Assert.Equal("2026-11-13", json.RootElement.GetProperty("CheckOut").GetString());

            foreach (var forbidden in new[]
                     {
                         "Giulietta", "Capuleti", "giulietta.capuleti", "+39333", "Allergica", "gatti", "Nota riservata", "987.65", "876.54",
                     })
            {
                Assert.DoesNotContain(forbidden, row.PayloadJson, StringComparison.OrdinalIgnoreCase);
            }
        }

        // By construction too: every member of the payload is an id, an enum or a date.
        var members = typeof(BookingEventPayload).GetConstructors().Single().GetParameters();
        Assert.All(members, p => Assert.True(
            p.ParameterType == typeof(Guid) || p.ParameterType == typeof(DateOnly) || p.ParameterType.IsEnum,
            $"{p.Name} is a {p.ParameterType.Name}"));
    }

    [Fact]
    public void BookingEventPayload_RoundTripsThroughTheOutboxJson()
    {
        var payload = BookingEventPayload.Of(NewBooking(BookingStatus.Pending), BookingEventOrigin.Octorate);

        var json = JsonSerializer.Serialize(payload, DomainEventRecorder.PayloadJsonOptions);
        var back = JsonSerializer.Deserialize<BookingEventPayload>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(payload, back);
        Assert.Contains("\"Origin\":\"Octorate\"", json, StringComparison.Ordinal);
    }

    // --- The events and their keys ------------------------------------------------------------------------------------------

    [Fact]
    public void DedupKeyOf_IsTheKeyOfTheBrief_ForEachEvent()
    {
        var booking = NewBooking();

        Assert.Equal($"booking-created:{BookingA}", BookingEvents.DedupKeyOf(DomainEventType.BookingCreated, booking));
        Assert.Equal($"booking-confirmed:{BookingA}", BookingEvents.DedupKeyOf(DomainEventType.BookingConfirmed, booking));
        Assert.Equal($"booking-cancelled:{BookingA}", BookingEvents.DedupKeyOf(DomainEventType.BookingCancelled, booking));
        Assert.Equal($"booking-modified:{BookingA}:2026-10-10T09:00:00.0000000Z", BookingEvents.DedupKeyOf(DomainEventType.BookingModified, booking));
        Assert.Equal($"checkin:{BookingA}", BookingEvents.DedupKeyOf(DomainEventType.CheckInCompleted, booking));
        Assert.Equal($"checkout:{BookingA}", BookingEvents.DedupKeyOf(DomainEventType.CheckOutCompleted, booking));
        Assert.Equal($"property-ready:{BookingA}", BookingEvents.DedupKeyOf(DomainEventType.PropertyReadyDeclared, booking));
        Assert.Throws<ArgumentOutOfRangeException>(() => BookingEvents.DedupKeyOf(DomainEventType.TimeTrigger, booking));
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed, 2)]
    [InlineData(BookingStatus.Pending, 1)]
    public async Task RecordCreated_RecordsTheCreationAndTheConfirmationOfABookingMadeConfirmed(BookingStatus status, int events)
    {
        var database = NewDatabase();
        await using var db = NewDb(database);

        BookingEvents.RecordCreated(Recorder(db), NewBooking(status), BookingEventOrigin.Guest);
        await db.SaveChangesAsync();

        await using var read = NewDb(database);
        var rows = await read.OutboxEvents.ToListAsync();
        Assert.Equal(events, rows.Count);
        Assert.Contains(rows, r => r.EventType == DomainEventType.BookingCreated && r.DedupKey == $"booking-created:{BookingA}");
        Assert.Equal(status == BookingStatus.Confirmed, rows.Any(r => r.EventType == DomainEventType.BookingConfirmed));
    }

    [Fact]
    public async Task RecordAsync_AReplayIsNotRecordedAgain_AnEditAtAnotherInstantIs()
    {
        var database = NewDatabase();
        await using var db = NewDb(database);
        var booking = NewBooking();

        // The same edit saved twice (a repeated save, a retried request): one event. Another edit, another instant: another event.
        await BookingEvents.RecordAsync(Recorder(db), db, DomainEventType.BookingModified, booking, BookingEventOrigin.Host);
        await db.SaveChangesAsync();
        await BookingEvents.RecordAsync(Recorder(db), db, DomainEventType.BookingModified, booking, BookingEventOrigin.Host);
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.OutboxEvents.CountAsync());

        booking.UpdatedAt = booking.UpdatedAt.AddMinutes(5);
        await BookingEvents.RecordAsync(Recorder(db), db, DomainEventType.BookingModified, booking, BookingEventOrigin.Host);
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.OutboxEvents.CountAsync());

        // The check-in of a stay is one event, however many times it is asked for.
        await BookingEvents.RecordAsync(Recorder(db), db, DomainEventType.CheckInCompleted, booking, BookingEventOrigin.Host);
        await BookingEvents.RecordAsync(Recorder(db), db, DomainEventType.CheckInCompleted, booking, BookingEventOrigin.Host);
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.OutboxEvents.CountAsync(e => e.EventType == DomainEventType.CheckInCompleted));
    }

    [Fact]
    public async Task RecordAsync_AConfirmationAndACancellationThatComeBack_AreNewEvents_NeverAFailure()
    {
        var database = NewDatabase();
        await using var db = NewDb(database);
        var booking = NewBooking();

        // Confirmed, cancelled, confirmed again by a late payment, cancelled again: the keys of the first time are taken, and the second
        // time is recorded with the instant of the change instead of failing the flow with a unique violation of the outbox.
        foreach (var type in new[] { DomainEventType.BookingConfirmed, DomainEventType.BookingCancelled })
        {
            await BookingEvents.RecordAsync(Recorder(db), db, type, booking, BookingEventOrigin.Guest);
            await db.SaveChangesAsync();
        }

        booking.UpdatedAt = booking.UpdatedAt.AddDays(2);
        foreach (var type in new[] { DomainEventType.BookingConfirmed, DomainEventType.BookingCancelled })
        {
            await BookingEvents.RecordAsync(Recorder(db), db, type, booking, BookingEventOrigin.Host);
            await db.SaveChangesAsync();
            // The same change again (a replay of it) is not a third one.
            await BookingEvents.RecordAsync(Recorder(db), db, type, booking, BookingEventOrigin.Host);
            await db.SaveChangesAsync();
        }

        var keys = await db.OutboxEvents.Select(e => e.DedupKey!).ToListAsync();
        Assert.Equal(4, keys.Count);
        Assert.Equal(4, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains($"booking-confirmed:{BookingA}", keys);
        Assert.Contains($"booking-cancelled:{BookingA}:2026-10-12T09:00:00.0000000Z", keys);
    }

    [Fact]
    public async Task Record_WithoutARecorder_RecordsNothingAndFailsNothing()
    {
        var database = NewDatabase();
        await using var db = NewDb(database);

        BookingEvents.RecordCreated(null, NewBooking(), BookingEventOrigin.Host);
        await BookingEvents.RecordAsync(null, db, DomainEventType.BookingCancelled, NewBooking(), BookingEventOrigin.Host);
        await db.SaveChangesAsync();

        Assert.Empty(await db.OutboxEvents.ToListAsync());
    }

    [Fact]
    public async Task Withdraw_TakesBackTheUnsavedEventsOfThatBookingOnly()
    {
        var database = NewDatabase();
        await using var db = NewDb(database);
        var other = NewBooking();
        other.Id = Guid.Parse("11111111-2222-4333-8444-555555555555");

        BookingEvents.RecordCreated(Recorder(db), NewBooking(), BookingEventOrigin.Host);
        BookingEvents.RecordCreated(Recorder(db), other, BookingEventOrigin.Host);
        BookingEvents.Withdraw(db, BookingA);
        await db.SaveChangesAsync();

        var kept = await db.OutboxEvents.ToListAsync();
        Assert.Equal(2, kept.Count);
        Assert.All(kept, e => Assert.Equal(other.Id, e.EntityId));
    }

    // --- The event is saved with the change, never after it (data rule, ADR-022-R1) -------------------------------------------

    /// <summary>
    /// Proof (data; ADR-022-R1, AC-E08-F06-US01-01): a check-in whose save is refused leaves neither the change of the booking nor its
    /// event; the same request sent again records the event once, a third time (the stay is checked in) records nothing; and the
    /// change and its event are one save (a second save would be refused here, and the check-in still stands).
    /// </summary>
    [Fact]
    public async Task BookingEvents_RolledBackChange_LeavesNoEvent_Test()
    {
        // One context for the whole test (the interceptor is part of it); between two requests its tracker is cleared, as a new request
        // starts with a new context.
        var interceptor = new FailingSaveInterceptor();
        await using var db = NewDb(NewDatabase(), interceptor);
        var world = await HostScopeScenario.SeedAsync(db);
        var bookingId = await db.Bookings.AsNoTracking()
            .Where(b => b.PropertyId == world.Granted.Id && b.Status == BookingStatus.Confirmed)
            .Select(b => b.Id)
            .SingleAsync();

        // 1. The database refuses the save: the booking is as it was and no event stays.
        interceptor.Failure = new DbUpdateException("the save is refused");
        await Assert.ThrowsAsync<DbUpdateException>(() => Stay(db).RegisterArrivalAsync(bookingId));
        interceptor.Failure = null;
        db.ChangeTracker.Clear();

        Assert.Equal(BookingStatus.Confirmed, (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId)).Status);
        Assert.Empty(await db.OutboxEvents.ToListAsync());

        // 2. The request is sent again: the change and its event are saved by one save (a second save is refused, and nothing needs it).
        interceptor.AfterSave = () => interceptor.Failure = new DbUpdateException("a second save");
        await Stay(db).RegisterArrivalAsync(bookingId);
        interceptor.AfterSave = null;
        interceptor.Failure = null;
        db.ChangeTracker.Clear();

        Assert.Equal(BookingStatus.CheckedIn, (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId)).Status);
        var row = Assert.Single(await db.OutboxEvents.ToListAsync());
        Assert.Equal(DomainEventType.CheckInCompleted, row.EventType);
        Assert.Equal(bookingId, row.EntityId);
        Assert.Equal($"checkin:{bookingId}", row.DedupKey);
        using (var json = JsonDocument.Parse(row.PayloadJson))
        {
            Assert.Equal("Host", json.RootElement.GetProperty("Origin").GetString());
            Assert.Equal("CheckedIn", json.RootElement.GetProperty("Status").GetString());
        }

        // 3. A third request finds the stay checked in: refused by the rule, and no second event.
        await Assert.ThrowsAsync<DomainConflictException>(() => Stay(db).RegisterArrivalAsync(bookingId));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task CheckOut_RecordsTheCheckInOfTheArrivalItRegistersAndTheCheckOut()
    {
        var (database, bookingId) = await SeedStayAsync();

        await using (var db = NewDb(database))
            await Stay(db).CheckOutAsync(bookingId, new StayCheckOut(RegisterArrival: true));

        await using var read = NewDb(database);
        var rows = await read.OutboxEvents.OrderBy(e => e.EventType).ToListAsync();
        Assert.Equal([DomainEventType.CheckInCompleted, DomainEventType.CheckOutCompleted], rows.Select(r => r.EventType));
        Assert.Equal([$"checkin:{bookingId}", $"checkout:{bookingId}"], rows.Select(r => r.DedupKey));
        using var checkedOut = JsonDocument.Parse(rows[1].PayloadJson);
        Assert.Equal("CheckedOut", checkedOut.RootElement.GetProperty("Status").GetString());
        Assert.All(rows, r => Assert.Equal(OrgOf(read, bookingId), r.OrgId));
    }

    [Fact]
    public async Task ConfirmPropertyReady_RecordsTheDeclarationOnce()
    {
        var database = NewDatabase();
        Guid closedBookingId;
        await using (var seed = NewDb(database))
        {
            var world = await HostScopeScenario.SeedAsync(seed);
            closedBookingId = await seed.Bookings.AsNoTracking()
                .Where(b => b.PropertyId == world.Granted.Id && b.Status == BookingStatus.CheckedOut)
                .Select(b => b.Id)
                .SingleAsync();
        }

        await using (var first = NewDb(database))
            await Stay(first).ConfirmPropertyReadyAsync(closedBookingId, notes: null);
        await using (var again = NewDb(database))
            await Stay(again).ConfirmPropertyReadyAsync(closedBookingId, notes: null);

        await using var read = NewDb(database);
        var row = Assert.Single(await read.OutboxEvents.ToListAsync());
        Assert.Equal(DomainEventType.PropertyReadyDeclared, row.EventType);
        Assert.Equal(closedBookingId, row.EntityId);
        Assert.Equal($"property-ready:{closedBookingId}", row.DedupKey);
    }

    /// <summary>
    /// A booking the repository refuses (the dates are taken) takes the events recorded for it back: the save that follows in the same
    /// context (the delete of the guest snapshot, say) does not write an event for a booking that does not exist.
    /// </summary>
    [Fact]
    public async Task RefusedBooking_LeavesNoEvent_EvenIfTheContextSavesAgain()
    {
        var database = NewDatabase();
        await using var db = NewDb(database);
        var repository = new BookingRepository(db);
        await repository.AddAsync(NewBooking(BookingStatus.Confirmed, Guid.NewGuid(), new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc)));
        var refused = NewBooking(BookingStatus.Confirmed, Guid.NewGuid(), new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc));

        BookingEvents.RecordCreated(Recorder(db), refused, BookingEventOrigin.Host);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(refused));
        await db.SaveChangesAsync();

        await using var read = NewDb(database);
        Assert.DoesNotContain(await read.OutboxEvents.ToListAsync(), e => e.EntityId == refused.Id);
    }

    /// <summary>A checkout whose payment could not be started is removed (BK-18) with the events of its creation, not published yet.</summary>
    [Fact]
    public async Task DiscardedCheckoutAttempt_TakesItsUnpublishedEventsWithIt()
    {
        var database = NewDatabase();
        await using var db = NewDb(database);
        var repository = new BookingRepository(db);
        var guest = new Guest { OrgId = OrgA, FirstName = "Anna", LastName = "Verdi", Email = "anna@example.com" };
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        var hold = NewBooking(BookingStatus.Pending);
        hold.GuestId = guest.Id;

        BookingEvents.RecordCreated(Recorder(db), hold, BookingEventOrigin.Guest);
        await repository.AddAsync(hold);
        Assert.Equal(1, await db.OutboxEvents.CountAsync(e => e.EntityId == hold.Id));

        await repository.DiscardCheckoutAttemptAsync(hold.Id);

        await using var read = NewDb(database);
        Assert.Empty(await read.OutboxEvents.ToListAsync());
        Assert.Empty(await read.Bookings.ToListAsync());
    }

    /// <summary>
    /// The host cancels a booking: one <c>BookingCancelled</c>, origin Host, saved with the cancellation; a second request is refused by
    /// the rule and records nothing.
    /// </summary>
    [Fact]
    public async Task HostCancellation_RecordsTheCancellationOnce()
    {
        var (database, bookingId) = await SeedStayAsync();

        await using (var db = NewDb(database))
            await Cancellation(db).CancelAsync(new BookingCancellationRequest(bookingId, null, "motivo riservato", "auth0|host"));
        await using (var again = NewDb(database))
        {
            await Assert.ThrowsAsync<DomainConflictException>(() =>
                Cancellation(again).CancelAsync(new BookingCancellationRequest(bookingId, null, null, "auth0|host")));
        }

        await using var read = NewDb(database);
        var row = Assert.Single(await read.OutboxEvents.ToListAsync());
        Assert.Equal(DomainEventType.BookingCancelled, row.EventType);
        Assert.Equal($"booking-cancelled:{bookingId}", row.DedupKey);
        using var json = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal("Host", json.RootElement.GetProperty("Origin").GetString());
        Assert.Equal("Cancelled", json.RootElement.GetProperty("Status").GetString());
        Assert.DoesNotContain("motivo riservato", row.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(BookingStatus.Cancelled, (await read.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId)).Status);
    }

    /// <summary>
    /// A «pay at the property» request that nobody answered in time is cancelled by the expiry job: the cancellation is the system's, in
    /// the save that cancels the booking.
    /// </summary>
    [Fact]
    public async Task HoldExpiry_RecordsTheCancellationOfAnExpiredRequestAsTheSystems()
    {
        var database = NewDatabase();
        await using var db = NewDb(database);
        var world = await HostScopeScenario.SeedAsync(db);
        var requestId = await PendingRequestIdAsync(db, world);
        // The request of the scenario waits for the host until the day after the clock of the scenario; the job runs two days later.
        var clock = new FixedTimeProvider(HostScopeScenario.Now.AddDays(2));
        var expiry = new CheckoutHoldExpiryService(
            db,
            Mock.Of<IStripeService>(),
            new OnSiteRequestNotifier(db, new RecordingEmailQueue(), EmailTestHelpers.Links(), NullLogger<OnSiteRequestNotifier>.Instance),
            new ConfigurationBuilder().Build(),
            NullLogger<CheckoutHoldExpiryService>.Instance,
            clock,
            new DomainEventRecorder(db, NullTenantContext.Instance, clock));

        var run = await expiry.ExpireDueHoldsAsync();

        Assert.True(run.Expired >= 1);
        db.ChangeTracker.Clear();
        Assert.Equal(BookingStatus.Cancelled, (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == requestId)).Status);
        var row = Assert.Single(await db.OutboxEvents.Where(e => e.EntityId == requestId).ToListAsync());
        Assert.Equal(DomainEventType.BookingCancelled, row.EventType);
        Assert.Equal($"booking-cancelled:{requestId}", row.DedupKey);
        Assert.Equal(world.OrgId, row.OrgId);
        using var json = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal("System", json.RootElement.GetProperty("Origin").GetString());
        Assert.Equal("Cancelled", json.RootElement.GetProperty("Status").GetString());
    }

    /// <summary>The host turns a «pay at the property» request down: the cancellation is recorded with it, as the request's own (System).</summary>
    [Fact]
    public async Task DeclinedRequest_RecordsTheCancellationOnceWithoutTheMessageToTheGuest()
    {
        var database = NewDatabase();
        await using var db = NewDb(database);
        var world = await HostScopeScenario.SeedAsync(db);
        var requestId = await PendingRequestIdAsync(db, world);
        var clock = new FixedTimeProvider(HostScopeScenario.Now);
        var service = new OnSiteBookingRequestService(
            db,
            null!,
            new OnSiteRequestNotifier(db, new RecordingEmailQueue(), EmailTestHelpers.Links(), NullLogger<OnSiteRequestNotifier>.Instance),
            null!,
            new ConfigurationBuilder().Build(),
            NullLogger<OnSiteBookingRequestService>.Instance,
            clock,
            new DomainEventRecorder(db, NullTenantContext.Instance, clock));

        await service.DeclineAsync(requestId, "Spiacenti, non siamo disponibili");
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<DomainConflictException>(() => service.DeclineAsync(requestId, null));

        db.ChangeTracker.Clear();
        var row = Assert.Single(await db.OutboxEvents.ToListAsync());
        Assert.Equal(DomainEventType.BookingCancelled, row.EventType);
        Assert.Equal(requestId, row.EntityId);
        Assert.Equal($"booking-cancelled:{requestId}", row.DedupKey);
        Assert.DoesNotContain("Spiacenti", row.PayloadJson, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal("System", json.RootElement.GetProperty("Origin").GetString());
        Assert.Equal(BookingStatus.Cancelled, (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == requestId)).Status);
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------------

    private static Task<Guid> PendingRequestIdAsync(AppDbContext db, HostScopeWorld world) =>
        db.Bookings.AsNoTracking()
            .Where(b => b.PropertyId == world.Granted.Id && b.Status == BookingStatus.Pending)
            .Select(b => b.Id)
            .SingleAsync();


    private static string NewDatabase() => $"booking-events-{Guid.NewGuid():N}";

    private static AppDbContext NewDb(string database, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).AddInterceptors(interceptors).Options);

    private static DomainEventRecorder Recorder(AppDbContext db) =>
        new(db, NullTenantContext.Instance, new FixedTimeProvider(Now));

    private static StayLifecycleService Stay(AppDbContext db) =>
        new(
            db,
            Mock.Of<IAlloggiatiWebService>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IServiceRequestService>(),
            NullLogger<StayLifecycleService>.Instance,
            new FixedTimeProvider(Now),
            Recorder(db));

    private static BookingCancellationService Cancellation(AppDbContext db)
    {
        var stripe = Mock.Of<IStripeService>();
        var emails = Mock.Of<IEmailQueue>();
        var notifier = new BookingNotifier(db, emails, EmailTestHelpers.Links(), Mock.Of<IPushNotificationService>(), NullLogger<BookingNotifier>.Instance);
        var refunds = new PaymentRefundService(
            db, stripe, Mock.Of<IPaymentRefundRetryScheduler>(), emails, NullLogger<PaymentRefundService>.Instance, new FixedTimeProvider(Now));
        return new BookingCancellationService(
            db, refunds, stripe, notifier, NullLogger<BookingCancellationService>.Instance, new FixedTimeProvider(Now), Recorder(db));
    }

    /// <summary>The scenario's confirmed stay of the first property (Oct 10-13), in a database of its own.</summary>
    private static async Task<(string Database, Guid BookingId)> SeedStayAsync()
    {
        var database = NewDatabase();
        await using var seed = NewDb(database);
        var world = await HostScopeScenario.SeedAsync(seed);
        var bookingId = await seed.Bookings.AsNoTracking()
            .Where(b => b.PropertyId == world.Granted.Id && b.Status == BookingStatus.Confirmed)
            .Select(b => b.Id)
            .SingleAsync();
        return (database, bookingId);
    }

    private static Guid OrgOf(AppDbContext db, Guid bookingId) =>
        db.Bookings.AsNoTracking().Where(b => b.Id == bookingId).Select(b => b.OrgId).Single();

    private static Booking NewBooking(BookingStatus status = BookingStatus.Confirmed, Guid? id = null, DateTime? checkIn = null)
    {
        var start = checkIn ?? new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        return new Booking
        {
            Id = id ?? BookingA,
            OrgId = OrgA,
            PropertyId = PropertyA,
            GuestId = Guid.NewGuid(),
            CheckInDate = start,
            CheckOutDate = start.AddDays(3),
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Manual,
            BasePrice = 876.54m,
            TotalPrice = 987.65m,
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime,
        };
    }
}
