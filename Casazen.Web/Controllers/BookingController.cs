using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "PropertyOwner")]
public class BookingsController(
    IBookingService bookingService,
    ITaxCalculationService taxCalculationService,
    IAlloggiatiWebService alloggiatiWebService,
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
    public async Task<ActionResult<Booking>> Create([FromBody] Booking booking)
    {
        logger.LogInformation("Creating booking for property: {PropertyId}", booking.PropertyId);
        var isAvailable = await bookingService.IsPropertyAvailableAsync(
            booking.PropertyId,
            booking.CheckInDate,
            booking.CheckOutDate
        );

        if (!isAvailable)
        {
            logger.LogWarning("Property not available: {PropertyId} from {CheckIn} to {CheckOut}",
                booking.PropertyId, booking.CheckInDate, booking.CheckOutDate);
            return BadRequest("Property not available for these dates");
        }

        booking.TouristTax = await taxCalculationService.CalculateTouristTaxAsync(
            booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, booking.NumberOfGuests);
        booking.TotalPrice = booking.BasePrice + booking.TouristTax;

        var created = await bookingService.CreateBookingAsync(booking);
        logger.LogInformation("Booking created: {BookingId}, tourist tax: {Tax} EUR", created.Id, created.TouristTax);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
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
    public async Task<ActionResult<IEnumerable<Booking>>> GetCalendar(
        [FromQuery] Guid propertyId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate)
    {
        var bookings = await bookingService.GetCalendarAsync(propertyId, startDate, endDate);
        return Ok(bookings);
    }

    [HttpPost("{id}/check-in")]
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
}
