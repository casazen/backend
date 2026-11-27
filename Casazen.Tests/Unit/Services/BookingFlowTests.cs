using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>casazen/backend#978: the settings of the booking flow, their defaults (every step automatic) and their bounds.</summary>
public class BookingFlowSettingsServiceTests
{
    private static readonly IOptions<GuestCheckInOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new GuestCheckInOptions { SendWindowDays = 3, SessionLifetimeDays = 7 });

    [Fact]
    public async Task GetAsync_OrgWithoutRow_EveryStepAutomatic_LinkWithTheDefaultWindow()
    {
        await using var db = BookingFlowTestData.CreateDb();

        var view = await Service(db).GetAsync(Guid.NewGuid());

        Assert.True(view.IsDefault);
        Assert.Equal(BookingFlowMode.Automatic, view.CheckInLinkMode);
        Assert.Equal(3, view.CheckInLinkDaysBefore);
        Assert.Equal(7, view.MaxCheckInLinkDaysBefore);
        Assert.Equal(BookingFlowMode.Automatic, view.ArrivalMode);
        Assert.Equal(BookingFlowMode.Automatic, view.DepartureMode);
        Assert.Equal(BookingFlowMode.Automatic, view.PropertyReadyMode);
        Assert.Equal(PropertyReadyTrigger.CleaningDone, view.PropertyReadyTrigger);
    }

    [Fact]
    public async Task UpdateAsync_SomeMembers_SavesThemAndKeepsTheOthers()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var orgId = Guid.NewGuid();

        var saved = await Service(db).UpdateAsync(
            orgId,
            new BookingFlowSettingsUpdate(CheckInLinkMode: BookingFlowMode.Manual, PropertyReadyTrigger: PropertyReadyTrigger.CheckOut),
            "auth0|admin");

        Assert.False(saved.IsDefault);
        Assert.Equal(BookingFlowMode.Manual, saved.CheckInLinkMode);
        Assert.Equal(3, saved.CheckInLinkDaysBefore);
        Assert.Equal(BookingFlowMode.Automatic, saved.ArrivalMode);
        Assert.Equal(PropertyReadyTrigger.CheckOut, saved.PropertyReadyTrigger);
        var row = await db.BookingFlowSettings.SingleAsync();
        Assert.Equal(orgId, row.OrgId);
        Assert.Equal("auth0|admin", row.UpdatedByUserId);
        Assert.Equal(saved, await Service(db).GetAsync(orgId));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public async Task UpdateAsync_DaysOutsideTheLifeOfALink_Throws422AndSavesNothing(int days)
    {
        await using var db = BookingFlowTestData.CreateDb();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service(db).UpdateAsync(Guid.NewGuid(), new BookingFlowSettingsUpdate(CheckInLinkDaysBefore: days), "auth0|admin"));

        Assert.Equal(BookingFlowErrorCodes.DaysOutOfRange, error.Code);
        Assert.False(await db.BookingFlowSettings.AnyAsync());
    }

    [Fact]
    public async Task UpdateAsync_AModeThatDoesNotExist_Throws422()
    {
        await using var db = BookingFlowTestData.CreateDb();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service(db).UpdateAsync(Guid.NewGuid(), new BookingFlowSettingsUpdate(ArrivalMode: (BookingFlowMode)9), "auth0|admin"));

        Assert.Equal(BookingFlowErrorCodes.ValueInvalid, error.Code);
    }

    [Fact]
    public async Task GetManyAsync_RowsAndDefaults_OneViewPerOrg()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var manual = Guid.NewGuid();
        var withoutRow = Guid.NewGuid();
        db.BookingFlowSettings.Add(new BookingFlowSettings { OrgId = manual, DepartureMode = BookingFlowMode.Manual, CheckInLinkDaysBefore = 2 });
        await db.SaveChangesAsync();

        var views = await Service(db).GetManyAsync([manual, withoutRow, manual]);

        Assert.Equal(2, views.Count);
        Assert.Equal(BookingFlowMode.Manual, views[manual].DepartureMode);
        Assert.Equal(2, views[manual].CheckInLinkDaysBefore);
        Assert.True(views[withoutRow].IsDefault);
    }

    internal static BookingFlowSettingsService Service(AppDbContext db) =>
        new(db, Options, TimeProvider.System, NullLogger<BookingFlowSettingsService>.Instance);
}

/// <summary>
/// casazen/backend#978: the steps <see cref="StayLifecycleService"/> does in the host's place: the property declared ready (at the check-out or when the cleaning is done),
/// the automatic arrival and departure, and what the host decided first always winning.
/// </summary>
public class StayLifecycleBookingFlowTests
{
    private static readonly DateTime October1 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime October3 = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    // 12:00 in Rome on the departure day.
    private static readonly DateTimeOffset DepartureNoon = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly Mock<IPushNotificationService> _push = new();

    [Fact]
    public async Task CheckOutAsync_FlowReadyAtCheckOut_DeclaresThePropertyReady()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.CheckedIn, October1, October3);
        await BookingFlowTestData.SetFlowAsync(db, booking.OrgId, propertyReadyTrigger: PropertyReadyTrigger.CheckOut);

        await CreateService(db).CheckOutAsync(booking.Id, new StayCheckOut(RegisterArrival: false));

        var checkout = await db.StayCheckouts.AsNoTracking().SingleAsync(c => c.BookingId == booking.Id);
        Assert.NotNull(checkout.PropertyReadyAt);
        Assert.True(checkout.PropertyReady);
    }

    [Fact]
    public async Task CheckOutAsync_DefaultFlow_NoCleaningDone_LeavesThePropertyToTheCleaning()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.CheckedIn, October1, October3);

        await CreateService(db).CheckOutAsync(booking.Id, new StayCheckOut(RegisterArrival: false));

        var checkout = await db.StayCheckouts.AsNoTracking().SingleAsync(c => c.BookingId == booking.Id);
        Assert.Null(checkout.PropertyReadyAt);
    }

    [Fact]
    public async Task CheckOutAsync_DefaultFlow_CleaningAlreadyDone_DeclaresThePropertyReady()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.CheckedIn, October1, October3);
        await BookingFlowTestData.SeedStaffCleaningAsync(db, booking, InternalActivity.StatusCompleted);

        await CreateService(db).CheckOutAsync(booking.Id, new StayCheckOut(RegisterArrival: false));

        var checkout = await db.StayCheckouts.AsNoTracking().SingleAsync(c => c.BookingId == booking.Id);
        Assert.NotNull(checkout.PropertyReadyAt);
    }

    [Fact]
    public async Task CheckOutAsync_ManualFlow_LeavesThePropertyToTheHost()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.CheckedIn, October1, October3);
        await BookingFlowTestData.SetFlowAsync(db, booking.OrgId, propertyReadyMode: BookingFlowMode.Manual, propertyReadyTrigger: PropertyReadyTrigger.CheckOut);

        await CreateService(db).CheckOutAsync(booking.Id, new StayCheckOut(RegisterArrival: false));

        var checkout = await db.StayCheckouts.AsNoTracking().SingleAsync(c => c.BookingId == booking.Id);
        Assert.Null(checkout.PropertyReadyAt);
    }

    [Fact]
    public async Task DeclarePropertyReadyAutomaticallyAsync_CleaningDoneFlow_DeclaresOnceAndTellsTheHosts()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.CheckedOut, October1, October3);
        db.StayCheckouts.Add(new StayCheckout { OrgId = booking.OrgId, BookingId = booking.Id, CompletedAt = October3 });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var first = await service.DeclarePropertyReadyAutomaticallyAsync(booking.Id);
        var readyAt = first?.PropertyReadyAt;
        var second = await service.DeclarePropertyReadyAutomaticallyAsync(booking.Id);

        Assert.NotNull(readyAt);
        Assert.Equal(readyAt, second?.PropertyReadyAt);
        _push.Verify(
            p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.Is<PushNotificationPayload>(n => n.Type == PushTypes.BookingFlowStep && n.BookingId == booking.Id)),
            Times.Once);
    }

    [Fact]
    public async Task DeclarePropertyReadyAutomaticallyAsync_HostSaidNotReadyYet_LeavesItToTheHost()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.CheckedOut, October1, October3);
        db.StayCheckouts.Add(new StayCheckout { OrgId = booking.OrgId, BookingId = booking.Id, CompletedAt = October3, PropertyReady = false });
        await db.SaveChangesAsync();

        var record = await CreateService(db).DeclarePropertyReadyAutomaticallyAsync(booking.Id);

        Assert.Null(record?.PropertyReadyAt);
        Assert.Null((await db.StayCheckouts.AsNoTracking().SingleAsync()).PropertyReadyAt);
    }

    [Fact]
    public async Task DeclarePropertyReadyAutomaticallyAsync_ManualFlow_DoesNothing()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.CheckedOut, October1, October3);
        await BookingFlowTestData.SetFlowAsync(db, booking.OrgId, propertyReadyMode: BookingFlowMode.Manual);

        Assert.Null(await CreateService(db).DeclarePropertyReadyAutomaticallyAsync(booking.Id));
        Assert.False(await db.StayCheckouts.AnyAsync(c => c.PropertyReadyAt != null));
    }

    [Fact]
    public async Task RegisterArrivalAutomaticallyAsync_NoShowMarked_LeavesTheStayAsItIs()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.Confirmed, October1, October3, noShowAt: October1);

        Assert.Null(await CreateService(db).RegisterArrivalAutomaticallyAsync(booking.Id));
        Assert.Equal(BookingStatus.Confirmed, (await db.Bookings.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task RegisterArrivalAutomaticallyAsync_ArrivalDayOver_ChecksInAndTellsTheHosts()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.Confirmed, October1, October3);

        var result = await CreateService(db, new DateTimeOffset(2026, 10, 2, 6, 0, 0, TimeSpan.Zero)).RegisterArrivalAutomaticallyAsync(booking.Id);

        Assert.Equal(BookingStatus.CheckedIn, result?.Booking.Status);
        _push.Verify(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.Is<PushNotificationPayload>(n => n.Type == PushTypes.BookingFlowStep)), Times.Once);
    }

    [Fact]
    public async Task CheckOutAutomaticallyAsync_ConfirmedStay_RegistersTheArrivalAndTheDeparture()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.Confirmed, October1, October3);

        var closed = await CreateService(db).CheckOutAutomaticallyAsync(booking.Id, registerArrival: true);

        Assert.Equal(BookingStatus.CheckedOut, closed?.Status);
        Assert.Equal(BookingStatus.CheckedOut, (await db.Bookings.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task CheckOutAutomaticallyAsync_WizardOpenedByTheHost_LeavesTheStayToTheHost()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var booking = await BookingFlowTestData.SeedStayAsync(db, BookingStatus.CheckedIn, October1, October3, wizardStartedAt: October3);

        Assert.Null(await CreateService(db).CheckOutAutomaticallyAsync(booking.Id, registerArrival: false));
        Assert.Equal(BookingStatus.CheckedIn, (await db.Bookings.AsNoTracking().SingleAsync()).Status);
        _push.Verify(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()), Times.Never);
    }

    private StayLifecycleService CreateService(AppDbContext db, DateTimeOffset? utcNow = null) =>
        new(
            db,
            Mock.Of<IAlloggiatiWebService>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IServiceRequestService>(),
            NullLogger<StayLifecycleService>.Instance,
            new FixedTimeProvider(utcNow ?? DepartureNoon),
            flowSettings: BookingFlowSettingsServiceTests.Service(db),
            pushNotifications: _push.Object);
}

/// <summary>casazen/backend#978: what the hourly job of the booking flow does for a stay, and when.</summary>
public class BookingFlowAutomationJobTests
{
    private static readonly DateTime October1 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime October2 = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime October3 = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly BookingFlowSettingsView Automatic = BookingFlowRules.Defaults(3, 7);

    [Fact]
    public void StepOf_ArrivalDayNotOver_Nothing()
    {
        // 20:00 in Rome on the arrival day: the host may still register it or mark a no-show.
        Assert.Null(BookingFlowAutomationJob.StepOf(Stay(BookingStatus.Confirmed), Automatic, October1, new DateTime(2026, 10, 1, 18, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void StepOf_ArrivalDayOver_RegistersTheArrival()
    {
        Assert.Equal(
            BookingFlowAutomaticStep.Arrival,
            BookingFlowAutomationJob.StepOf(Stay(BookingStatus.Confirmed), Automatic, October2, new DateTime(2026, 10, 1, 23, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void StepOf_BeforeTheCheckOutTime_Nothing()
    {
        // 09:00 in Rome, check-out at 10:00 (the property says none).
        Assert.Null(BookingFlowAutomationJob.StepOf(Stay(BookingStatus.CheckedIn), Automatic, October3, new DateTime(2026, 10, 3, 7, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void StepOf_AfterTheCheckOutTimeOfTheProperty_RegistersTheDeparture()
    {
        // 11:30 in Rome, the property checks out at 11:00.
        Assert.Equal(
            BookingFlowAutomaticStep.Departure,
            BookingFlowAutomationJob.StepOf(Stay(BookingStatus.CheckedIn, new TimeOnly(11, 0)), Automatic, October3, new DateTime(2026, 10, 3, 9, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void StepOf_ManualSteps_Nothing()
    {
        var manual = Automatic with { ArrivalMode = BookingFlowMode.Manual, DepartureMode = BookingFlowMode.Manual };
        var afternoon = new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc);

        Assert.Null(BookingFlowAutomationJob.StepOf(Stay(BookingStatus.CheckedIn), manual, October3, afternoon));
        Assert.Null(BookingFlowAutomationJob.StepOf(Stay(BookingStatus.Confirmed), manual, October3, afternoon));
    }

    [Fact]
    public void StepOf_ManualArrivalNeverRegistered_TheDepartureWaitsForTheHost()
    {
        var manualArrival = Automatic with { ArrivalMode = BookingFlowMode.Manual };

        Assert.Null(BookingFlowAutomationJob.StepOf(Stay(BookingStatus.Confirmed), manualArrival, October3, new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc)));
    }

    private static BookingFlowAutomationJob.Stay Stay(BookingStatus status, TimeOnly? checkOutBy = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), status, October1, October3, checkOutBy, "Europe/Rome");
}

/// <summary>casazen/backend#978: the cleaning a supplier completed declares the property of its stay ready, through the booking flow.</summary>
public class PropertyReadyAutomationHandlerTests
{
    [Fact]
    public async Task HandleAsync_CleaningOfAStayCompleted_AsksTheFlowToDeclareThePropertyReady()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var (orgId, requestId, bookingId) = await SeedRequestAsync(db, Casazen.Core.Suppliers.ServiceCategories.Cleaning);
        var lifecycle = new Mock<IStayLifecycleService>();

        await new PropertyReadyAutomationHandler(db, lifecycle.Object).HandleAsync(Event(orgId, requestId), CancellationToken.None);

        lifecycle.Verify(l => l.DeclarePropertyReadyAutomaticallyAsync(bookingId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_AnotherCategory_DoesNothing()
    {
        await using var db = BookingFlowTestData.CreateDb();
        var (orgId, requestId, _) = await SeedRequestAsync(db, "maintenance");
        var lifecycle = new Mock<IStayLifecycleService>();

        await new PropertyReadyAutomationHandler(db, lifecycle.Object).HandleAsync(Event(orgId, requestId), CancellationToken.None);

        lifecycle.Verify(l => l.DeclarePropertyReadyAutomaticallyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static DomainEvent Event(Guid orgId, Guid requestId) =>
        new(Guid.NewGuid(), orgId, DomainEventType.ServiceRequestCompleted, requestId, DateTime.UtcNow, "{}");

    private static async Task<(Guid OrgId, Guid RequestId, Guid BookingId)> SeedRequestAsync(AppDbContext db, string category)
    {
        var orgId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var request = new ServiceRequest
        {
            OrgId = orgId,
            BookingId = bookingId,
            PropertyId = Guid.NewGuid(),
            SupplierOrgId = Guid.NewGuid(),
            Category = category,
            Status = ServiceRequestStatus.Completato,
        };
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return (orgId, request.Id, bookingId);
    }
}

/// <summary>The data of the tests of the booking flow: a stay with its org, property and guest, the flow of an org, a staff cleaning.</summary>
internal static class BookingFlowTestData
{
    public static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public static async Task<Booking> SeedStayAsync(
        AppDbContext db,
        BookingStatus status,
        DateTime checkIn,
        DateTime checkOut,
        DateTime? noShowAt = null,
        DateTime? wizardStartedAt = null)
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
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = $"anna-{Guid.NewGuid():N}@example.com" };
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
            NoShowAt = noShowAt,
            CheckoutWizardStartedAt = wizardStartedAt,
        };
        db.AddRange(org, property, guest, booking);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return booking;
    }

    public static async Task SetFlowAsync(
        AppDbContext db,
        Guid orgId,
        BookingFlowMode propertyReadyMode = BookingFlowMode.Automatic,
        PropertyReadyTrigger propertyReadyTrigger = PropertyReadyTrigger.CleaningDone)
    {
        db.BookingFlowSettings.Add(new BookingFlowSettings
        {
            OrgId = orgId,
            CheckInLinkDaysBefore = 3,
            PropertyReadyMode = propertyReadyMode,
            PropertyReadyTrigger = propertyReadyTrigger,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    public static async Task SeedStaffCleaningAsync(AppDbContext db, Booking booking, string status)
    {
        db.InternalActivities.Add(new InternalActivity
        {
            OrgId = booking.OrgId,
            PropertyId = booking.PropertyId,
            BookingId = booking.Id,
            StaffUserId = "auth0|staff",
            ActivityType = InternalActivity.CleaningActivityType,
            Status = status,
            ScheduledAt = booking.CheckOutDate,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
