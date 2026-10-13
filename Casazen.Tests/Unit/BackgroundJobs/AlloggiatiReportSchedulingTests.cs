using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// CO-11 (A5-03): the Alloggiati job is scheduled (Hangfire <c>Schedule</c>) at the start of the arrival day in
/// Europe/Rome, once per booking and guest, whoever asks first (guest portal or host check-in).
/// </summary>
public class AlloggiatiReportSchedulingTests
{
    private static readonly DateTime CheckIn = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RomeMidnightOfCheckIn = new(2026, 10, 9, 22, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private int _jobCounter;

    public AlloggiatiReportSchedulingTests()
    {
        _jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns(() => $"job-{++_jobCounter}");
    }

    [Fact]
    public async Task EnsureScheduled_GuestSubmitsThreeDaysBefore_SchedulesAtRomeMidnightNotNow()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        var scheduler = CreateScheduler(db, CheckIn.AddDays(-3).AddHours(9));

        await scheduler.EnsureScheduledAsync(booking.Id);

        _jobs.Verify(c => c.Create(
            It.Is<Job>(j => IsReportJob(j, booking)),
            It.Is<ScheduledState>(s => s.EnqueueAt == RomeMidnightOfCheckIn)), Times.Once);
        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()), Times.Never);
        var report = await db.AlloggiatiWebReports.SingleAsync();
        Assert.Equal("job-1", report.ScheduledJobId);
        Assert.Equal(RomeMidnightOfCheckIn, report.ScheduledFor);
        Assert.Equal(AlloggiatiWebStatus.DaInviare, report.Status);
    }

    [Fact]
    public async Task EnsureScheduled_PortalThenHostCheckInAfterTheJobRan_QueuesTheJobOnce()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);

        await CreateScheduler(db, CheckIn.AddDays(-2)).EnsureScheduledAsync(booking.Id); // guest portal
        await CreateJob(db, RomeMidnightOfCheckIn).ReportGuestAsync(booking.GuestId, booking.Id); // arrival day
        await CreateScheduler(db, RomeMidnightOfCheckIn.AddHours(15)).EnsureScheduledAsync(booking.Id); // host check-in

        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
        var report = await db.AlloggiatiWebReports.SingleAsync();
        Assert.Equal(AlloggiatiWebStatus.DaInviareManualmente, report.Status);
    }

    [Fact]
    public async Task EnsureScheduled_HostCheckInWhileTheJobIsDue_QueuesTheJobOnce()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);

        await CreateScheduler(db, CheckIn.AddDays(-2)).EnsureScheduledAsync(booking.Id); // guest portal
        await CreateScheduler(db, RomeMidnightOfCheckIn.AddMinutes(30)).EnsureScheduledAsync(booking.Id); // host check-in

        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
        _jobs.Verify(c => c.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        Assert.Single(db.AlloggiatiWebReports);
    }

    [Fact]
    public async Task EnsureScheduled_HostCheckInOnArrivalDay_RunsRightAway()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        var now = RomeMidnightOfCheckIn.AddHours(16);

        await CreateScheduler(db, now).EnsureScheduledAsync(booking.Id);

        _jobs.Verify(c => c.Create(
            It.Is<Job>(j => IsReportJob(j, booking)),
            It.Is<ScheduledState>(s => s.EnqueueAt == now)), Times.Once);
    }

    [Fact]
    public async Task EnsureScheduled_LostJob_SchedulesAgainAndDeletesTheOldOne()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        db.AlloggiatiWebReports.Add(new AlloggiatiWebReport
        {
            BookingId = booking.Id,
            GuestId = booking.GuestId,
            OrgId = booking.OrgId,
            Status = AlloggiatiWebStatus.DaInviare,
            ScheduledJobId = "lost",
            ScheduledFor = RomeMidnightOfCheckIn,
        });
        await db.SaveChangesAsync();

        await CreateScheduler(db, RomeMidnightOfCheckIn.AddHours(6)).EnsureScheduledAsync(booking.Id);

        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<ScheduledState>()), Times.Once);
        _jobs.Verify(c => c.ChangeState("lost", It.IsAny<DeletedState>(), null), Times.Once);
        Assert.Equal("job-1", (await db.AlloggiatiWebReports.SingleAsync()).ScheduledJobId);
    }

    [Fact]
    public async Task ReportGuestJob_ArrivalDayReached_MarksToSendManuallyAndSchedulesNothing()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        var now = RomeMidnightOfCheckIn.AddMinutes(1);
        await CreateScheduler(db, CheckIn.AddDays(-1)).EnsureScheduledAsync(booking.Id);
        _jobs.Invocations.Clear();

        await CreateJob(db, now).ReportGuestAsync(booking.GuestId, booking.Id);

        Assert.Equal(AlloggiatiWebStatus.DaInviareManualmente, (await db.AlloggiatiWebReports.SingleAsync()).Status);
        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task ReportGuestJob_CheckInMovedLater_ReschedulesForTheNewArrivalDay()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await CreateScheduler(db, CheckIn.AddDays(-1)).EnsureScheduledAsync(booking.Id);
        booking.CheckInDate = CheckIn.AddDays(2);
        booking.CheckOutDate = booking.CheckOutDate.AddDays(2);
        await db.SaveChangesAsync();
        _jobs.Invocations.Clear();

        await CreateJob(db, RomeMidnightOfCheckIn.AddMinutes(1)).ReportGuestAsync(booking.GuestId, booking.Id);

        Assert.Equal(AlloggiatiWebStatus.DaInviare, (await db.AlloggiatiWebReports.SingleAsync()).Status);
        _jobs.Verify(c => c.Create(
            It.IsAny<Job>(),
            It.Is<ScheduledState>(s => s.EnqueueAt == RomeMidnightOfCheckIn.AddDays(2))), Times.Once);
    }

    [Fact]
    public async Task ReportGuestJob_QueuedBeforeCo11WithoutReport_ReservesAndSchedulesIt()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);

        await CreateJob(db, CheckIn.AddDays(-4)).ReportGuestAsync(booking.GuestId, booking.Id);

        Assert.Equal(AlloggiatiWebStatus.DaInviare, (await db.AlloggiatiWebReports.SingleAsync()).Status);
        _jobs.Verify(c => c.Create(
            It.IsAny<Job>(),
            It.Is<ScheduledState>(s => s.EnqueueAt == RomeMidnightOfCheckIn)), Times.Once);
    }

    private static bool IsReportJob(Job job, Booking booking) =>
        job.Type == typeof(AlloggiatiWebReportJob)
        && job.Method.Name == nameof(AlloggiatiWebReportJob.ReportGuestAsync)
        && (Guid)job.Args[0] == booking.GuestId
        && (Guid)job.Args[1] == booking.Id;

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AlloggiatiWebService CreateService(AppDbContext db, DateTime utcNow) =>
        new(db, NullLogger<AlloggiatiWebService>.Instance, new FixedTimeProvider(new DateTimeOffset(utcNow, TimeSpan.Zero)));

    private AlloggiatiReportScheduler CreateScheduler(AppDbContext db, DateTime utcNow) =>
        new(CreateService(db, utcNow), _jobs.Object, NullLogger<AlloggiatiReportScheduler>.Instance);

    private AlloggiatiWebReportJob CreateJob(AppDbContext db, DateTime utcNow) =>
        new(CreateService(db, utcNow), CreateScheduler(db, utcNow), NullLogger<AlloggiatiWebReportJob>.Instance);

    private static async Task<Booking> SeedBookingAsync(AppDbContext db)
    {
        var orgId = Guid.NewGuid();
        var guest = new Guest { Id = Guid.NewGuid(), OrgId = orgId, FirstName = "Anna", LastName = "Bianchi", Email = "anna@example.com" };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            OrgId = orgId,
            GuestId = guest.Id,
            CheckInDate = CheckIn,
            CheckOutDate = CheckIn.AddDays(3),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }
}
