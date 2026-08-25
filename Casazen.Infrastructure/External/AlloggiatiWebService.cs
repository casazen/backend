using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

public class AlloggiatiWebService(
    IGuestRepository guestRepository,
    IAlloggiatiWebReportRepository reportRepository,
    IBookingRepository bookingRepository,
    AppDbContext context,
    IConfiguration configuration,
    ILogger<AlloggiatiWebService> logger) : IAlloggiatiWebService
{
    private static readonly HashSet<BookingStatus> ActiveBookingStatuses =
    [
        BookingStatus.Confirmed,
        BookingStatus.CheckedIn,
        BookingStatus.CheckedOut,
    ];

    public async Task ReportGuestAsync(Guid guestId, Guid bookingId)
    {
        var guest = await guestRepository.GetByIdAsync(guestId);
        if (guest == null)
        {
            logger.LogError("Guest {GuestId} not found for Alloggiati Web report", guestId);
            return;
        }

        var existingReport = await reportRepository.GetByBookingIdAsync(bookingId);
        if (existingReport?.Status is AlloggiatiWebStatus.Submitted or AlloggiatiWebStatus.Confirmed)
        {
            logger.LogInformation(
                "Alloggiati Web report for booking {BookingId} already has status {Status}; skipping duplicate submission",
                bookingId, existingReport.Status);
            return;
        }

        var report = existingReport ?? new AlloggiatiWebReport
        {
            GuestId = guestId,
            BookingId = bookingId,
        };
        report.GuestId = guestId;

        if (!await ValidateGuestDataAsync(guestId))
        {
            report.Status = AlloggiatiWebStatus.Failed;
            report.ErrorMessage = "Validation failed: required Alloggiati Web fields missing (DateOfBirth, Nationality, DocumentType, DocumentNumber, Gender)";
            if (existingReport == null)
                await reportRepository.AddAsync(report);
            else
                await reportRepository.UpdateAsync(report);
            logger.LogWarning("Alloggiati Web report validation failed for guest {GuestId}", guestId);
            return;
        }

        try
        {
            var enabled = configuration.GetValue("Alloggiati:Enabled", false);
            logger.LogInformation(
                "Submitting Alloggiati Web report for booking {BookingId}, guest {GuestId}, enabled={Enabled}",
                bookingId, guestId, enabled);

            if (enabled)
            {
                // TODO: HTTP call to Alloggiati Web API once Questura credentials are configured per property.
                report.Status = AlloggiatiWebStatus.Submitted;
            }
            else
            {
                report.Status = AlloggiatiWebStatus.Submitted;
                report.ErrorMessage = "simulated";
            }

            report.ReportedAt = DateTime.UtcNow;

            if (existingReport == null)
                await reportRepository.AddAsync(report);
            else
                await reportRepository.UpdateAsync(report);

            logger.LogInformation("Alloggiati Web report submitted for booking {BookingId}", bookingId);
        }
        catch (Exception ex)
        {
            report.Status = AlloggiatiWebStatus.Failed;
            report.ErrorMessage = ex.Message;
            report.RetryCount++;

            if (existingReport == null)
                await reportRepository.AddAsync(report);
            else
                await reportRepository.UpdateAsync(report);

            logger.LogError(ex, "Alloggiati Web submission failed for booking {BookingId}", bookingId);
            throw;
        }
    }

    public async Task<bool> ValidateGuestDataAsync(Guid guestId)
    {
        var guest = await guestRepository.GetByIdAsync(guestId);
        if (guest == null) return false;

        return guest.DateOfBirth.HasValue
            && !string.IsNullOrEmpty(guest.PlaceOfBirth)
            && !string.IsNullOrEmpty(guest.Nationality)
            && guest.DocumentType.HasValue
            && !string.IsNullOrEmpty(guest.DocumentNumber)
            && !string.IsNullOrEmpty(guest.DocumentIssuingCountry)
            && guest.Gender.HasValue;
    }

    public async Task<AlloggiatiWebReport?> GetReportStatusAsync(Guid bookingId)
    {
        return await reportRepository.GetByBookingIdAsync(bookingId);
    }

    public async Task<AlloggiatiStatusInfo> GetStatusAsync(Guid bookingId)
    {
        var booking = await bookingRepository.GetByIdAsync(bookingId)
            ?? throw new KeyNotFoundException($"Booking {bookingId} not found");

        var report = await reportRepository.GetByBookingIdAsync(bookingId);
        var dataComplete = await ValidateGuestDataAsync(booking.GuestId);
        return BuildStatusInfo(booking, report, dataComplete);
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
        var reports = await context.AlloggiatiWebReports
            .AsNoTracking()
            .Where(r => bookingIds.Contains(r.BookingId))
            .ToDictionaryAsync(r => r.BookingId);

        var summaries = new List<AlloggiatiSummaryInfo>();
        foreach (var booking in bookings)
        {
            reports.TryGetValue(booking.Id, out var report);
            var dataComplete = IsGuestDataComplete(booking.Guest);
            var status = report?.Status ?? AlloggiatiWebStatus.Pending;
            summaries.Add(new AlloggiatiSummaryInfo(
                booking.Id,
                $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim(),
                booking.Property.Name,
                booking.CheckInDate,
                status,
                dataComplete,
                IsOverdue(booking.CheckInDate, dataComplete, status),
                GetHoursUntilDeadline(booking.CheckInDate)));
        }

        return summaries;
    }

    public async Task<AlloggiatiStatusInfo> SendManualAsync(Guid bookingId)
    {
        var booking = await bookingRepository.GetByIdAsync(bookingId)
            ?? throw new KeyNotFoundException($"Booking {bookingId} not found");

        await ReportGuestAsync(booking.GuestId, bookingId);

        var report = await reportRepository.GetByBookingIdAsync(bookingId);
        if (report != null)
        {
            report.ManuallyCompleted = true;
            await reportRepository.UpdateAsync(report);
        }

        var dataComplete = await ValidateGuestDataAsync(booking.GuestId);
        report = await reportRepository.GetByBookingIdAsync(bookingId);
        return BuildStatusInfo(booking, report, dataComplete);
    }

    public double GetHoursUntilDeadline(DateTime checkInDate)
    {
        var deadline = checkInDate.AddHours(24);
        var remaining = (deadline - DateTime.UtcNow).TotalHours;
        return Math.Max(0, remaining);
    }

    public bool IsOverdue(DateTime checkInDate, bool dataComplete, AlloggiatiWebStatus? reportStatus)
    {
        if (DateTime.UtcNow <= checkInDate.AddHours(24))
            return false;

        if (!dataComplete)
            return true;

        return reportStatus is AlloggiatiWebStatus.Failed or AlloggiatiWebStatus.Pending;
    }

    private AlloggiatiStatusInfo BuildStatusInfo(Booking booking, AlloggiatiWebReport? report, bool dataComplete)
    {
        var status = report?.Status ?? AlloggiatiWebStatus.Pending;
        return new AlloggiatiStatusInfo(
            booking.Id,
            status,
            report?.ConfirmationNumber,
            report?.ErrorMessage,
            report?.ReportedAt,
            GetHoursUntilDeadline(booking.CheckInDate),
            IsOverdue(booking.CheckInDate, dataComplete, status),
            dataComplete);
    }

    private static bool IsGuestDataComplete(Guest guest) =>
        guest.DateOfBirth.HasValue
        && !string.IsNullOrEmpty(guest.PlaceOfBirth)
        && !string.IsNullOrEmpty(guest.Nationality)
        && guest.DocumentType.HasValue
        && !string.IsNullOrEmpty(guest.DocumentNumber)
        && !string.IsNullOrEmpty(guest.DocumentIssuingCountry)
        && guest.Gender.HasValue;
}
