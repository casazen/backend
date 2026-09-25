using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;

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
    IStringLocalizer<SharedResources> localizer,
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
    /// Alloggiati communication is scheduled. Incomplete guest data do not block the arrival:
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
    /// booking whose arrival was never registered is checked in first ("registra arrivo e procedi"). Returns the 5
    /// steps (CO-17) with the progress saved: opened again, on the web or in the app, the wizard resumes where it was.
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

        var state = await complianceWizard.StartCheckoutWizardAsync(
            id,
            request?.RegisterArrival ?? false,
            cancellationToken);
        return Ok(MapCheckoutWizard(state));
    }

    /// <summary>
    /// The check-out wizard of a stay without any change (CO-17): after the check-out it shows what the host declared,
    /// and whether the property is still to be declared ready.
    /// </summary>
    [HttpGet("{id:guid}/checkout-wizard")]
    [ProducesResponseType(typeof(CheckoutWizardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckoutWizardDto>> GetCheckoutWizard(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanAccessAsync(id, BookingOperations.Read, cancellationToken))
            return BookingNotFound();

        return Ok(MapCheckoutWizard(await complianceWizard.GetCheckoutWizardAsync(id, cancellationToken)));
    }

    /// <summary>
    /// Saves the progress of the check-out wizard (CO-17): the step the host is on and the answers given so far. Nothing
    /// is created or declared until <c>/complete</c>. 409 <c>checkout_wizard_not_started</c> before the start,
    /// <c>booking_already_checked_out</c> after the completion; 422 <c>checkout_step_invalid</c> for an unknown step.
    /// </summary>
    [HttpPut("{id:guid}/checkout-wizard/progress")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(CheckoutWizardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CheckoutWizardDto>> SaveCheckoutProgress(
        Guid id,
        [FromBody] SaveCheckoutProgressRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (!await CanWriteAsync(id, cancellationToken))
            return BookingNotFound();

        if (!CheckoutWizardSteps.TryParse(request.CurrentStep, out var step))
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity, BookingErrorCodes.CheckoutStepInvalid, "CheckoutStepInvalid");
        }

        var state = await complianceWizard.SaveCheckoutProgressAsync(
            id,
            new StayCheckoutProgress(
                step,
                request.DepartureConfirmed,
                request.CleaningChoice,
                request.SupplierOrgId,
                request.ServiceCategory,
                request.ServiceNotes,
                request.TouristTaxCollection,
                request.PropertyReady,
                request.PropertyNotes),
            cancellationToken);
        return Ok(MapCheckoutWizard(state));
    }

    /// <summary>
    /// Completes the check-out wizard, once started (409 <c>checkout_wizard_not_started</c>): the guest left
    /// (<c>confirmDeparture</c>), the cleaning request to the chosen supplier is created for the stay or skipped, the
    /// tourist tax collection and the property readiness are recorded as declared; same rules and transition as
    /// <c>POST /check-out</c>. <c>propertyReady</c> is true only when the host declared it.
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
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (!await CanWriteAsync(id, cancellationToken))
            return BookingNotFound();

        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var state = await complianceWizard.CompleteCheckoutWizardAsync(
            id,
            userId,
            new CompleteCheckoutWizardInput(
                request.ConfirmDeparture,
                request.SupplierOrgId,
                request.ServiceNotes,
                request.ServiceCategory,
                request.RegisterArrival)
            {
                CleaningChoice = request.CleaningChoice,
                TouristTaxCollection = request.TouristTaxCollection,
                PropertyReady = request.PropertyReady,
                PropertyNotes = request.PropertyNotes,
            },
            cancellationToken);

        return Ok(new CompleteCheckoutWizardResponse
        {
            PropertyReady = state.PropertyReady,
            BookingStatus = state.Booking.Status.ToString(),
            ServiceRequestId = state.Checkout?.CleaningRequestId,
            Wizard = MapCheckoutWizard(state),
        });
    }

    /// <summary>
    /// The host declares ready the property of a stay already checked out (the turnover left open in the cockpit,
    /// CO-17). Idempotent. 409 <c>checkout_not_completed</c> before the check-out.
    /// </summary>
    [HttpPost("{id:guid}/checkout-wizard/property-ready")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(CheckoutWizardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CheckoutWizardDto>> ConfirmPropertyReady(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConfirmPropertyReadyRequest? request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (!await CanWriteAsync(id, cancellationToken))
            return BookingNotFound();

        var state = await complianceWizard.ConfirmPropertyReadyAsync(id, request?.Notes, cancellationToken);
        return Ok(MapCheckoutWizard(state));
    }

    private CheckoutWizardDto MapCheckoutWizard(CheckoutWizardState state)
    {
        var booking = state.Booking;
        var checkout = state.Checkout;
        var checkedOut = booking.Status == BookingStatus.CheckedOut;
        return new CheckoutWizardDto
        {
            BookingId = booking.Id,
            BookingStatus = booking.Status.ToString(),
            CurrentStep = CheckoutWizardSteps.IdOf(state.CurrentStep),
            StartedAt = booking.CheckoutWizardStartedAt,
            CompletedAt = checkout?.CompletedAt,
            Steps = state.Steps.Select(s => new ComplianceActivationStepDto
            {
                Id = s.Id,
                Label = s.LabelKey is null ? s.Label : localizer[s.LabelKey].Value,
                Status = s.Status,
                Blocker = s.Blocker,
                Message = s.MessageKey is null
                    ? s.Message
                    : localizer[s.MessageKey, s.MessageArgs?.ToArray() ?? []].Value,
            }).ToList(),
            Stay = new CheckoutStayDto
            {
                GuestName = $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim(),
                PropertyId = booking.PropertyId,
                PropertyName = booking.Property.Name,
                PropertyCity = booking.Property.City,
                CheckInDate = booking.CheckInDate,
                CheckOutDate = booking.CheckOutDate,
                Nights = Math.Max(0, (booking.CheckOutDate.Date - booking.CheckInDate.Date).Days),
                NumberOfGuests = booking.NumberOfGuests,
                NumberOfAdults = booking.NumberOfAdults,
                NumberOfChildren = booking.NumberOfChildren,
                ArrivedAt = booking.ArrivedAt,
                Source = booking.Source.ToString(),
                DepartureConfirmed = checkedOut || checkout?.DepartureConfirmed == true,
            },
            Alloggiati = new CheckoutAlloggiatiDto
            {
                Status = state.Alloggiati.Status,
                Sent = state.Alloggiati.Sent,
                DeadlineAt = state.Alloggiati.DeadlineAt,
                IsOverdue = state.Alloggiati.IsOverdue,
                DataComplete = state.Alloggiati.DataComplete,
            },
            Cleaning = new CheckoutCleaningDto
            {
                Choice = checkout?.CleaningChoice,
                SupplierOrgId = checkout?.CleaningSupplierOrgId,
                Category = checkout?.CleaningCategory,
                Notes = checkout?.CleaningNotes,
                RequestId = checkout?.CleaningRequestId,
            },
            TouristTax = new CheckoutTouristTaxDto
            {
                RecordedAmount = state.TouristTax.RecordedAmount,
                CollectedWithOnlinePayment = state.TouristTax.CollectedWithOnlinePayment,
                Collection = state.TouristTax.Collection,
            },
            PropertyReady = new CheckoutPropertyReadyDto
            {
                Ready = checkedOut ? state.PropertyReady : checkout?.PropertyReady,
                ReadyAt = checkout?.PropertyReadyAt,
                Notes = checkout?.PropertyNotes,
            },
        };
    }

    /// <summary>TN-3: the booking is visible (tenant filter) and the caller has <c>booking.write</c> on its property.</summary>
    private Task<bool> CanWriteAsync(Guid bookingId, CancellationToken cancellationToken) =>
        CanAccessAsync(bookingId, BookingOperations.Write, cancellationToken);

    /// <summary>TN-3: the booking is visible (tenant filter) and the caller has <paramref name="operation"/> on its property.</summary>
    private async Task<bool> CanAccessAsync(
        Guid bookingId,
        HostOperationRequirement operation,
        CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        if (booking is null)
            return false;

        var property = await hostResources.ForPropertyAsync(booking.PropertyId, cancellationToken);
        if (property is null)
            return false;

        if (await authorizationService.IsAuthorizedAsync(User, property with { OrgId = booking.OrgId }, operation))
            return true;

        logger.LogWarning(
            "User {UserId} denied {Permission} on booking {BookingId}", User.GetUserId(), operation.PermissionKey, bookingId);
        return false;
    }

    private ObjectResult BookingNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");
}
