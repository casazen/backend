using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E08/F01/US02/T02 (NF-80) on a real PostgreSQL database, what EF InMemory cannot prove (it has no transaction to roll back). When the nights of a
/// booking move, <see cref="BookingOverlapHandler"/> closes its item and raises another one that says the new nights, in one transaction: if the
/// second cannot be written the first is still open, so the delivery that is made again finds it and replaces it. Without the transaction the item
/// would stay closed, and a delivery of the event of another booking (which only looks at the open items) would never bring it back.
/// </summary>
public class BookingOverlapReplacementPostgresTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private PostgresTestDatabase? _database;
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateMigratedAsync("overlap");
        await using var db = _database.CreateContext();
        _world = await HostScopeScenario.SeedAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task BookingOverlapItem_AReplacementThatCannotBeWritten_LeavesTheOldItemOpen_AndTheNextDeliveryReplacesIt()
    {
        // A booking of the channel manager on Oct 12-15 of a property whose stay of Oct 10-13 takes the night of Oct 12, with the item its arrival raised.
        Guid bookingId;
        await using (var db = _database!.CreateContext())
        {
            bookingId = await AddChannelBookingAsync(db, 12, 15);
            await Activities(db).CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.BookingOverlap,
                    Action = ActivityAction.ReviewBooking,
                    EntityType = InternalActivity.BookingEntityType,
                    EntityId = bookingId,
                    Urgency = ActivityUrgency.High,
                    PropertyId = _world.Granted.Id,
                    Deadline = RomeCalendar.StartOfDayUtc(HostScopeScenario.Day(2026, 10, 12)),
                    Reason = $"{_world.Granted.Name}: la prenotazione arriva su notti già occupate, dal 12/10/2026 al 13/10/2026",
                },
                Ct);

            // The channel manager moves it to Oct 11-14: the stay shares two nights of it now, the item says the old ones.
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId, Ct);
            booking.CheckInDate = HostScopeScenario.Day(2026, 10, 11);
            booking.CheckOutDate = HostScopeScenario.Day(2026, 10, 14);
            await db.SaveChangesAsync(Ct);
        }

        // The first delivery: the item is closed, then the database cannot write the new one.
        await using (var db = _database.CreateContext())
        {
            var real = Activities(db);
            var failing = new Mock<IInternalActivityService>();
            failing
                .Setup(a => a.CloseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((Guid id, string closedBy, CancellationToken token) => real.CloseAsync(id, closedBy, token));
            failing
                .Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("The database went away."));

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await Handler(db, failing.Object).HandleAsync(await ModifiedAsync(db, bookingId), Ct));
        }

        // Nothing of the failed replacement was kept: the old item is open, as it was, and it is the only one.
        var afterTheFailure = await ItemsAsync(bookingId);
        var stillOpen = Assert.Single(afterTheFailure);
        Assert.Null(stillOpen.CompletedAt);
        Assert.Null(stillOpen.ClosedBy);
        Assert.EndsWith("dal 12/10/2026 al 13/10/2026", stillOpen.Reason, StringComparison.Ordinal);

        // The delivery made again: the old item is closed by the system and the new one says the new nights.
        await using (var db = _database.CreateContext())
            await Handler(db, Activities(db)).HandleAsync(await ModifiedAsync(db, bookingId), Ct);

        var afterTheRetry = await ItemsAsync(bookingId);
        Assert.Equal(2, afterTheRetry.Count);
        var closed = Assert.Single(afterTheRetry, item => item.CompletedAt != null);
        Assert.Equal(InternalActivity.ClosedBySystem, closed.ClosedBy);
        Assert.EndsWith("dal 12/10/2026 al 13/10/2026", closed.Reason, StringComparison.Ordinal);
        var open = Assert.Single(afterTheRetry, item => item.CompletedAt == null);
        Assert.EndsWith("dal 11/10/2026 al 13/10/2026", open.Reason, StringComparison.Ordinal);
        Assert.Equal(RomeCalendar.StartOfDayUtc(HostScopeScenario.Day(2026, 10, 11)), open.Deadline);
    }

    private async Task<List<InternalActivity>> ItemsAsync(Guid bookingId)
    {
        await using var db = _database!.CreateContext();
        return await db.InternalActivities.AsNoTracking().Where(a => a.Action != null && a.EntityId == bookingId).ToListAsync(Ct);
    }

    private async Task<Guid> AddChannelBookingAsync(AppDbContext db, int fromDay, int toDay)
    {
        var guest = new Guest { OrgId = _world.OrgId, FirstName = "Giulietta", LastName = "Capuleti", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Granted.Id,
            GuestId = guest.Id,
            CheckInDate = HostScopeScenario.Day(2026, 10, fromDay),
            CheckOutDate = HostScopeScenario.Day(2026, 10, toDay),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Airbnb,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(Ct);
        return booking.Id;
    }

    private static InternalActivityService Activities(AppDbContext db) => new(db, NullLogger<InternalActivityService>.Instance, Clock);

    private static BookingOverlapHandler Handler(AppDbContext db, IInternalActivityService activities) =>
        new(
            db,
            activities,
            new AlertRouter(db, new NoOpPopupAlertPublisher(), Clock, NullLogger<AlertRouter>.Instance),
            new ConfigurationBuilder().Build(),
            Clock,
            NullLogger<BookingOverlapHandler>.Instance);

    /// <summary>The event of a booking changed by the channel manager, as the services record it: the payload of <see cref="BookingEventPayload"/>.</summary>
    private async Task<DomainEvent> ModifiedAsync(AppDbContext db, Guid bookingId)
    {
        var current = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId, Ct);
        return new DomainEvent(
            Guid.NewGuid(),
            _world.OrgId,
            DomainEventType.BookingModified,
            current.Id,
            HostScopeScenario.Now.UtcDateTime,
            JsonSerializer.Serialize(BookingEventPayload.Of(current, BookingEventOrigin.Octorate), DomainEventRecorder.PayloadJsonOptions));
    }
}
