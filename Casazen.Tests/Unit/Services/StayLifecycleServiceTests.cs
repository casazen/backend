using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CO-08 (A5-08): arrival and check-out rules of <see cref="StayLifecycleService"/>, with "today" in Europe/Rome and the
/// jobs scheduled around the transitions. The HTTP contract, the locks and TN-3 are covered by
/// <c>StayLifecyclePostgresTests</c>.
/// </summary>
public class StayLifecycleServiceTests
{
    private static readonly DateTime October1 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime October3 = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IAlloggiatiWebService> _alloggiati = new();
    private readonly Mock<IAlloggiatiReportScheduler> _alloggiatiScheduler = new();
    private readonly Mock<IServiceRequestService> _serviceRequests = new();

    public StayLifecycleServiceTests()
    {
        _alloggiati.Setup(a => a.IsStayDataCompleteAsync(It.IsAny<Guid>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task RegisterArrivalAsync_AfterMidnightInRomeOnTheCheckInDay_ChecksInWithTheArrivalTimeAndSchedulesAlloggiati()
    {
        // 22:30 UTC on 30/09 is 00:30 on 01/10 in Rome: the check-in day has started.
        var now = new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.Zero);
        await using var db = CreateDb();
        var booking = await SeedAsync(db, BookingStatus.Confirmed, October1, October3);

        var result = await CreateService(db, now).RegisterArrivalAsync(booking.Id);

        Assert.Equal(BookingStatus.CheckedIn, result.Booking.Status);
        Assert.True(result.GuestDataComplete);
        Assert.Equal(now.UtcDateTime, result.Booking.ArrivedAt);
        _alloggiatiScheduler.Verify(s => s.EnsureScheduledAsync(booking.Id), Times.Once);
        Assert.Equal(BookingStatus.CheckedIn, (await ReloadAsync(db, booking.Id)).Status);
    }

    [Fact]
    public async Task RegisterArrivalAsync_LateEveningInRomeBeforeTheCheckInDay_Throws422AndSchedulesNothing()
    {
        // 21:30 UTC on 30/09 is 23:30 on 30/09 in Rome: the check-in day (01/10) has not started.
        await using var db = CreateDb();
        var booking = await SeedAsync(db, BookingStatus.Confirmed, October1, October3);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            CreateService(db, new DateTimeOffset(2026, 9, 30, 21, 30, 0, TimeSpan.Zero)).RegisterArrivalAsync(booking.Id));

        Assert.Equal(BookingErrorCodes.ArrivalTooEarly, error.Code);
        Assert.Equal("01/10/2026", Assert.Single(error.MessageArgs));
        Assert.Equal(BookingStatus.Confirmed, (await ReloadAsync(db, booking.Id)).Status);
        _alloggiatiScheduler.Verify(s => s.EnsureScheduledAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RegisterArrivalAsync_OnTheCheckOutDay_ChecksInWithoutAnArrivalTime()
    {
        // 21:00 in Rome on the check-out day.
        var now = new DateTimeOffset(2026, 10, 3, 19, 0, 0, TimeSpan.Zero);
        await using var db = CreateDb();
        var booking = await SeedAsync(db, BookingStatus.Confirmed, October1, October3);

        var result = await CreateService(db, now).RegisterArrivalAsync(booking.Id);

        Assert.Equal(BookingStatus.CheckedIn, result.Booking.Status);
        // Registered late: the real arrival time is unknown, so none is invented.
        Assert.Null(result.Booking.ArrivedAt);
        _alloggiatiScheduler.Verify(s => s.EnsureScheduledAsync(booking.Id), Times.Once);
    }

    [Fact]
    public async Task RegisterArrivalAsync_DayAfterTheCheckOutDay_Throws422()
    {
        await using var db = CreateDb();
        var booking = await SeedAsync(db, BookingStatus.Confirmed, October1, October3);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            CreateService(db, new DateTimeOffset(2026, 10, 3, 22, 30, 0, TimeSpan.Zero)).RegisterArrivalAsync(booking.Id));

        Assert.Equal(BookingErrorCodes.ArrivalAfterDeparture, error.Code);
    }

    [Theory]
    [InlineData(BookingStatus.CheckedIn, BookingErrorCodes.AlreadyCheckedIn)]
    [InlineData(BookingStatus.Pending, BookingErrorCodes.NotConfirmed)]
    [InlineData(BookingStatus.Cancelled, BookingErrorCodes.NotConfirmed)]
    [InlineData(BookingStatus.CheckedOut, BookingErrorCodes.NotConfirmed)]
    public async Task RegisterArrivalAsync_BookingNotConfirmed_Throws409AndSchedulesNothing(BookingStatus status, string code)
    {
        await using var db = CreateDb();
        var booking = await SeedAsync(db, status, October1, October3);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() =>
            CreateService(db, new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero)).RegisterArrivalAsync(booking.Id));

        Assert.Equal(code, error.Code);
        Assert.Equal(status, (await ReloadAsync(db, booking.Id)).Status);
        _alloggiatiScheduler.Verify(s => s.EnsureScheduledAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RegisterArrivalAsync_IncompleteGuestData_ChecksInAndSaysTheDataAreIncomplete()
    {
        _alloggiati.Setup(a => a.IsStayDataCompleteAsync(It.IsAny<Guid>())).ReturnsAsync(false);
        await using var db = CreateDb();
        var booking = await SeedAsync(db, BookingStatus.Confirmed, October1, October3);

        var result = await CreateService(db, new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero)).RegisterArrivalAsync(booking.Id);

        Assert.Equal(BookingStatus.CheckedIn, result.Booking.Status);
        Assert.False(result.GuestDataComplete);
    }

    [Fact]
    public async Task RegisterArrivalAsync_AlloggiatiSchedulingFails_KeepsTheArrival()
    {
        _alloggiatiScheduler.Setup(s => s.EnsureScheduledAsync(It.IsAny<Guid>())).ThrowsAsync(new TimeoutException("hangfire"));
        await using var db = CreateDb();
        var booking = await SeedAsync(db, BookingStatus.Confirmed, October1, October3);

        var result = await CreateService(db, new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero)).RegisterArrivalAsync(booking.Id);

        Assert.Equal(BookingStatus.CheckedIn, (await ReloadAsync(db, result.Booking.Id)).Status);
    }

    [Fact]
    public async Task CheckOutAsync_CheckedInStay_ClosesIt()
    {
        await using var db = CreateDb();
        var booking = await SeedAsync(db, BookingStatus.CheckedIn, October1, October3);

        var closed = await CreateService(db, new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero))
            .CheckOutAsync(booking.Id, new StayCheckOut(RegisterArrival: false));

        Assert.Equal(BookingStatus.CheckedOut, closed.Status);
        Assert.Equal(BookingStatus.CheckedOut, (await ReloadAsync(db, booking.Id)).Status);
    }

    [Fact]
    public async Task CheckOutAsync_ConfirmedWithoutRegisterArrival_Throws409AndChangesNothing()
    {
        await using var db = CreateDb();
        var booking = await SeedAsync(db, BookingStatus.Confirmed, October1, October3);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() =>
            CreateService(db, new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero))
                .CheckOutAsync(booking.Id, new StayCheckOut(RegisterArrival: false)));

        Assert.Equal(BookingErrorCodes.ArrivalNotRegistered, error.Code);
        Assert.Equal(BookingStatus.Confirmed, (await ReloadAsync(db, booking.Id)).Status);
    }

    [Fact]
    public async Task CheckOutAsync_BeforeTheCheckInDay_Throws422()
    {
        await using var db = CreateDb();
        var booking = await SeedAsync(db, BookingStatus.Confirmed, October1, October3);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            CreateService(db, new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero))
                .CheckOutAsync(booking.Id, new StayCheckOut(RegisterArrival: true)));

        Assert.Equal(BookingErrorCodes.CheckOutTooEarly, error.Code);
    }

    [Fact]
    public async Task CheckOutAsync_TurnoverRequestRefused_Throws422AndKeepsTheStayOpen()
    {
        _serviceRequests
            .Setup(s => s.CreateAsync(It.IsAny<CreateServiceRequestCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException(
                ServiceRequestErrorCodes.SupplierInactive, ServiceRequestErrorCodes.SupplierInactiveMessageKey));
        await using var db = CreateDb();
        var booking = await SeedAsync(db, BookingStatus.CheckedIn, October1, October3);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            CreateService(db, new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero)).CheckOutAsync(
                booking.Id,
                new StayCheckOut(false, new StayTurnoverRequest("auth0|host", Guid.NewGuid(), null, null))));

        Assert.Equal(BookingErrorCodes.TurnoverRequestInvalid, error.Code);
        Assert.Equal(BookingStatus.CheckedIn, (await ReloadAsync(db, booking.Id)).Status);
        _serviceRequests.Verify(
            s => s.CreateAsync(It.Is<CreateServiceRequestCommand>(c => c.BookingId == booking.Id && c.Category == "cleaning"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private StayLifecycleService CreateService(AppDbContext db, DateTimeOffset utcNow) =>
        new(
            db,
            _alloggiati.Object,
            _alloggiatiScheduler.Object,
            _serviceRequests.Object,
            NullLogger<StayLifecycleService>.Instance,
            new FixedTimeProvider(utcNow));

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Booking> SeedAsync(
        AppDbContext db,
        BookingStatus status,
        DateTime checkIn,
        DateTime checkOut)
    {
        var org = new OrgEntity { Name = "Org", Slug = $"org-{Guid.NewGuid():N}" };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|host",
            Name = "Casa Test",
            Address = "Via Roma 1",
            City = "Roma",
            Timezone = "Europe/Rome",
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna-{Guid.NewGuid():N}@test.com",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 1,
            Status = status,
            Source = BookingSource.Manual,
        };
        db.AddRange(org, property, guest, booking);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return booking;
    }

    private static async Task<Booking> ReloadAsync(AppDbContext db, Guid bookingId) =>
        await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
}
