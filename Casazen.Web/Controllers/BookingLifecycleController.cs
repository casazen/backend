using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// What the host does on a booking from the console (PC-07, A2-07, A2-08): price a stay, change a booking, confirm a
/// pending booking entered by hand, check out. Cancellation with refunds is <see cref="BookingCancellationController"/>.
/// TN-3: the tenant filter hides a booking or property of another org (404); on a visible one the caller needs
/// <c>booking.write</c> on its property (owner, or org-wide role), otherwise 404 as well. Domain errors are ProblemDetails
/// with the codes of <see cref="BookingErrorCodes"/> (FD-05).
/// </summary>
[ApiController]
[Route("api/bookings")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class BookingLifecycleController(
    IBookingService bookingService,
    IHostBookingService hostBookings,
    IPropertyService propertyService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<BookingLifecycleController> logger) : ControllerBase
{
    /// <summary>
    /// Price of a stay the host is entering or changing (manual booking): lodging, cleaning and the tourist tax of the
    /// comune (BK-03), computed like the booking will record it. <c>touristTax.ageRulesApply</c> tells the form to ask
    /// for the minors and their ages; <c>ChildAgesRequired</c> means they are missing.
    /// </summary>
    [HttpPost("quote")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(DirectBookingQuoteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DirectBookingQuoteResponse>> Quote(
        [FromBody] HostBookingQuoteRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var property = await propertyService.GetPropertyAsync(request.PropertyId);
        if (property is null ||
            !await authorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), BookingOperations.Write))
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound");

        var quote = await bookingService.PriceHostStayAsync(
            property,
            request.CheckInDate!.Value,
            request.CheckOutDate!.Value,
            request.NumberOfGuests - request.NumberOfChildren,
            request.NumberOfChildren,
            request.ChildrenAges,
            cancellationToken);
        return Ok(DirectBookingQuoteResponse.From(quote));
    }

    /// <summary>
    /// Changes dates, guests (minors and their ages included) and notes of a booking. Replaces the old PUT that bound the
    /// EF entity (A2-07): any other field of the body, such as status or prices, is ignored. Dates and guests change only
    /// for bookings entered by the host, before the check-in; the price is computed again (tourist tax included) and new
    /// dates that overlap another booking or a calendar block answer 409 <c>booking_dates_unavailable</c>.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(BookingResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingResponseDto>> Update(
        Guid id,
        [FromBody] UpdateBookingRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (!await CanWriteAsync(id, cancellationToken))
            return BookingNotFound();

        var updated = await hostBookings.UpdateAsync(
            new HostBookingUpdate(
                id,
                request.CheckInDate!.Value,
                request.CheckOutDate!.Value,
                request.NumberOfGuests,
                request.NumberOfChildren,
                request.ChildrenAges,
                request.SpecialRequests),
            cancellationToken);
        return Ok(BookingMapper.ToResponse(updated));
    }

    /// <summary>
    /// Confirms a pending booking entered by the host (A2-08): the ones the old code stored as Pending. Other pending
    /// bookings answer 422 <c>booking_not_confirmable</c> (a checkout hold is confirmed by the guest's payment); a booking
    /// no longer pending answers 409 <c>booking_not_pending</c>.
    /// </summary>
    [HttpPost("{id:guid}/confirm")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(BookingResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingResponseDto>> Confirm(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return BookingNotFound();

        logger.LogInformation("Host confirming booking {BookingId}", id);
        var confirmed = await hostBookings.ConfirmAsync(id, cancellationToken);
        return Ok(BookingMapper.ToResponse(confirmed));
    }

    /// <summary>
    /// Check-out of a checked-in booking, from its check-out day (Europe/Rome). Only the status changes: the dates of the
    /// stay are not validated again, so a stay of any length can be checked out (A2-08).
    /// </summary>
    [HttpPost("{id:guid}/check-out")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(BookingResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingResponseDto>> CheckOut(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return BookingNotFound();

        var checkedOut = await hostBookings.CheckOutAsync(id, cancellationToken);
        return Ok(BookingMapper.ToResponse(checkedOut));
    }

    /// <summary>TN-3: the booking is visible (tenant filter) and the caller has <c>booking.write</c> on its property.</summary>
    private async Task<bool> CanWriteAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        if (booking is null)
            return false;

        var property = await hostResources.ForPropertyAsync(booking.PropertyId, cancellationToken);
        if (property is null)
            return false;

        if (await authorizationService.IsAuthorizedAsync(User, property with { OrgId = booking.OrgId }, BookingOperations.Write))
            return true;

        logger.LogWarning("User {UserId} denied booking.write on booking {BookingId}", User.GetUserId(), bookingId);
        return false;
    }

    private ObjectResult BookingNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");
}
