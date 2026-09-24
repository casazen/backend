using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Casazen.Web.Controllers;

/// <summary>
/// What the host does on a booking from the console (PC-07, A2-07, A2-08): price a stay, change a booking, register the
/// arrival and check out (CO-08, A5-08: one domain path, <see cref="IStayLifecycleService"/>, for the arrival, the
/// check-out wizard and <c>POST /check-out</c>).
/// Cancellation with refunds is <see cref="BookingCancellationController"/>; the confirmation of a pending booking (entered
/// by hand, or a "pay at the property" request) is the single <c>POST /api/bookings/{id}/approve</c> of
/// <see cref="BookingApprovalController"/>.
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
    IStayLifecycleService stayLifecycle,
    IComplianceWizardService complianceWizard,
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
    /// "Registra arrivo": the host records that the guest arrived. A confirmed booking becomes checked in from its
    /// check-in day to its check-out day (Europe/Rome); a registration on a later day of the stay is not an error. The
    /// Alloggiati communication and the check-out reminder are scheduled. Incomplete guest data do not block the arrival:
    /// <c>guestDataComplete</c> tells the client to send the host to complete them.
    /// 409 <c>booking_already_checked_in</c> / <c>booking_not_confirmed</c>, 422 <c>booking_arrival_too_early</c> /
    /// <c>booking_arrival_after_departure</c>.
    /// </summary>
    [HttpPost("{id:guid}/check-in")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(ArrivalRegisteredResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ArrivalRegisteredResponse>> CheckIn(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return BookingNotFound();

        var arrival = await stayLifecycle.RegisterArrivalAsync(id, cancellationToken);
        var response = BookingMapper.Fill(new ArrivalRegisteredResponse(), arrival.Booking, DateTime.UtcNow);
        response.GuestDataComplete = arrival.GuestDataComplete;
        return Ok(response);
    }

    /// <summary>
    /// Check-out of a stay, with the rules of the check-out wizard (same domain path): a checked-in stay from its
    /// check-in day (an early departure is allowed), or a confirmed booking with <c>registerArrival: true</c>, which
    /// registers the arrival in the same transaction. Only the status changes, not the dates, so a stay of any length can
    /// be checked out (A2-08). 409 <c>booking_arrival_not_registered</c> when the arrival was never registered.
    /// </summary>
    [HttpPost("{id:guid}/check-out")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(BookingResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingResponseDto>> CheckOut(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StayCheckOutRequest? request,
        CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return BookingNotFound();

        var checkedOut = await stayLifecycle.CheckOutAsync(
            id,
            new StayCheckOut(request?.RegisterArrival ?? false),
            cancellationToken);
        return Ok(BookingMapper.ToResponse(checkedOut));
    }

    /// <summary>
    /// Opens the check-out wizard: same rules as <c>POST /check-out</c>. With <c>registerArrival: true</c> a confirmed
    /// booking whose arrival was never registered is checked in first ("registra arrivo e procedi").
    /// </summary>
    [HttpPost("{id:guid}/checkout-wizard/start")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(CheckoutWizardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CheckoutWizardDto>> StartCheckoutWizard(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StayCheckOutRequest? request,
        CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return BookingNotFound();

        var (_, steps) = await complianceWizard.StartCheckoutWizardAsync(
            id,
            request?.RegisterArrival ?? false,
            cancellationToken);
        return Ok(new CheckoutWizardDto
        {
            Steps = steps.Select(s => new ComplianceActivationStepDto
            {
                Id = s.Id,
                Label = s.Label,
                Status = s.Status,
                Blocker = s.Blocker,
                Message = s.Message,
            }),
        });
    }

    /// <summary>
    /// Completes the check-out wizard: the guest left (<c>confirmDeparture</c>), optional turnover request to a supplier,
    /// same rules and transition as <c>POST /check-out</c>.
    /// </summary>
    [HttpPost("{id:guid}/checkout-wizard/complete")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(CompleteCheckoutWizardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CompleteCheckoutWizardResponse>> CompleteCheckoutWizard(
        Guid id,
        [FromBody] CompleteCheckoutWizardRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return BookingNotFound();

        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var (booking, propertyReady) = await complianceWizard.CompleteCheckoutWizardAsync(
            id,
            userId,
            new CompleteCheckoutWizardInput(
                request.ConfirmDeparture,
                request.SupplierOrgId,
                request.ServiceNotes,
                request.ServiceCategory,
                request.RegisterArrival),
            cancellationToken);

        return Ok(new CompleteCheckoutWizardResponse
        {
            PropertyReady = propertyReady,
            BookingStatus = booking.Status.ToString(),
        });
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
