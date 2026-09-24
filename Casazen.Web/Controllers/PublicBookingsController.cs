using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/bookings")]
[AllowAnonymous]
public class PublicBookingsController(
    IBookingService bookingService,
    IOnSiteBookingRequestService onSiteRequests,
    ILogger<PublicBookingsController> logger,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    [HttpGet("property/{propertyId}/availability")]
    [EnableRateLimiting(RateLimitPolicies.PublicRead)]
    public async Task<ActionResult<PropertyAvailabilityResponse>> GetPropertyAvailability(
        Guid propertyId,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null)
    {
        var today = _clock.TodayInRome();
        var start = startDate ?? today;
        var end = endDate ?? today.AddDays(365);

        var bookings = await bookingService.GetCalendarAsync(propertyId, start, end);

        var bookedDates = bookings
            .Where(b => b.Status != BookingStatus.Cancelled)
            .SelectMany(b => GetDateRange(b.CheckInDate, b.CheckOutDate))
            .ToHashSet();

        return Ok(new PropertyAvailabilityResponse
        {
            PropertyId = propertyId,
            StartDate = start,
            EndDate = end,
            BookedDates = bookedDates.OrderBy(d => d).ToList(),
        });
    }

    private static IEnumerable<string> GetDateRange(DateTime checkIn, DateTime checkOut)
    {
        var current = checkIn.Date;
        while (current < checkOut.Date)
        {
            yield return current.ToString("yyyy-MM-dd");
            current = current.AddDays(1);
        }
    }

    [HttpGet("{bookingId}/status")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    public async Task<ActionResult<BookingStatusResponse>> GetBookingStatus(Guid bookingId)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        if (booking is null)
            return NotFound();

        return Ok(new BookingStatusResponse(
            booking.Id,
            booking.Status,
            booking.PaymentOption));
    }

    [HttpPost("lookup")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    public async Task<ActionResult<GuestBookingLookupResponse>> LookupGuestBookings(
        [FromBody] GuestBookingLookupRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var booking = await bookingService.GetBookingAsync(request.BookingId!.Value);
        if (booking is null ||
            booking.Status == BookingStatus.Cancelled ||
            !string.Equals(booking.Guest.Email, request.Email, StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new GuestBookingLookupResponse([]));
        }

        return Ok(new GuestBookingLookupResponse([
            new GuestBookingItem(
                booking.Id,
                booking.Property.Name,
                booking.Property.City,
                booking.CheckInDate,
                booking.CheckOutDate,
                booking.Status,
                booking.PaymentOption,
                booking.FreeRefundDeadline ?? booking.CheckInDate.AddDays(-7))
        ]));
    }

    /// <summary>
    /// Price of a stay before booking (BK-03, A3-02, R-05): lodging, cleaning and the tourist tax computed by the only
    /// tourist tax engine, from the <c>TouristTaxRates</c> of the property's comune. The booking created afterwards
    /// records the same amounts. A comune without rate answers 200 with <c>touristTax.status = RateUnavailable</c>
    /// (tax not included, checkout not blocked); <c>ChildAgesRequired</c> asks the ages of the minors.
    /// </summary>
    [HttpPost("quote")]
    [EnableRateLimiting(RateLimitPolicies.PublicRead)]
    [ProducesResponseType(typeof(DirectBookingQuoteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DirectBookingQuoteResponse>> Quote(
        [FromBody] DirectBookingQuoteRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        try
        {
            var quote = await bookingService.QuoteDirectBookingAsync(
                new DirectBookingQuoteInput(
                    request.PropertyId,
                    request.CheckInDate,
                    request.CheckOutDate,
                    request.NumberOfAdults,
                    request.NumberOfChildren,
                    request.ChildrenAges),
                cancellationToken);
            return Ok(DirectBookingQuoteResponse.From(quote));
        }
        catch (DirectBookingException ex)
        {
            logger.LogInformation("Direct booking quote rejected: {ErrorCode}", ex.ErrorCode);
            return ex.ErrorCode switch
            {
                DirectBookingErrorCodes.TooManyGuests => this.ApiProblem(
                    StatusCodes.Status422UnprocessableEntity,
                    BookingErrorCodes.TooManyGuests,
                    "BookingTooManyGuests",
                    ex.MessageArgs),
                DirectBookingErrorCodes.InvalidDates => this.ApiProblem(
                    StatusCodes.Status422UnprocessableEntity,
                    BookingErrorCodes.InvalidDates,
                    "BookingInvalidDates"),
                _ => this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound"),
            };
        }
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingCreate)]
    public async Task<ActionResult<DirectBookingResponse>> CreateDirectBooking(
        [FromBody] CreateDirectBookingRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (request.Consent is null || !request.Consent.DataProcessing)
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest,
                DirectBookingProblemCodes.ConsentRequired,
                "DirectBookingConsentRequired");
        }

        var consentIp = ClientIp.GetString(HttpContext) ?? string.Empty;
        var guest = request.Guest;

        try
        {
            var result = await bookingService.CreateDirectBookingAsync(new DirectBookingCreateInput(
                request.PropertyId,
                request.CheckInDate,
                request.CheckOutDate,
                request.NumberOfAdults,
                request.NumberOfChildren,
                new DirectBookingGuestInput(
                    guest.FirstName,
                    guest.LastName,
                    guest.Email,
                    guest.Phone,
                    guest.Country),
                request.Consent.ConsentVersion,
                consentIp,
                request.SpecialRequests,
                request.PaymentOption,
                request.ChildrenAges));

            return Ok(new DirectBookingResponse
            {
                BookingId = result.BookingId,
                ClientSecret = result.PaymentOption == PaymentOption.Immediate ? result.ClientSecret : string.Empty,
                SetupIntentClientSecret = result.SetupIntentClientSecret,
                ConnectedAccountPublishableContext = new ConnectedAccountPublishableContext
                {
                    PublishableKey = result.PublishableKey,
                    StripeAccountId = result.StripeAccountId,
                },
                Amount = result.Amount,
                Currency = result.Currency,
                TouristTaxAmount = result.TouristTaxAmount,
                BasePrice = result.BasePrice,
                FreeRefundDeadline = result.FreeRefundDeadline ?? DateTime.UtcNow,
                PaymentOption = result.PaymentOption,
                TouristTaxStatus = result.TouristTaxStatus,
                EmailConfirmationExpiresAt = result.OnSiteRequestExpiresAt,
            });
        }
        catch (DirectBookingException ex)
        {
            logger.LogWarning(ex, "Direct booking rejected: {ErrorCode}", ex.ErrorCode);
            return DirectBookingProblem(ex);
        }
    }

    /// <summary>
    /// The guest confirms the email of a "pay at the property" request with the token of the link (BK-06, D5, A3-06): only
    /// then the request goes to the host, who accepts or declines it. Idempotent (a second click answers the current
    /// state). 404 <c>onsite_request_link_invalid</c> for a wrong link, 409 <c>onsite_request_expired</c> when the time to
    /// confirm has passed.
    /// </summary>
    [HttpPost("{bookingId:guid}/confirm-email")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(OnSiteRequestConfirmationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OnSiteRequestConfirmationResponse>> ConfirmOnSiteRequestEmail(
        Guid bookingId,
        [FromBody] ConfirmOnSiteRequestEmailRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var snapshot = await onSiteRequests.ConfirmGuestEmailAsync(bookingId, request.Token, cancellationToken);
        return Ok(OnSiteRequestConfirmationResponse.From(snapshot));
    }

    /// <summary>
    /// Public checkout errors as ProblemDetails with a stable code and a localized message (R-11): the guest reads why
    /// the booking failed (e.g. the host has not enabled payments yet) instead of a generic "checkout failed".
    /// </summary>
    private ObjectResult DirectBookingProblem(DirectBookingException ex) => ex.ErrorCode switch
    {
        DirectBookingErrorCodes.PropertyNotFound => this.ApiProblem(
            StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound"),
        DirectBookingErrorCodes.PaymentNotReady => this.ApiProblem(
            StatusCodes.Status409Conflict, DirectBookingProblemCodes.PaymentsNotReady, "DirectBookingPaymentsNotReady"),
        DirectBookingErrorCodes.NotAvailable => this.ApiProblem(
            StatusCodes.Status409Conflict, BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable"),
        DirectBookingErrorCodes.TooManyGuests => this.ApiProblem(
            StatusCodes.Status422UnprocessableEntity, BookingErrorCodes.TooManyGuests, "BookingTooManyGuests", ex.MessageArgs),
        DirectBookingErrorCodes.InvalidDates => this.ApiProblem(
            StatusCodes.Status422UnprocessableEntity, DirectBookingProblemCodes.InvalidStay, "DirectBookingInvalidStay"),
        DirectBookingErrorCodes.InvalidConsentVersion => this.ApiProblem(
            StatusCodes.Status422UnprocessableEntity, DirectBookingProblemCodes.ConsentOutdated, "DirectBookingConsentOutdated"),
        DirectBookingErrorCodes.InvalidPaymentOption => this.ApiProblem(
            StatusCodes.Status422UnprocessableEntity,
            DirectBookingProblemCodes.InvalidPaymentOption,
            "DirectBookingInvalidPaymentOption"),
        DirectBookingErrorCodes.ChildAgesRequired => this.ApiProblem(
            StatusCodes.Status422UnprocessableEntity, DirectBookingErrorCodes.ChildAgesRequired, "TouristTaxChildAgesRequired"),
        DirectBookingErrorCodes.StripeError => this.ApiProblem(
            StatusCodes.Status503ServiceUnavailable, ProblemCodes.PaymentProviderError, "PaymentProviderUnavailableDetail"),
        _ => this.ApiProblem(
            StatusCodes.Status422UnprocessableEntity, ProblemCodes.BusinessRuleViolation, "BusinessRuleViolationDetail"),
    };
}
