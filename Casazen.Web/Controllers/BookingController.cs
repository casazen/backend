using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BookingsController(IBookingService bookingService, ILogger<BookingsController> logger) : ControllerBase
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
        var isAvailable = await bookingService.IsPropertyAvailableAsync(
            booking.PropertyId, 
            booking.CheckInDate, 
            booking.CheckOutDate
        );

        if (!isAvailable)
            return BadRequest("Property not available for these dates");

        var created = await bookingService.CreateBookingAsync(booking);
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
        var existing = await bookingService.GetBookingAsync(id);
        if (existing == null)
            return NotFound();

        await bookingService.CancelBookingAsync(id);
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

        booking.Status = BookingStatus.CheckedIn;
        await bookingService.UpdateBookingAsync(booking);
        return Ok(booking);
    }

    [HttpPost("{id}/check-out")]
    public async Task<IActionResult> CheckOut(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        if (booking == null)
            return NotFound();

        booking.Status = BookingStatus.CheckedOut;
        await bookingService.UpdateBookingAsync(booking);
        return Ok(booking);
    }
}
