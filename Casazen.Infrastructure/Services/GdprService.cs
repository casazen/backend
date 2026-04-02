using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class GdprService(
    IGuestRepository guestRepository,
    IBookingRepository bookingRepository,
    ILogger<GdprService> logger) : IGdprService
{
    public async Task<bool> RequestDataErasureAsync(Guid guestId, string reason)
    {
        var guest = await guestRepository.GetByIdAsync(guestId);
        if (guest == null)
        {
            logger.LogWarning("Erasure request failed: Guest {GuestId} not found", guestId);
            return false;
        }

        if (guest.ErasureRequested)
        {
            logger.LogInformation("Erasure already requested for guest {GuestId}", guestId);
            return true;
        }

        guest.ErasureRequested = true;
        guest.ErasureRequestedDate = DateTime.UtcNow;
        await guestRepository.UpdateAsync(guest);

        logger.LogInformation(
            "GDPR erasure requested for guest {GuestId} ({Email}). Reason: {Reason}",
            guestId, guest.Email, reason);

        return true;
    }

    public async Task<bool> AnonymizeGuestDataAsync(Guid guestId)
    {
        var guest = await guestRepository.GetByIdAsync(guestId);
        if (guest == null)
        {
            logger.LogWarning("Anonymization failed: Guest {GuestId} not found", guestId);
            return false;
        }

        // Check if there are active bookings
        var activeBookings = await bookingRepository.GetByGuestAsync(guestId);
        var hasActiveBookings = activeBookings.Any(b =>
            b.Status == BookingStatus.Confirmed ||
            b.Status == BookingStatus.CheckedIn ||
            (b.Status == BookingStatus.Pending && b.CheckInDate > DateTime.UtcNow));

        if (hasActiveBookings)
        {
            logger.LogWarning(
                "Cannot anonymize guest {GuestId}: Has active or future bookings",
                guestId);
            throw new InvalidOperationException(
                "Cannot anonymize guest data: Active or future bookings exist. " +
                "Please cancel all bookings before requesting data erasure.");
        }

        // Anonymize personal data
        guest.FirstName = "ANONYMIZED";
        guest.LastName = "ANONYMIZED";
        guest.Email = $"anonymized_{guestId}@deleted.local";
        guest.PhoneNumber = string.Empty;
        guest.Address = string.Empty;
        guest.City = string.Empty;
        guest.PostalCode = string.Empty;
        guest.Country = string.Empty;
        guest.Notes = "Data anonymized per GDPR Article 17";

        // Anonymize Alloggiati Web data
        guest.DateOfBirth = null;
        guest.PlaceOfBirth = string.Empty;
        guest.Nationality = string.Empty;
        guest.DocumentType = string.Empty;
        guest.DocumentNumber = string.Empty;
        guest.DocumentIssueDate = null;
        guest.DocumentExpiryDate = null;
        guest.DocumentIssuingCountry = string.Empty;

        // Clear GDPR consent data
        guest.DataProcessingConsentDate = null;
        guest.MarketingConsentDate = null;
        guest.ConsentIpAddress = string.Empty;

        // Mark as anonymized
        guest.DataAnonymizedDate = DateTime.UtcNow;
        guest.ErasureRequested = true;
        guest.ErasureRequestedDate ??= DateTime.UtcNow;

        await guestRepository.UpdateAsync(guest);

        logger.LogInformation(
            "Guest data anonymized: {GuestId}. GDPR Article 17 compliance completed.",
            guestId);

        return true;
    }

    public async Task<IEnumerable<Guest>> GetPendingErasureRequestsAsync()
    {
        var allGuests = await guestRepository.GetAllAsync();
        return allGuests.Where(g =>
            g.ErasureRequested &&
            g.DataAnonymizedDate == null);
    }

    public async Task<GuestDataExport> ExportGuestDataAsync(Guid guestId)
    {
        var guest = await guestRepository.GetByIdAsync(guestId);
        if (guest == null)
            throw new KeyNotFoundException($"Guest {guestId} not found");

        var bookings = await bookingRepository.GetByGuestAsync(guestId);

        var export = new GuestDataExport
        {
            GuestId = guest.Id,
            Email = guest.Email,
            FirstName = guest.FirstName,
            LastName = guest.LastName,
            DataCollectedAt = guest.CreatedAt,
            Bookings = bookings.Select(b => new BookingSummary
            {
                BookingId = b.Id,
                CheckInDate = b.CheckInDate,
                CheckOutDate = b.CheckOutDate,
                PropertyName = b.Property?.Name ?? "Unknown",
                TotalPrice = b.TotalPrice
            }).ToList(),
            Consents = new GdprConsents
            {
                DataProcessingConsentDate = guest.DataProcessingConsentDate,
                MarketingConsentDate = guest.MarketingConsentDate,
                ConsentIpAddress = guest.ConsentIpAddress
            }
        };

        logger.LogInformation("Guest data exported for {GuestId} ({Email})", guestId, guest.Email);

        return export;
    }
}
