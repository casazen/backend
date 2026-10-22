using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The booking the host enters by hand with only what it needs (SR-06a): the name and the email of the guest, the stay and, when the
/// host agreed another price, that price with a short note. It sits next to <c>POST /api/bookings</c>, which asks for the whole
/// contact of the guest and is unchanged. Nothing is sent to the guest and no payment is started.
/// </summary>
/// <remarks>
/// TN-3: the tenant filter hides a property of another org, and a visible one still needs <c>booking.write</c> on it (owner or org-wide
/// role): both are 404, as for the host quote. Errors are ProblemDetails with the codes of <see cref="BookingErrorCodes"/> and
/// <see cref="OtaStayErrorCodes"/> (FD-05).
/// </remarks>
[ApiController]
[Route("api/bookings/manual")]
[Authorize(Policy = CasazenPolicies.BookingWrite)]
public class ManualBookingsController(
    IManualBookingService manualBookings,
    IBookingService bookingService,
    IAlloggiatiWebService alloggiatiWebService,
    IPropertyService propertyService,
    IAuthorizationService authorizationService,
    ILogger<ManualBookingsController> logger) : ControllerBase
{
    /// <summary>
    /// Creates the booking. By direct contact (no <c>blockId</c>) it is <c>Confirmed</c> with the source <c>Manual</c>, priced by the quote
    /// of the host (nightly rates, weekend surcharge, cleaning, tourist tax of the comune): <c>totalPrice</c>, when it is not the quote's,
    /// is the price the guest pays, the tourist tax stays the quote's and the lodging is what is left (0 to 100,000 EUR), with
    /// <c>priceNote</c> (80 characters) on it. A stay shorter than the minimum stay of the property is created all the same:
    /// <c>belowMinimum</c> says it. With <c>blockId</c> it completes the reservation a portal sent as a block of its calendar (same
    /// dates, no second booking, no payment: the portal collects) as an OTA stay. A portal's reservation with no block is not created by
    /// hand. 201 with the booking, <c>belowMinimum</c>, <c>minNights</c> and <c>guestDataComplete</c> (false: the guest completes the
    /// data with the online check-in). 404 for the property or the block; 409 <c>booking_dates_unavailable</c> and the codes of the
    /// block; 422 for a rule of the stay, <c>booking_price_override_invalid</c>, <c>booking_block_dates_mismatch</c>.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ManualBookingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ManualBookingResponse>> Create(
        [FromBody] CreateManualBookingRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var property = await propertyService.GetPropertyAsync(request.PropertyId);
        if (property is null
            || !await authorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), BookingOperations.Write))
        {
            if (property is not null)
            {
                logger.LogWarning(
                    "User {UserId} denied booking.write on property {PropertyId}", User.GetUserId(), request.PropertyId);
            }

            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound");
        }

        var result = await manualBookings.CreateAsync(
            new ManualBookingInput(
                property,
                request.CheckInDate!.Value,
                request.CheckOutDate!.Value,
                request.Adults,
                request.Children,
                request.ChildrenAges,
                new ManualBookingGuest(
                    request.Guest.FirstName,
                    request.Guest.LastName,
                    request.Guest.Email,
                    request.Guest.Phone,
                    request.Guest.Country),
                request.Note,
                request.TotalPrice,
                request.PriceNote,
                request.BlockId,
                request.Source),
            cancellationToken);

        // The booking as every other answer shows it: with its property and its guest.
        var booking = await bookingService.GetBookingAsync(result.Booking.Id) ?? result.Booking;
        var response = BookingMapper.Fill(new ManualBookingResponse(), booking, DateTime.UtcNow);
        response.BelowMinimum = result.BelowMinimum;
        response.MinNights = result.MinNights;
        response.GuestDataComplete = await alloggiatiWebService.IsStayDataCompleteAsync(booking.Id);
        return Created($"/api/bookings/{booking.Id}", response);
    }
}
