using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "PropertyOwner")]
[Authorize(Policy = "RequireContext:short-rent:booking.read")]
public class BookingsController(
    IBookingService bookingService,
    ITaxCalculationService taxCalculationService,
    IAlloggiatiWebService alloggiatiWebService,
    IPropertyService propertyService,
    IPropertyAuthorizationService authorizationService,
    IGuestService guestService,
    ILogger<BookingsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Booking>>> GetAll([FromQuery] Guid? propertyId)
    {
        IEnumerable<Booking> bookings;

        if (propertyId.HasValue)
            bookings = await bookingService.GetPropertyBookingsAsync(propertyId.Value);
        else
            bookings = await bookingService.GetAllBookingsAsync();

        return Ok(bookings);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Booking>> GetById(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        return booking == null ? NotFound() : Ok(booking);
    }

    [HttpPost]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<ActionResult<Booking>> Create([FromBody] CreateBookingRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(request.PropertyId);
        if (property == null)
            return NotFound("Property not found");

        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
            return Forbid();

        if (request.NumberOfGuests > property.MaxGuests)
        {
            return BadRequest(new
            {
                error = "Too many guests",
                message = $"This property allows a maximum of {property.MaxGuests} guests."
            });
        }

        var checkIn = request.CheckInDate.Date;
        var checkOut = request.CheckOutDate.Date;
        if (checkOut <= checkIn)
            return BadRequest("Check-out date must be after check-in date");

        logger.LogInformation("Creating booking for property: {PropertyId}", request.PropertyId);

        var isAvailable = await bookingService.IsPropertyAvailableAsync(
            request.PropertyId, checkIn, checkOut);

        if (!isAvailable)
        {
            logger.LogWarning("Property not available: {PropertyId} from {CheckIn} to {CheckOut}",
                request.PropertyId, checkIn, checkOut);
            return BadRequest("Property not available for these dates");
        }

        var guest = await ResolveGuestAsync(request.Guest);
        var nights = (checkOut - checkIn).Days;
        var basePrice = property.NightlyRate * nights + property.CleaningFee;

        var booking = new Booking
        {
            PropertyId = request.PropertyId,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = request.NumberOfGuests,
            SpecialRequests = request.SpecialRequests ?? string.Empty,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            BasePrice = basePrice,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        booking.TouristTax = await taxCalculationService.CalculateTouristTaxAsync(
            booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, booking.NumberOfGuests);
        booking.TotalPrice = booking.BasePrice + booking.TouristTax;

        try
        {
            var created = await bookingService.CreateBookingAsync(booking);
            logger.LogInformation("Booking created: {BookingId}, tourist tax: {Tax} EUR", created.Id, created.TouristTax);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Booking creation failed for property {PropertyId}", request.PropertyId);
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Booking booking)
    {
        var existing = await bookingService.GetBookingAsync(id);
        if (existing == null)
            return NotFound();

        booking.Id = id;
        await bookingService.UpdateBookingAsync(booking);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        logger.LogInformation("Cancelling booking: {BookingId}", id);
        var existing = await bookingService.GetBookingAsync(id);
        if (existing == null)
            return NotFound();

        await bookingService.CancelBookingAsync(id);
        logger.LogInformation("Booking cancelled: {BookingId}", id);
        return NoContent();
    }

    [HttpGet("calendar")]
    public async Task<ActionResult<CalendarResponseDto>> GetCalendar(
        [FromQuery] Guid propertyId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] string? timezone = null)
    {
        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null)
            return NotFound("Property not found");

        var targetTimezone = timezone ?? property.Timezone;

        if (!TimezoneHelper.IsValidTimezone(targetTimezone))
            return BadRequest($"Invalid timezone: {targetTimezone}");

        var startDateUtc = TimezoneHelper.ConvertLocalToUtc(startDate, targetTimezone);
        var endDateUtc = TimezoneHelper.ConvertLocalToUtc(endDate, targetTimezone);

        var bookings = await bookingService.GetCalendarAsync(propertyId, startDateUtc, endDateUtc);

        var utcOffsetMinutes = TimezoneHelper.GetUtcOffsetMinutes(targetTimezone, DateTime.UtcNow);

        var calendarBookings = bookings.Select(b => new CalendarBookingDto
        {
            Id = b.Id,
            PropertyId = b.PropertyId,
            GuestId = b.GuestId,
            CheckInDate = TimezoneHelper.ConvertUtcToLocal(b.CheckInDate, targetTimezone),
            CheckOutDate = TimezoneHelper.ConvertUtcToLocal(b.CheckOutDate, targetTimezone),
            CheckInDateUtc = b.CheckInDate,
            CheckOutDateUtc = b.CheckOutDate,
            Status = b.Status.ToString(),
            Source = b.Source.ToString(),
            NumberOfGuests = b.NumberOfGuests,
            TotalPrice = b.TotalPrice,
            GuestName = b.Guest != null ? $"{b.Guest.FirstName} {b.Guest.LastName}".Trim() : ""
        }).ToList();

        var response = new CalendarResponseDto
        {
            Timezone = targetTimezone,
            UtcOffsetMinutes = utcOffsetMinutes,
            Bookings = calendarBookings
        };

        return Ok(response);
    }

    [HttpPost("{id}/check-in")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<IActionResult> CheckIn(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        if (booking == null)
            return NotFound();

        // Validate status transition
        if (booking.Status != BookingStatus.Confirmed)
        {
            return BadRequest(new
            {
                Error = "Invalid status transition",
                Message = $"Can only check-in bookings with Confirmed status. Current status: {booking.Status}"
            });
        }

        // Validate date
        if (booking.CheckInDate.Date > DateTime.UtcNow.Date)
        {
            return BadRequest(new
            {
                Error = "Check-in date not reached",
                Message = $"Cannot check-in before check-in date: {booking.CheckInDate:yyyy-MM-dd}"
            });
        }

        booking.Status = BookingStatus.CheckedIn;
        await bookingService.UpdateBookingAsync(booking);

        // Mandatory guest registration with police database within 24h (D.L. 286/1998, Art. 7)
        BackgroundJob.Enqueue<AlloggiatiWebReportJob>(
            job => job.ReportGuestAsync(booking.GuestId, booking.Id));

        logger.LogInformation("Check-in completed for booking {BookingId}, queued Alloggiati Web report", id);
        return Ok(booking);
    }

    [HttpPost("{id}/check-out")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<IActionResult> CheckOut(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        if (booking == null)
            return NotFound();

        // Validate status transition
        if (booking.Status != BookingStatus.CheckedIn)
        {
            return BadRequest(new
            {
                Error = "Invalid status transition",
                Message = $"Can only check-out bookings with CheckedIn status. Current status: {booking.Status}"
            });
        }

        // Validate date
        if (booking.CheckOutDate.Date > DateTime.UtcNow.Date)
        {
            return BadRequest(new
            {
                Error = "Check-out date not reached",
                Message = $"Cannot check-out before check-out date: {booking.CheckOutDate:yyyy-MM-dd}"
            });
        }

        booking.Status = BookingStatus.CheckedOut;
        await bookingService.UpdateBookingAsync(booking);
        return Ok(booking);
    }

    [HttpGet("{id}/alloggiati-status")]
    public async Task<IActionResult> GetAlloggiatiStatus(Guid id)
    {
        var report = await alloggiatiWebService.GetReportStatusAsync(id);
        return report == null ? NotFound() : Ok(report);
    }

    private async Task<Guest> ResolveGuestAsync(CreateBookingGuestRequest guestInfo)
    {
        var existing = await guestService.GetGuestByEmailAsync(guestInfo.Email);
        if (existing != null)
            return existing;

        var guest = new Guest
        {
            FirstName = guestInfo.FirstName,
            LastName = guestInfo.LastName,
            Email = guestInfo.Email,
            PhoneNumber = guestInfo.Phone,
            Country = guestInfo.Country,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        try
        {
            return await guestService.CreateGuestAsync(guest);
        }
        catch (InvalidOperationException)
        {
            var raced = await guestService.GetGuestByEmailAsync(guestInfo.Email);
            if (raced == null)
                throw;
            return raced;
        }
    }

    private string? GetUserId() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

    private IEnumerable<string> GetUserRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value);
}
