using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

public class AlloggiatiWebService(
    IGuestRepository guestRepository,
    IAlloggiatiWebReportRepository reportRepository,
    IConfiguration configuration,
    ILogger<AlloggiatiWebService> logger) : IAlloggiatiWebService
{
    public async Task ReportGuestAsync(Guid guestId, Guid bookingId)
    {
        var guest = await guestRepository.GetByIdAsync(guestId);
        if (guest == null)
        {
            logger.LogError("Guest {GuestId} not found for Alloggiati Web report", guestId);
            return;
        }

        var existingReport = await reportRepository.GetByBookingIdAsync(bookingId);
        var report = existingReport ?? new AlloggiatiWebReport
        {
            GuestId = guestId,
            BookingId = bookingId
        };

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
            // TODO: Implement HTTP call to Alloggiati Web API once credentials obtained from local Questura.
            // API docs: https://alloggiatiweb.poliziadistato.it/
            // Credentials request: contact local Questura with property CIN code.
            logger.LogInformation("Submitting Alloggiati Web report for booking {BookingId}, guest {GuestId}", bookingId, guestId);

            report.Status = AlloggiatiWebStatus.Submitted;
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
}
