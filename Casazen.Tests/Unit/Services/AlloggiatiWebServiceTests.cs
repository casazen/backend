using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Web.DTOs.Alloggiati;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CO-11 (A5-01, A9-05, A5-03, A5-37, A5-35): CasaZen does not transmit to Alloggiati Web, so nothing may ever look
/// sent without a receipt; the job runs on the arrival day in Europe/Rome; the report row is the idempotency key.
/// These tests replace the ones that certified the simulated "Submitted" as a success.
/// </summary>
public class AlloggiatiWebServiceTests
{
    // 10 Oct 2026 is in summer time (CEST, UTC+2); 10 Dec 2026 in winter time (CET, UTC+1).
    private static readonly DateTime CheckIn = Utc(2026, 10, 10);
    private static readonly DateTime CheckOut = Utc(2026, 10, 13);
    private static readonly DateTime RomeMidnightOfCheckIn = Utc(2026, 10, 9, 22);

    [Fact]
    public async Task ProcessScheduledReport_ArrivalDayReached_MarksToSendManuallyAndNeverSent()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, AlloggiatiWebStatus.DaInviare);
        var service = CreateService(db, RomeMidnightOfCheckIn.AddMinutes(1));

        var outcome = await service.ProcessScheduledReportAsync(booking.Id, booking.GuestId);

        Assert.Equal(AlloggiatiProcessOutcome.MarkedForManualSubmission, outcome);
        var report = await db.AlloggiatiWebReports.SingleAsync();
        Assert.Equal(AlloggiatiWebStatus.DaInviareManualmente, report.Status);
        Assert.Null(report.ReportedAt);
        Assert.Null(report.ConfirmationNumber);
        Assert.False(report.ManuallyCompleted);
        var status = await service.GetStatusAsync(booking.Id);
        Assert.Equal(AlloggiatiWebStatus.DaInviareManualmente, status.Status);
        Assert.Null(status.ReportedAt);
    }

    [Theory]
    [InlineData(2026, 10, 9, 21, 59, AlloggiatiProcessOutcome.NotYetDue)] // 23:59 in Rome on 9 Oct (CEST)
    [InlineData(2026, 10, 9, 22, 0, AlloggiatiProcessOutcome.MarkedForManualSubmission)] // 00:00 in Rome on 10 Oct
    public async Task ProcessScheduledReport_AroundRomeMidnight_UsesTheArrivalDayInEuropeRome(
        int year, int month, int day, int hour, int minute, AlloggiatiProcessOutcome expected)
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, AlloggiatiWebStatus.DaInviare);
        var service = CreateService(db, new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc));

        var outcome = await service.ProcessScheduledReportAsync(booking.Id, booking.GuestId);

        Assert.Equal(expected, outcome);
        var stored = (await db.AlloggiatiWebReports.SingleAsync()).Status;
        Assert.Equal(
            expected == AlloggiatiProcessOutcome.NotYetDue ? AlloggiatiWebStatus.DaInviare : AlloggiatiWebStatus.DaInviareManualmente,
            stored);
    }

    [Theory]
    [InlineData(AlloggiatiWebStatus.InviatoManualmente)]
    [InlineData(AlloggiatiWebStatus.DaInviareManualmente)]
    [InlineData(AlloggiatiWebStatus.Rifiutato)]
    public async Task ProcessScheduledReport_ReportPastArrivalStep_IsNoOp(AlloggiatiWebStatus status)
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, status);
        var service = CreateService(db, RomeMidnightOfCheckIn.AddHours(2));

        var outcome = await service.ProcessScheduledReportAsync(booking.Id, booking.GuestId);

        Assert.Equal(AlloggiatiProcessOutcome.AlreadyHandled, outcome);
        Assert.Equal(status, (await db.AlloggiatiWebReports.SingleAsync()).Status);
    }

    [Fact]
    public async Task ProcessScheduledReport_BookingGuestReplaced_ReturnsSupersededWithoutChanges()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        var oldGuestId = booking.GuestId;
        await AddReportAsync(db, booking, AlloggiatiWebStatus.DaInviare);
        var snapshot = CompleteGuest(booking.OrgId);
        db.Guests.Add(snapshot);
        booking.GuestId = snapshot.Id;
        await db.SaveChangesAsync();

        var outcome = await CreateService(db, RomeMidnightOfCheckIn.AddHours(1))
            .ProcessScheduledReportAsync(booking.Id, oldGuestId);

        Assert.Equal(AlloggiatiProcessOutcome.Superseded, outcome);
        Assert.Equal(AlloggiatiWebStatus.DaInviare, (await db.AlloggiatiWebReports.SingleAsync()).Status);
    }

    [Fact]
    public async Task ProcessScheduledReport_CancelledBeforeArrival_LeavesNothingToSend()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db, status: BookingStatus.Cancelled);
        await AddReportAsync(db, booking, AlloggiatiWebStatus.DaInviare);

        var outcome = await CreateService(db, RomeMidnightOfCheckIn.AddHours(1))
            .ProcessScheduledReportAsync(booking.Id, booking.GuestId);

        Assert.Equal(AlloggiatiProcessOutcome.BookingInactive, outcome);
        Assert.Equal(AlloggiatiWebStatus.DaInviare, (await db.AlloggiatiWebReports.SingleAsync()).Status);
    }

    [Fact]
    public async Task ProcessScheduledReport_NoReport_ReturnsNotReserved()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);

        var outcome = await CreateService(db, RomeMidnightOfCheckIn.AddHours(1))
            .ProcessScheduledReportAsync(booking.Id, booking.GuestId);

        Assert.Equal(AlloggiatiProcessOutcome.NotReserved, outcome);
        Assert.Empty(db.AlloggiatiWebReports);
    }

    [Theory]
    [InlineData(2026, 10, 10, 2026, 10, 9, 22)] // CEST: 00:00 in Rome = 22:00 UTC of the day before
    [InlineData(2026, 12, 10, 2026, 12, 9, 23)] // CET: 00:00 in Rome = 23:00 UTC of the day before
    public async Task ReserveReport_GuestSubmitsDaysBefore_SchedulesAtRomeMidnightOfArrivalDay(
        int checkInYear, int checkInMonth, int checkInDay, int runYear, int runMonth, int runDay, int runHour)
    {
        await using var db = CreateDb();
        var checkIn = Utc(checkInYear, checkInMonth, checkInDay);
        var booking = await SeedBookingAsync(db, checkIn: checkIn, checkOut: checkIn.AddDays(3));
        var service = CreateService(db, checkIn.AddDays(-3));

        var reservation = await service.ReserveReportAsync(booking.Id);

        Assert.NotNull(reservation);
        Assert.Equal(Utc(runYear, runMonth, runDay, runHour), reservation.RunAtUtc);
        Assert.Equal(booking.GuestId, reservation.GuestId);
        Assert.Null(reservation.PreviousJobId);
        var report = await db.AlloggiatiWebReports.SingleAsync();
        Assert.Equal(AlloggiatiWebStatus.DaInviare, report.Status);
        Assert.Equal(report.Id, reservation.ReportId);
        Assert.Equal(booking.OrgId, report.OrgId); // TN-2: the report belongs to its booking's org
        Assert.Null(report.ReportedAt);
    }

    [Fact]
    public async Task ReserveReport_ArrivalDayAlreadyStarted_RunsNow()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        var now = RomeMidnightOfCheckIn.AddHours(15);

        var reservation = await CreateService(db, now).ReserveReportAsync(booking.Id);

        Assert.Equal(now, reservation!.RunAtUtc);
    }

    [Fact]
    public async Task ReserveReport_JobAlreadyScheduled_ReturnsNullSoNothingIsQueuedTwice()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        var service = CreateService(db, CheckIn.AddDays(-2));
        var first = await service.ReserveReportAsync(booking.Id);
        await service.SetScheduledJobAsync(first!.ReportId, "job-1", first.RunAtUtc);

        var second = await service.ReserveReportAsync(booking.Id);
        var onArrival = await CreateService(db, RomeMidnightOfCheckIn.AddMinutes(10)).ReserveReportAsync(booking.Id);

        Assert.Null(second);
        Assert.Null(onArrival);
        Assert.Single(db.AlloggiatiWebReports);
    }

    [Theory]
    [InlineData(AlloggiatiWebStatus.DaInviareManualmente)]
    [InlineData(AlloggiatiWebStatus.InviatoManualmente)]
    public async Task ReserveReport_ReportPastArrivalStep_ReturnsNull(AlloggiatiWebStatus status)
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, status);

        var reservation = await CreateService(db, RomeMidnightOfCheckIn.AddHours(3)).ReserveReportAsync(booking.Id);

        Assert.Null(reservation);
        Assert.Equal(status, (await db.AlloggiatiWebReports.SingleAsync()).Status);
    }

    [Fact]
    public async Task ReserveReport_ScheduledJobNeverRan_ReschedulesReplacingTheLostJob()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, AlloggiatiWebStatus.DaInviare, jobId: "lost-job", scheduledFor: RomeMidnightOfCheckIn);
        var now = RomeMidnightOfCheckIn.AddHours(5);

        var reservation = await CreateService(db, now).ReserveReportAsync(booking.Id);

        Assert.NotNull(reservation);
        Assert.Equal("lost-job", reservation.PreviousJobId);
        Assert.Equal(now, reservation.RunAtUtc);
    }

    [Fact]
    public async Task ReserveReport_CheckInMovedLater_ReschedulesAtTheNewArrivalDay()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, AlloggiatiWebStatus.DaInviare, jobId: "old-date", scheduledFor: RomeMidnightOfCheckIn);
        booking.CheckInDate = CheckIn.AddDays(5);
        booking.CheckOutDate = CheckOut.AddDays(5);
        await db.SaveChangesAsync();

        var reservation = await CreateService(db, CheckIn.AddDays(-1)).ReserveReportAsync(booking.Id);

        Assert.Equal(RomeMidnightOfCheckIn.AddDays(5), reservation!.RunAtUtc);
        Assert.Equal("old-date", reservation.PreviousJobId);
    }

    [Fact]
    public async Task ReserveReport_GuestReplacedBySnapshot_MovesTheReportInsteadOfDuplicatingIt()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, AlloggiatiWebStatus.DaInviare, jobId: "job-old-guest", scheduledFor: RomeMidnightOfCheckIn);
        var snapshot = CompleteGuest(booking.OrgId);
        db.Guests.Add(snapshot);
        booking.GuestId = snapshot.Id;
        await db.SaveChangesAsync();

        var reservation = await CreateService(db, CheckIn.AddDays(-1)).ReserveReportAsync(booking.Id);

        var report = await db.AlloggiatiWebReports.SingleAsync();
        Assert.Equal(snapshot.Id, report.GuestId);
        Assert.Equal(snapshot.Id, reservation!.GuestId);
        Assert.Equal("job-old-guest", reservation.PreviousJobId);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task ReserveReport_BookingNotActive_ReturnsNullAndCreatesNothing(BookingStatus status)
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db, status: status);

        var reservation = await CreateService(db, CheckIn.AddDays(-1)).ReserveReportAsync(booking.Id);

        Assert.Null(reservation);
        Assert.Empty(db.AlloggiatiWebReports);
    }

    [Fact]
    public async Task GetStatus_NoReportBeforeArrival_IsToSendAndNotOverdue()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);

        var status = await CreateService(db, CheckIn.AddDays(-1)).GetStatusAsync(booking.Id);

        Assert.Equal(AlloggiatiWebStatus.DaInviare, status.Status);
        Assert.False(status.IsOverdue);
    }

    [Fact]
    public async Task GetStatus_NoReportOnArrivalDay_IsToSendManuallyNeverSent()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);

        var status = await CreateService(db, RomeMidnightOfCheckIn.AddHours(10)).GetStatusAsync(booking.Id);

        Assert.Equal(AlloggiatiWebStatus.DaInviareManualmente, status.Status);
        Assert.Null(status.ReportedAt);
        Assert.Null(status.ConfirmationNumber);
    }

    [Fact]
    public async Task GetStatus_WithoutArrivedAt_DeadlineIs24HoursFromRomeMidnightOfCheckIn()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);

        var status = await CreateService(db, RomeMidnightOfCheckIn.AddHours(4)).GetStatusAsync(booking.Id);

        Assert.False(status.IsShortStay);
        Assert.Equal(RomeMidnightOfCheckIn.AddHours(24), status.DeadlineAt);
        Assert.Equal(20, status.HoursUntilDeadline, 3);
    }

    [Fact]
    public async Task GetStatus_ArrivedAtRecorded_DeadlineIs24HoursFromTheRealArrival()
    {
        await using var db = CreateDb();
        var arrivedAt = Utc(2026, 10, 10, 21); // 23:00 in Rome
        var booking = await SeedBookingAsync(db, arrivedAt: arrivedAt);

        var status = await CreateService(db, Utc(2026, 10, 11, 1)).GetStatusAsync(booking.Id);

        Assert.Equal(arrivedAt.AddHours(24), status.DeadlineAt);
        Assert.False(status.IsOverdue); // 03:00 in Rome the next day: still in time (A5-37)
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task GetStatus_StayOfAtMostOneNight_DeadlineIs6HoursFromArrival(int nights)
    {
        await using var db = CreateDb();
        var arrivedAt = Utc(2026, 10, 10, 13);
        var booking = await SeedBookingAsync(db, checkOut: CheckIn.AddDays(nights), arrivedAt: arrivedAt);

        var status = await CreateService(db, arrivedAt.AddHours(7)).GetStatusAsync(booking.Id);

        Assert.True(status.IsShortStay);
        Assert.Equal(arrivedAt.AddHours(6), status.DeadlineAt);
        Assert.True(status.IsOverdue);
    }

    [Fact]
    public async Task GetStatus_ToSendManuallyPastDeadline_IsOverdue()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, AlloggiatiWebStatus.DaInviareManualmente);

        var status = await CreateService(db, RomeMidnightOfCheckIn.AddHours(25)).GetStatusAsync(booking.Id);

        Assert.True(status.IsOverdue);
        Assert.Equal(0, status.HoursUntilDeadline);
    }

    [Fact]
    public async Task GetStatus_DeclaredSentPastDeadline_IsNotOverdue()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, AlloggiatiWebStatus.InviatoManualmente, reportedAt: CheckIn);

        var status = await CreateService(db, RomeMidnightOfCheckIn.AddHours(48)).GetStatusAsync(booking.Id);

        Assert.Equal(AlloggiatiWebStatus.InviatoManualmente, status.Status);
        Assert.False(status.IsOverdue);
        Assert.Equal(CheckIn, status.ReportedAt);
    }

    [Fact]
    public async Task GetStatus_UnknownBooking_ThrowsNotFound()
    {
        await using var db = CreateDb();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            CreateService(db, CheckIn).GetStatusAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task MarkSentManually_DateOnArrivalDay_RecordsDeclarationNotReceipt()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, AlloggiatiWebStatus.DaInviareManualmente);

        var status = await CreateService(db, RomeMidnightOfCheckIn.AddHours(30))
            .MarkSentManuallyAsync(booking.Id, CheckIn);

        Assert.Equal(AlloggiatiWebStatus.InviatoManualmente, status.Status);
        Assert.Equal(CheckIn, status.ReportedAt);
        Assert.Null(status.ConfirmationNumber);
        var report = await db.AlloggiatiWebReports.SingleAsync();
        Assert.Equal(AlloggiatiWebStatus.InviatoManualmente, report.Status);
        Assert.True(report.ManuallyCompleted);
        Assert.Null(report.ConfirmationNumber);
    }

    [Fact]
    public async Task MarkSentManually_NoReportYet_CreatesTheDeclaration()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);

        await CreateService(db, RomeMidnightOfCheckIn.AddHours(30)).MarkSentManuallyAsync(booking.Id, CheckIn.AddDays(1));

        var report = await db.AlloggiatiWebReports.SingleAsync();
        Assert.Equal(booking.GuestId, report.GuestId);
        Assert.Equal(booking.OrgId, report.OrgId);
        Assert.Equal(AlloggiatiWebStatus.InviatoManualmente, report.Status);
        Assert.Equal(CheckIn.AddDays(1), report.ReportedAt);
    }

    [Fact]
    public async Task MarkSentManually_DateAfterTodayInRome_ThrowsAndChangesNothing()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, AlloggiatiWebStatus.DaInviareManualmente);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            CreateService(db, RomeMidnightOfCheckIn.AddHours(10)).MarkSentManuallyAsync(booking.Id, CheckIn.AddDays(1)));

        Assert.Equal(AlloggiatiWebService.ManualDateInFutureCode, ex.Code);
        Assert.Equal(AlloggiatiWebStatus.DaInviareManualmente, (await db.AlloggiatiWebReports.SingleAsync()).Status);
    }

    [Fact]
    public async Task MarkSentManually_DateBeforeCheckIn_Throws()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            CreateService(db, RomeMidnightOfCheckIn.AddHours(10)).MarkSentManuallyAsync(booking.Id, CheckIn.AddDays(-1)));

        Assert.Equal(AlloggiatiWebService.ManualDateBeforeArrivalCode, ex.Code);
        Assert.Empty(db.AlloggiatiWebReports);
    }

    [Theory]
    [InlineData(AlloggiatiWebStatus.InviatoManualmente)]
    [InlineData(AlloggiatiWebStatus.Inviato)]
    public async Task MarkSentManually_AlreadySent_ThrowsConflict(AlloggiatiWebStatus status)
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);
        await AddReportAsync(db, booking, status, reportedAt: CheckIn, receipt: status == AlloggiatiWebStatus.Inviato ? "RIC-1" : null);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            CreateService(db, RomeMidnightOfCheckIn.AddHours(30)).MarkSentManuallyAsync(booking.Id, CheckIn));

        Assert.Equal(AlloggiatiWebService.AlreadySentCode, ex.Code);
    }

    [Fact]
    public async Task GetGuestSummary_RegisteredSingleGuest_ReturnsTheRecordFieldsAndCodesToComplete()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db, numberOfGuests: 1);

        var summary = await CreateService(db, CheckIn.AddDays(-1)).GetGuestSummaryAsync(booking.Id);

        Assert.Equal(CheckIn, summary.ArrivalDate);
        Assert.Equal(3, summary.StayDays);
        Assert.False(summary.StayExceedsMaxDays);
        Assert.Equal(1, summary.DeclaredGuests);
        var row = Assert.Single(summary.Guests);
        Assert.NotNull(row.StayGuestId);
        Assert.Equal(StayGuestType.SingleGuest, row.Type);
        Assert.False(row.IsMinor);
        Assert.Equal(CheckIn, row.ArrivalDate);
        Assert.Equal(3, row.StayDays);
        Assert.Equal("Rossi", row.LastName);
        Assert.Equal("Mario", row.FirstName);
        Assert.Equal(Gender.Male, row.Gender);
        Assert.Equal(Utc(1980, 4, 2), row.DateOfBirth);
        Assert.True(row.BornInItaly);
        Assert.Equal("Milano", row.BirthComune);
        Assert.Equal("MI", row.BirthProvince);
        Assert.Equal("Italia", row.Citizenship);
        Assert.True(row.RequiresDocument);
        Assert.Equal(GuestDocumentType.IdentityCard, row.DocumentType);
        Assert.Equal("CA12345AB", row.DocumentNumber);
        Assert.Equal("Milano", row.DocumentIssuePlace);
        Assert.Empty(row.MissingFields);
        Assert.Null(row.CompositionIssue);
        // No official table imported: every code is to complete, the data is complete but cannot be exported.
        Assert.Equal(
            new[] { "type", "birthComune", "birthCountry", "citizenship", "documentType", "documentIssuePlace" },
            row.CodesToComplete);
        Assert.True(summary.DataComplete);
        Assert.False(summary.ExportReady);
        Assert.Equal(Enum.GetValues<AlloggiatiCodeTable>(), summary.MissingCodeTables);
    }

    [Fact]
    public async Task GetGuestSummary_NoGuestRegisteredAndCompanionsDeclared_ReturnsBookerAsHeadOfFamilyWithMissingFields()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db, numberOfGuests: 3, completeGuest: false);

        var summary = await CreateService(db, CheckIn.AddDays(-1)).GetGuestSummaryAsync(booking.Id);

        Assert.Equal(3, summary.DeclaredGuests);
        var row = Assert.Single(summary.Guests);
        Assert.Null(row.StayGuestId);
        Assert.Equal(StayGuestType.HeadOfFamily, row.Type);
        Assert.Equal(
            new[] { "gender", "dateOfBirth", "bornInItaly", "citizenship", "documentType", "documentNumber", "documentIssuePlace" },
            row.MissingFields);
        // A head of family needs the lines of the family members.
        Assert.Equal("head_without_members", row.CompositionIssue);
        Assert.False(summary.DataComplete);
        Assert.False(summary.ExportReady);
    }

    [Fact]
    public async Task IsStayDataComplete_EveryGuestOfTheStay_IsChecked()
    {
        await using var db = CreateDb();
        var complete = await SeedBookingAsync(db);
        var bookerOnly = await SeedBookingAsync(db, numberOfGuests: 3, completeGuest: false);
        var service = CreateService(db, CheckIn.AddDays(-1));

        Assert.True(await service.IsStayDataCompleteAsync(complete.Id));
        Assert.False(await service.IsStayDataCompleteAsync(bookerOnly.Id));
        Assert.False(await service.IsStayDataCompleteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetStatus_RegisteredCompleteGuest_IsDataComplete()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db);

        var status = await CreateService(db, CheckIn.AddDays(-1)).GetStatusAsync(booking.Id);

        Assert.True(status.DataComplete);
    }

    [Fact]
    public async Task GetGuestSummary_StayLongerThan30Days_FlagsThePortalLimit()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db, checkOut: CheckIn.AddDays(31));

        var summary = await CreateService(db, CheckIn.AddDays(-1)).GetGuestSummaryAsync(booking.Id);

        Assert.Equal(31, summary.StayDays);
        Assert.True(summary.StayExceedsMaxDays);
    }

    [Fact]
    public async Task GuestProgressFrom_CompleteGuestAndCompanionsDeclared_CountsOneCompleteOfThreeDeclared()
    {
        // Arrange: MO-08, the counts of the app booking detail ("1 ospite completo su 3").
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db, numberOfGuests: 3);
        var summary = await CreateService(db, CheckIn.AddDays(-1)).GetGuestSummaryAsync(booking.Id);

        // Act
        var progress = AlloggiatiGuestProgressDto.From(summary);

        // Assert: the two companions are declared but not registered yet.
        Assert.Equal(booking.Id, progress.BookingId);
        Assert.Equal(3, progress.DeclaredGuests);
        Assert.Equal(1, progress.RegisteredGuests);
        Assert.Equal(1, progress.CompleteGuests);
        Assert.Equal(summary.DataComplete, progress.DataComplete);
        Assert.False(progress.StayExceedsMaxDays);
    }

    [Fact]
    public async Task GuestProgressFrom_BookerOnlyWithMissingFields_CountsNoCompleteGuest()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db, numberOfGuests: 2, completeGuest: false);
        var summary = await CreateService(db, CheckIn.AddDays(-1)).GetGuestSummaryAsync(booking.Id);

        var progress = AlloggiatiGuestProgressDto.From(summary);

        Assert.Equal(2, progress.DeclaredGuests);
        Assert.Equal(1, progress.RegisteredGuests);
        Assert.Equal(0, progress.CompleteGuests);
        Assert.False(progress.DataComplete);
    }

    [Fact]
    public async Task GuestProgressFrom_HeadOfFamilyWithoutMembers_IsNotCountedComplete()
    {
        // Arrange: every field of the line is there, but a head of family needs the lines of the family members.
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db, numberOfGuests: 2);
        var line = await db.StayGuests.SingleAsync(g => g.BookingId == booking.Id);
        line.Type = StayGuestType.HeadOfFamily;
        await db.SaveChangesAsync();
        var summary = await CreateService(db, CheckIn.AddDays(-1)).GetGuestSummaryAsync(booking.Id);

        // Act
        var progress = AlloggiatiGuestProgressDto.From(summary);

        // Assert
        Assert.Empty(Assert.Single(summary.Guests).MissingFields);
        Assert.Equal(1, progress.RegisteredGuests);
        Assert.Equal(0, progress.CompleteGuests);
        Assert.False(progress.DataComplete);
    }

    [Fact]
    public async Task GuestProgressFrom_StayLongerThan30Days_FlagsThePortalLimit()
    {
        await using var db = CreateDb();
        var booking = await SeedBookingAsync(db, checkOut: CheckIn.AddDays(31));
        var summary = await CreateService(db, CheckIn.AddDays(-1)).GetGuestSummaryAsync(booking.Id);

        Assert.True(AlloggiatiGuestProgressDto.From(summary).StayExceedsMaxDays);
    }

    [Fact]
    public async Task GetSummary_OtherOrgBookings_AreNotListed()
    {
        await using var db = CreateDb();
        var mine = await SeedBookingAsync(db);
        await SeedBookingAsync(db);

        var rows = await CreateService(db, CheckIn).GetSummaryAsync(mine.OrgId, null);

        Assert.Equal(mine.Id, Assert.Single(rows).BookingId);
    }

    private static DateTime Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AlloggiatiWebService CreateService(AppDbContext db, DateTime utcNow) =>
        new(db, NullLogger<AlloggiatiWebService>.Instance, new FixedTimeProvider(new DateTimeOffset(utcNow, TimeSpan.Zero)));

    private static Guest CompleteGuest(Guid orgId) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = orgId,
        FirstName = "Mario",
        LastName = "Rossi",
        Email = $"{Guid.NewGuid():N}@example.com",
        Gender = Gender.Male,
        DateOfBirth = Utc(1980, 4, 2),
        PlaceOfBirth = "Milano (MI)",
        Nationality = "Italiana",
        DocumentType = GuestDocumentType.IdentityCard,
        DocumentNumber = "CA12345AB",
        DocumentIssuingCountry = "Comune di Milano",
    };

    private static async Task<Booking> SeedBookingAsync(
        AppDbContext db,
        DateTime? checkIn = null,
        DateTime? checkOut = null,
        DateTime? arrivedAt = null,
        BookingStatus status = BookingStatus.Confirmed,
        int numberOfGuests = 1,
        bool completeGuest = true)
    {
        var orgId = Guid.NewGuid();
        var guest = completeGuest
            ? CompleteGuest(orgId)
            : new Guest { Id = Guid.NewGuid(), OrgId = orgId, FirstName = "Anna", LastName = "Bianchi", Email = "anna@example.com" };
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = "Casa Test",
            Address = "Via Roma 1",
            City = "Roma",
            PostalCode = "00100",
        };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = property.Id,
            OrgId = orgId,
            GuestId = guest.Id,
            CheckInDate = checkIn ?? CheckIn,
            CheckOutDate = checkOut ?? CheckOut,
            ArrivedAt = arrivedAt,
            Status = status,
            Source = BookingSource.Direct,
            NumberOfGuests = numberOfGuests,
        };
        db.Guests.Add(guest);
        db.Properties.Add(property);
        db.Bookings.Add(booking);
        if (completeGuest)
        {
            // CO-12: the stay's guest line, as registered by the guest portal.
            db.StayGuests.Add(new StayGuest
            {
                BookingId = booking.Id,
                OrgId = orgId,
                GuestId = guest.Id,
                Position = 0,
                Type = StayGuestType.SingleGuest,
                FirstName = "Mario",
                LastName = "Rossi",
                Gender = Gender.Male,
                DateOfBirth = Utc(1980, 4, 2),
                BornInItaly = true,
                BirthComuneName = "Milano",
                BirthProvince = "MI",
                CitizenshipName = "Italia",
                DocumentType = GuestDocumentType.IdentityCard,
                DocumentNumber = "CA12345AB",
                DocumentIssuePlaceName = "Milano",
            });
        }

        await db.SaveChangesAsync();
        return booking;
    }

    private static async Task AddReportAsync(
        AppDbContext db,
        Booking booking,
        AlloggiatiWebStatus status,
        string? jobId = null,
        DateTime? scheduledFor = null,
        DateTime? reportedAt = null,
        string? receipt = null)
    {
        db.AlloggiatiWebReports.Add(new AlloggiatiWebReport
        {
            BookingId = booking.Id,
            GuestId = booking.GuestId,
            OrgId = booking.OrgId,
            Status = status,
            ScheduledJobId = jobId,
            ScheduledFor = scheduledFor,
            ReportedAt = reportedAt,
            ConfirmationNumber = receipt,
        });
        await db.SaveChangesAsync();
    }
}
