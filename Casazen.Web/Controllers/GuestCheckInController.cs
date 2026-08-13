using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.DTOs.CheckIn;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/checkin")]
[AllowAnonymous]
[EnableRateLimiting("GuestCheckIn")]
public class GuestCheckInController(
    IBookingRepository bookingRepository,
    IGuestRepository guestRepository,
    IAlloggiatiWebService alloggiatiWebService,
    IGuestDocumentStorage documentStorage,
    IConfiguration configuration,
    AppDbContext db,
    ILogger<GuestCheckInController> logger) : ControllerBase
{
    private static bool IsTokenExpired(Booking booking) =>
        booking.CheckInTokenExpiresAt.HasValue && booking.CheckInTokenExpiresAt.Value < DateTime.UtcNow;

    private static bool IsBookingEligibleForCheckIn(Booking booking) =>
        booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn && !IsTokenExpired(booking);

    [HttpGet("{token:guid}")]
    public async Task<ActionResult<CheckInContextDto>> GetContext(Guid token)
    {
        var booking = await bookingRepository.GetByCheckInTokenAsync(token);
        if (booking is null || !IsBookingEligibleForCheckIn(booking))
            return NotFound();

        var dataComplete = await alloggiatiWebService.ValidateGuestDataAsync(booking.GuestId);
        return Ok(MapContext(booking, dataComplete));
    }

    [HttpPost("{token:guid}/guest-data")]
    public async Task<ActionResult<GuestCheckInDataResponse>> SubmitGuestData(
        Guid token,
        [FromBody] SubmitGuestCheckInRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (!request.ConsentAccepted)
        {
            return BadRequest(new
            {
                error = "Consent required",
                message = "Data processing consent is required for Alloggiati registration.",
            });
        }

        var booking = await bookingRepository.GetByCheckInTokenAsync(token);
        if (booking is null || !IsBookingEligibleForCheckIn(booking))
            return NotFound();

        var guest = await guestRepository.GetByIdAsync(booking.GuestId);
        if (guest is null)
            return NotFound();

        var now = DateTime.UtcNow;
        guest = await EnsureBookingOwnsMutableGuestAsync(booking, guest, now);

        guest.DateOfBirth = DateTime.SpecifyKind(request.DateOfBirth!.Value.Date, DateTimeKind.Utc);
        guest.PlaceOfBirth = request.PlaceOfBirth;
        guest.Nationality = request.Nationality;
        guest.Gender = request.Gender;
        guest.DocumentType = request.DocumentType;
        guest.DocumentNumber = request.DocumentNumber;
        guest.DocumentExpiryDate = request.DocumentExpiryDate.HasValue
            ? DateTime.SpecifyKind(request.DocumentExpiryDate.Value.Date, DateTimeKind.Utc)
            : null;
        guest.DocumentIssuingCountry = request.DocumentIssuingCountry;
        guest.Address = request.Address;
        guest.City = request.City;
        guest.PostalCode = request.PostalCode;
        guest.Country = request.Country;

        var consentVersion = configuration["CheckIn:ConsentVersion"] ?? "2026-06-alloggiati-checkin-v1";
        var consentIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        guest.ConsentDate = now;
        guest.ConsentVersion = consentVersion;
        guest.DataProcessingConsentDate = now;
        guest.ConsentIpAddress = consentIp.Length > 50 ? consentIp[..50] : consentIp;
        guest.DataProcessingPurpose = "Alloggiati Web guest registration (TULPS Art. 109)";
        guest.DataRetentionUntil = now.AddYears(7);
        guest.UpdatedAt = now;

        await db.SaveChangesAsync();

        var dataComplete = await alloggiatiWebService.ValidateGuestDataAsync(guest.Id);
        logger.LogInformation(
            "Guest check-in data submitted for booking {BookingId}, complete={Complete}",
            booking.Id, dataComplete);

        return Ok(new GuestCheckInDataResponse { DataComplete = dataComplete });
    }

    [HttpPost("{token:guid}/document")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<GuestDocumentUploadResponse>> UploadDocument(
        Guid token,
        IFormFile file)
    {
        var booking = await bookingRepository.GetByCheckInTokenAsync(token);
        if (booking is null || !IsBookingEligibleForCheckIn(booking))
            return NotFound();

        if (!documentStorage.ValidateDocument(file))
            return BadRequest(new { error = "Invalid file", message = "Accepted formats: JPG, PNG, PDF (max 5MB)." });

        var guest = await guestRepository.GetByIdAsync(booking.GuestId);
        if (guest is null)
            return NotFound();

        var now = DateTime.UtcNow;
        guest = await EnsureBookingOwnsMutableGuestAsync(booking, guest, now);
        var url = await documentStorage.UploadDocumentAsync(file, booking.OrgId, guest.Id);
        guest.DocumentScanUrl = url;
        guest.UpdatedAt = now;
        await db.SaveChangesAsync();

        return Ok(new GuestDocumentUploadResponse { DocumentScanUrl = url });
    }

    private async Task<Guest> EnsureBookingOwnsMutableGuestAsync(Booking booking, Guest guest, DateTime now)
    {
        var guestIsShared = await db.Bookings.AnyAsync(b => b.GuestId == guest.Id && b.Id != booking.Id);
        if (!guestIsShared)
            return guest;

        var snapshot = guest.CreateSnapshot(now);
        db.Guests.Add(snapshot);
        booking.GuestId = snapshot.Id;
        booking.Guest = snapshot;

        logger.LogInformation(
            "Created guest snapshot {SnapshotGuestId} for legacy check-in booking {BookingId} from shared guest {GuestId}",
            snapshot.Id, booking.Id, guest.Id);

        return snapshot;
    }

    private static CheckInContextDto MapContext(Booking booking, bool dataComplete) =>
        new()
        {
            BookingId = booking.Id,
            GuestId = booking.GuestId,
            PropertyName = booking.Property.Name,
            CheckInDate = booking.CheckInDate,
            CheckOutDate = booking.CheckOutDate,
            DataComplete = dataComplete,
            Guest = new CheckInGuestDto
            {
                FirstName = booking.Guest.FirstName,
                LastName = booking.Guest.LastName,
                Email = booking.Guest.Email,
                DateOfBirth = booking.Guest.DateOfBirth,
                PlaceOfBirth = booking.Guest.PlaceOfBirth,
                Nationality = booking.Guest.Nationality,
                Gender = booking.Guest.Gender,
                DocumentType = booking.Guest.DocumentType,
                DocumentNumber = booking.Guest.DocumentNumber,
                DocumentExpiryDate = booking.Guest.DocumentExpiryDate,
                DocumentIssuingCountry = booking.Guest.DocumentIssuingCountry,
                Address = booking.Guest.Address,
                City = booking.Guest.City,
                PostalCode = booking.Guest.PostalCode,
                Country = booking.Guest.Country,
                DocumentScanUrl = booking.Guest.DocumentScanUrl,
            },
        };
}
