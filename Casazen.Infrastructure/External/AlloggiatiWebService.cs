using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Alloggiati Web communications, honest state (CO-11, decision D6). CasaZen has no client of the Alloggiati web
/// service yet (CO-13): nothing is transmitted, so a communication is never marked
/// <see cref="AlloggiatiWebStatus.Inviato"/> here. On the arrival day (Europe/Rome) it becomes
/// <see cref="AlloggiatiWebStatus.DaInviareManualmente"/>: the host sends it on the Questura portal using the
/// per-guest summary, then declares it with <see cref="MarkSentManuallyAsync"/>.
/// </summary>
/// <remarks>
/// The report is per booking: one communication with a line per guest of the stay (<see cref="StayGuest"/>, CO-12). It is
/// keyed by the booker's guest record (the "slot"): a report whose booker was replaced by a snapshot of the same guest
/// is moved to the new guest record, never duplicated.
/// </remarks>
public class AlloggiatiWebService(
    AppDbContext context,
    ILogger<AlloggiatiWebService> logger,
    TimeProvider? timeProvider = null,
    IStayGuestService? stayGuestService = null,
    IAlloggiatiCodeTableService? codeTableService = null) : IAlloggiatiWebService
{
    /// <summary>Error code of a sent date before the check-in date.</summary>
    public const string ManualDateBeforeArrivalCode = "alloggiati_manual_date_before_arrival";

    /// <summary>Error code of a sent date after today (Europe/Rome).</summary>
    public const string ManualDateInFutureCode = "alloggiati_manual_date_in_future";

    /// <summary>Error code of a communication already sent or declared sent.</summary>
    public const string AlreadySentCode = "alloggiati_already_sent";

    /// <summary>
    /// A report still waiting for its job this long after the scheduled time is considered lost (e.g. Hangfire
    /// storage reset) and can be scheduled again.
    /// </summary>
    public static readonly TimeSpan LostJobGrace = TimeSpan.FromHours(1);

    private static readonly BookingStatus[] ActiveBookingStatuses =
    [
        BookingStatus.Confirmed,
        BookingStatus.CheckedIn,
        BookingStatus.CheckedOut,
    ];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private readonly IAlloggiatiCodeTableService _codeTables =
        codeTableService ?? new AlloggiatiCodeTableService(context, NullLogger<AlloggiatiCodeTableService>.Instance);

    private readonly IStayGuestService _stayGuests = stayGuestService
        ?? new StayGuestService(context, codeTableService ?? new AlloggiatiCodeTableService(context, NullLogger<AlloggiatiCodeTableService>.Instance), timeProvider);

    public async Task<bool> IsStayDataCompleteAsync(Guid bookingId)
    {
        var booking = await context.Bookings.AsNoTracking().Include(b => b.Guest).FirstOrDefaultAsync(b => b.Id == bookingId);
        return booking is not null && await IsDataCompleteAsync(booking);
    }

    public async Task<AlloggiatiStatusInfo> GetStatusAsync(Guid bookingId)
    {
        var booking = await LoadBookingAsync(bookingId, tracking: false);
        var report = SlotReport(await ReportsOfAsync(bookingId, tracking: false), booking.GuestId);
        return BuildStatusInfo(booking, report, await IsDataCompleteAsync(booking));
    }

    public async Task<IReadOnlyList<AlloggiatiSummaryInfo>> GetSummaryAsync(Guid orgId, Guid? propertyId)
    {
        var query = context.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Include(b => b.Property)
            .Where(b => b.OrgId == orgId && ActiveBookingStatuses.Contains(b.Status));

        if (propertyId.HasValue)
            query = query.Where(b => b.PropertyId == propertyId.Value);

        var bookings = await query
            .OrderBy(b => b.CheckInDate)
            .ToListAsync();

        var bookingIds = bookings.Select(b => b.Id).ToList();
        var reports = (await context.AlloggiatiWebReports
                .AsNoTracking()
                .Where(r => bookingIds.Contains(r.BookingId))
                .ToListAsync())
            .ToLookup(r => r.BookingId);

        var guests = await _stayGuests.GetForBookingsAsync(bookings);
        var now = UtcNow();
        var today = _clock.TodayInRome();
        return bookings
            .Select(booking =>
            {
                var report = SlotReport(reports[booking.Id].ToList(), booking.GuestId);
                var status = AlloggiatiStatusRules.Effective(report?.Status, booking.CheckInDate, today);
                var deadline = AlloggiatiTerms.DeadlineUtc(booking);
                return new AlloggiatiSummaryInfo(
                    booking.Id,
                    $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim(),
                    booking.Property.Name,
                    booking.CheckInDate,
                    status,
                    AlloggiatiRecordRules.IsDataComplete(guests[booking.Id]),
                    IsOverdue(deadline, status, now),
                    HoursUntil(deadline, now),
                    deadline,
                    AlloggiatiTerms.IsShortStay(booking.CheckInDate, booking.CheckOutDate));
            })
            .ToList();
    }

    public async Task<AlloggiatiGuestSummaryInfo> GetGuestSummaryAsync(Guid bookingId)
    {
        var booking = await LoadBookingAsync(bookingId, tracking: false);
        var report = SlotReport(await ReportsOfAsync(bookingId, tracking: false), booking.GuestId);
        var status = AlloggiatiStatusRules.Effective(report?.Status, booking.CheckInDate, _clock.TodayInRome());
        var stayDays = AlloggiatiTerms.StayDays(booking.CheckInDate, booking.CheckOutDate);

        // One line per guest of the stay, in record order: a head of family or group before its members (CO-12).
        var guests = await _stayGuests.GetForBookingAsync(booking);
        var codeBook = await _codeTables.LoadCodeBookAsync(guests);
        var compositionIssues = AlloggiatiRecordRules.CompositionErrors(guests.Select(g => g.Type).ToList())
            .GroupBy(e => e.Index)
            .ToDictionary(g => g.Key, g => AlloggiatiRecordRules.CompositionIssueCode(g.First().Kind));

        var rows = guests
            .Select((guest, index) => BuildRow(guest, booking, stayDays, codeBook.ResolveGuest(guest), compositionIssues.GetValueOrDefault(index)))
            .ToList();
        var dataComplete = AlloggiatiRecordRules.IsDataComplete(guests);
        var missingTables = Enum.GetValues<AlloggiatiCodeTable>().Where(t => !codeBook.IsLoaded(t)).ToList();

        return new AlloggiatiGuestSummaryInfo(
            booking.Id,
            status,
            booking.CheckInDate,
            stayDays,
            stayDays > AlloggiatiTerms.MaxStayDaysPerSchedina,
            Math.Max(1, booking.NumberOfGuests),
            rows,
            dataComplete,
            dataComplete && rows.All(r => r.CodesToComplete.Count == 0),
            missingTables);
    }

    private static AlloggiatiGuestRow BuildRow(
        StayGuest guest,
        Booking booking,
        int stayDays,
        StayGuestCodes codes,
        string? compositionIssue)
    {
        var requiresDocument = AlloggiatiRecordRules.RequiresDocument(guest.Type);
        return new AlloggiatiGuestRow(
            guest.Id == Guid.Empty ? null : guest.Id,
            guest.Position,
            guest.Type,
            AlloggiatiRecordRules.IsMinor(guest.DateOfBirth, booking.CheckInDate),
            booking.CheckInDate,
            stayDays,
            guest.LastName,
            guest.FirstName,
            guest.Gender,
            guest.DateOfBirth,
            guest.BornInItaly,
            guest.BornInItaly == true ? guest.BirthComuneName : guest.BornInItaly is null ? guest.BirthComuneName : string.Empty,
            guest.BornInItaly == true ? guest.BirthProvince : null,
            guest.BornInItaly == false ? guest.BirthCountryName : string.Empty,
            guest.CitizenshipName,
            requiresDocument,
            requiresDocument ? guest.DocumentType : null,
            requiresDocument ? guest.DocumentNumber : string.Empty,
            requiresDocument ? guest.DocumentIssuePlaceName : string.Empty,
            new AlloggiatiRowCodes(
                codes.Type.Code,
                codes.BirthComune.Code,
                codes.BirthCountry.Code,
                codes.Citizenship.Code,
                codes.DocumentType.Code,
                codes.DocumentType.Description,
                codes.DocumentIssuePlace.Code),
            AlloggiatiRecordRules.MissingFields(guest),
            codes.CodesToComplete,
            compositionIssue,
            guest.DataSource,
            guest.Id == Guid.Empty ? null : guest.UpdatedAt);
    }

    public async Task<AlloggiatiReportReservation?> ReserveReportAsync(Guid bookingId)
    {
        var booking = await context.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking is null || !ActiveBookingStatuses.Contains(booking.Status))
        {
            logger.LogInformation("Alloggiati report not scheduled: booking {BookingId} missing or not active", bookingId);
            return null;
        }

        var now = UtcNow();
        var arrivalDayStart = AlloggiatiTerms.ArrivalDayStartUtc(booking.CheckInDate);
        var runAt = arrivalDayStart > now ? arrivalDayStart : now;
        var reports = await ReportsOfAsync(bookingId, tracking: true);
        var report = reports.FirstOrDefault(r => r.GuestId == booking.GuestId);

        if (report is null)
        {
            var previous = reports.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
            if (previous is not null)
                return await MoveSlotToGuestAsync(previous, booking, runAt, now);

            report = new AlloggiatiWebReport
            {
                BookingId = bookingId,
                GuestId = booking.GuestId,
                OrgId = booking.OrgId, // the report belongs to its booking's org (TN-2)
                Status = AlloggiatiWebStatus.DaInviare,
                CreatedAt = now,
                UpdatedAt = now,
            };
            context.AlloggiatiWebReports.Add(report);
            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                // Unique (BookingId, GuestId): a concurrent caller reserved the same report and schedules its job.
                context.Entry(report).State = EntityState.Detached;
                logger.LogInformation(ex, "Alloggiati report of booking {BookingId} reserved concurrently", bookingId);
                return null;
            }

            return new AlloggiatiReportReservation(report.Id, bookingId, booking.GuestId, runAt, null);
        }

        if (report.Status != AlloggiatiWebStatus.DaInviare || !NeedsScheduling(report, arrivalDayStart, runAt, now))
            return null;

        return new AlloggiatiReportReservation(report.Id, bookingId, booking.GuestId, runAt, report.ScheduledJobId);
    }

    public async Task SetScheduledJobAsync(Guid reportId, string jobId, DateTime scheduledForUtc)
    {
        var report = await context.AlloggiatiWebReports.FirstOrDefaultAsync(r => r.Id == reportId)
            ?? throw new NotFoundException($"Alloggiati report {reportId} not found");

        report.ScheduledJobId = jobId;
        report.ScheduledFor = scheduledForUtc;
        report.UpdatedAt = UtcNow();
        await context.SaveChangesAsync();
    }

    public async Task<AlloggiatiProcessOutcome> ProcessScheduledReportAsync(Guid bookingId, Guid guestId)
    {
        var booking = await context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking is null)
            return AlloggiatiProcessOutcome.BookingNotFound;

        if (booking.GuestId != guestId)
            return AlloggiatiProcessOutcome.Superseded;

        var report = await context.AlloggiatiWebReports
            .FirstOrDefaultAsync(r => r.BookingId == bookingId && r.GuestId == guestId);
        if (report is null)
            return AlloggiatiProcessOutcome.NotReserved;

        if (report.Status is not (AlloggiatiWebStatus.DaInviare or AlloggiatiWebStatus.Errore))
            return AlloggiatiProcessOutcome.AlreadyHandled;

        if (!ActiveBookingStatuses.Contains(booking.Status))
            return AlloggiatiProcessOutcome.BookingInactive;

        if (!AlloggiatiTerms.IsArrivalDayReached(booking.CheckInDate, _clock.TodayInRome()))
            return AlloggiatiProcessOutcome.NotYetDue;

        // CO-13 will transmit here (Test, then Send) and set Inviato only with the receipt. Until then the host
        // sends it on the portal: honest state, nothing is reported as sent.
        report.Status = AlloggiatiWebStatus.DaInviareManualmente;
        report.ErrorMessage = null;
        report.UpdatedAt = UtcNow();
        await context.SaveChangesAsync();

        logger.LogInformation(
            "Alloggiati report {ReportId} of booking {BookingId} is ready: the host must send it on the portal",
            report.Id, bookingId);
        return AlloggiatiProcessOutcome.MarkedForManualSubmission;
    }

    public async Task<AlloggiatiStatusInfo> MarkSentManuallyAsync(Guid bookingId, DateTime sentOn)
    {
        var booking = await LoadBookingAsync(bookingId, tracking: true);
        var sentDate = sentOn.Date;
        if (sentDate < booking.CheckInDate.Date)
            throw new DomainRuleException(ManualDateBeforeArrivalCode, "AlloggiatiManualDateBeforeArrival");
        if (sentDate > _clock.TodayInRome())
            throw new DomainRuleException(ManualDateInFutureCode, "AlloggiatiManualDateInFuture");

        var now = UtcNow();
        var reports = await ReportsOfAsync(bookingId, tracking: true);
        var report = SlotReport(reports, booking.GuestId);
        if (report is not null && AlloggiatiStatusRules.IsSent(report.Status))
            throw new DomainConflictException(AlreadySentCode, "AlloggiatiAlreadySent");

        if (report is null)
        {
            report = new AlloggiatiWebReport
            {
                BookingId = bookingId,
                GuestId = booking.GuestId,
                OrgId = booking.OrgId,
                CreatedAt = now,
            };
            context.AlloggiatiWebReports.Add(report);
        }

        report.GuestId = booking.GuestId;
        report.Status = AlloggiatiWebStatus.InviatoManualmente;
        report.ManuallyCompleted = true;
        report.ReportedAt = sentDate;
        report.ErrorMessage = null;
        report.UpdatedAt = now;
        await context.SaveChangesAsync();

        logger.LogInformation("Alloggiati report of booking {BookingId} declared sent manually by the host", bookingId);
        return BuildStatusInfo(booking, report, await IsDataCompleteAsync(booking));
    }

    public bool IsOverdue(Booking booking, AlloggiatiWebStatus? reportStatus) =>
        IsOverdue(AlloggiatiTerms.DeadlineUtc(booking), reportStatus ?? AlloggiatiWebStatus.DaInviare, UtcNow());

    private async Task<AlloggiatiReportReservation?> MoveSlotToGuestAsync(
        AlloggiatiWebReport previous,
        Booking booking,
        DateTime runAt,
        DateTime now)
    {
        // Sent (or declared sent) with the data of the earlier guest record: the booking's communication is done.
        if (AlloggiatiStatusRules.IsSent(previous.Status))
            return null;

        previous.GuestId = booking.GuestId;
        previous.UpdatedAt = now;
        await context.SaveChangesAsync();

        logger.LogInformation(
            "Alloggiati report {ReportId} of booking {BookingId} moved to the booking's current guest record",
            previous.Id, booking.Id);

        // A job still to run carries the old guest and would stop as superseded: schedule one for the new guest.
        return previous.Status == AlloggiatiWebStatus.DaInviare
            ? new AlloggiatiReportReservation(previous.Id, booking.Id, booking.GuestId, runAt, previous.ScheduledJobId)
            : null;
    }

    private static bool NeedsScheduling(AlloggiatiWebReport report, DateTime arrivalDayStart, DateTime runAt, DateTime now) =>
        string.IsNullOrEmpty(report.ScheduledJobId)
        || report.ScheduledFor is not { } scheduledFor
        || scheduledFor > runAt // the check-in date moved earlier
        || scheduledFor < arrivalDayStart // the check-in date moved later: the job would run too early
        || scheduledFor < now - LostJobGrace; // the job never ran

    private async Task<bool> IsDataCompleteAsync(Booking booking) =>
        AlloggiatiRecordRules.IsDataComplete(await _stayGuests.GetForBookingAsync(booking));

    private async Task<Booking> LoadBookingAsync(Guid bookingId, bool tracking)
    {
        var query = context.Bookings.Include(b => b.Guest).AsQueryable();
        if (!tracking)
            query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(b => b.Id == bookingId)
            ?? throw new NotFoundException($"Booking {bookingId} not found");
    }

    private async Task<List<AlloggiatiWebReport>> ReportsOfAsync(Guid bookingId, bool tracking)
    {
        var query = context.AlloggiatiWebReports.Where(r => r.BookingId == bookingId);
        if (!tracking)
            query = query.AsNoTracking();
        return await query.ToListAsync();
    }

    /// <summary>The report of the booking's guest, or else the latest report of the booking (same guest slot).</summary>
    private static AlloggiatiWebReport? SlotReport(IReadOnlyCollection<AlloggiatiWebReport> reports, Guid guestId) =>
        reports.FirstOrDefault(r => r.GuestId == guestId)
        ?? reports.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();

    private AlloggiatiStatusInfo BuildStatusInfo(Booking booking, AlloggiatiWebReport? report, bool dataComplete)
    {
        var now = UtcNow();
        var status = AlloggiatiStatusRules.Effective(report?.Status, booking.CheckInDate, _clock.TodayInRome());
        var deadline = AlloggiatiTerms.DeadlineUtc(booking);
        return new AlloggiatiStatusInfo(
            booking.Id,
            status,
            status == AlloggiatiWebStatus.Inviato ? report?.ConfirmationNumber : null,
            AlloggiatiStatusRules.IsFailure(status) ? report?.ErrorMessage : null,
            AlloggiatiStatusRules.IsSent(status) ? report?.ReportedAt : null,
            deadline,
            AlloggiatiTerms.IsShortStay(booking.CheckInDate, booking.CheckOutDate),
            HoursUntil(deadline, now),
            IsOverdue(deadline, status, now),
            dataComplete);
    }

    private static bool IsOverdue(DateTime deadline, AlloggiatiWebStatus status, DateTime now) =>
        now > deadline && !AlloggiatiStatusRules.IsSent(status);

    private static double HoursUntil(DateTime deadline, DateTime now) =>
        Math.Max(0, (deadline - now).TotalHours);

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
