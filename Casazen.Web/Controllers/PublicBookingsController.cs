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
    ICheckoutOutcomeService checkoutOutcomes,
    IGuestBookingLookupService guestBookings,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Nights already taken on the public booking site (BK-05, A3-09, A2-13, A9-39), from <paramref name="startDate"/>
    /// (included, default today in Europe/Rome) to <paramref name="endDate"/> (excluded, default one year later): the
    /// same rule that refuses a booking (bookings, holds within their time, "pay at the property" requests waiting for the
    /// host, iCal and manual blocks), so a free night can be booked and a taken one answers 409. Dates only. 404
    /// <c>public_property_not_found</c> for a property that does not exist or is not published; 422
    /// <c>availability_range_invalid</c> for an empty range or one longer than a year; 429 per client IP.
    /// </summary>
    [HttpGet("property/{propertyId:guid}/availability")]
    [EnableRateLimiting(RateLimitPolicies.PublicRead)]
    [ProducesResponseType(typeof(PropertyAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PropertyAvailabilityResponse>> GetPropertyAvailability(
        Guid propertyId,
        [FromServices] IPublicAvailabilityService publicAvailability,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken cancellationToken = default)
    {
        var start = startDate ?? _clock.TodayInRome();
        var end = endDate ?? start.Date.AddDays(365);

        var availability = await publicAvailability.GetAsync(propertyId, start, end, cancellationToken);
        return Ok(PropertyAvailabilityResponse.From(availability));
    }

    /// <summary>
    /// Outcome of a public checkout for its outcome page (BK-07, A3-15): the real state of the booking (confirmed, payment
    /// in progress or failed, "pay at the property" request waiting, expired…), polled by the page until it is final. Needs
    /// the checkout token returned by <c>POST /api/public/bookings</c>: the booking id alone reveals nothing, and a wrong
    /// id or token gets the same 404 <c>checkout_link_invalid</c>. No personal data of the guest in the answer.
    /// (Replaces <c>GET /api/public/bookings/{id}/status</c>, which answered for any booking id.)
    /// </summary>
    [HttpPost("{bookingId:guid}/outcome")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(CheckoutOutcomeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckoutOutcomeResponse>> GetCheckoutOutcome(
        Guid bookingId,
        [FromBody] CheckoutTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var outcome = await checkoutOutcomes.GetOutcomeAsync(bookingId, request.Token, cancellationToken);
        return Ok(CheckoutOutcomeResponse.From(outcome));
    }

    /// <summary>
    /// The guest completes the payment of the same hold again (BK-07, A3-15): after a failed card, a redirect method that
    /// came back failed, or a page closed before paying. Answers the client secret of the booking's own PaymentIntent /
    /// SetupIntent, so the guest never books again and hits their own hold (409 on the dates). 409
    /// <c>checkout_hold_expired</c> once the dates were released, 409 <c>checkout_payment_not_resumable</c> when there is
    /// nothing left to pay (already paid or being paid, confirmed, cancelled, pay at the property).
    /// </summary>
    [HttpPost("{bookingId:guid}/payment-session")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(CheckoutPaymentSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CheckoutPaymentSessionResponse>> ResumeCheckoutPayment(
        Guid bookingId,
        [FromBody] CheckoutTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var session = await checkoutOutcomes.ResumePaymentAsync(bookingId, request.Token, cancellationToken);
        return Ok(CheckoutPaymentSessionResponse.From(session));
    }

    /// <summary>
    /// "Le mie prenotazioni" (BK-11, A3-10, R-06): the booking of this site with the booking code of the confirmation email
    /// and the email the booking was made with; state, stay, amounts, online check-in and the host's contact, no personal
    /// data. A code that does not exist, a code of another site and a wrong email all get the same 404
    /// <c>guest_booking_not_found</c>. Rate limited per client IP (<see cref="RateLimitPolicies.PublicGuestBookingLookup"/>)
    /// and per email (<see cref="GuestBookingEmailRateLimiter"/>): 429 <c>rate_limited</c>.
    /// </summary>
    [HttpPost("lookup")]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [GuestBookingEmailRateLimit]
    [ProducesResponseType(typeof(GuestBookingLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestBookingLookupResponse>> LookupGuestBooking(
        [FromBody] GuestBookingLookupRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var booking = await guestBookings.FindAsync(Credentials(request), cancellationToken);
        return Ok(GuestBookingLookupResponse.From(booking));
    }

    /// <summary>
    /// "Le mie prenotazioni": emails the online check-in link (CO-02) again to the address of the booking, with the same
    /// code + email check and limits as the lookup. 202 when queued; 404 <c>guest_booking_not_found</c>; 409
    /// <c>guest_check_in_link_unavailable</c> when the check-in is not open (not confirmed, too early, completed, over).
    /// The link is never in the answer: only the guest's mailbox receives it.
    /// </summary>
    [HttpPost("lookup/check-in-link")]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [GuestBookingEmailRateLimit]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SendGuestCheckInLink(
        [FromBody] GuestBookingLookupRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        await guestBookings.SendCheckInLinkAsync(Credentials(request), cancellationToken);
        return Accepted();
    }

    private static GuestBookingCredentials Credentials(GuestBookingLookupRequest request) =>
        new(request.OrgSlug, request.BookingCode, request.Email);

    /// <summary>
    /// Price of a stay before booking (BK-03, A3-02, R-05): lodging, cleaning and the tourist tax computed by the only
    /// tourist tax engine, from the <c>TouristTaxRates</c> of the property's comune. The booking created afterwards
    /// records the same amounts. A comune without rate answers 200 with <c>touristTax.status = RateUnavailable</c>
    /// (tax not included, checkout not blocked); <c>ChildAgesRequired</c> asks the ages of the minors.
    /// <c>paymentOptions</c> says whether "Paga alla scadenza" can be offered and whether a free cancellation can be
    /// promised (A3-16). Errors: 422 <c>booking_too_many_guests</c>, <c>direct_booking_invalid_stay</c>; 404.
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

    /// <summary>
    /// Public checkout (spec-direct-checkout, BK-06, BK-07). Every error is a ProblemDetails with a stable code and a
    /// localized message (R-11): 400 <c>direct_booking_consent_required</c>; 404 <c>not_found</c>; 409
    /// <c>booking_dates_unavailable</c>, <c>direct_booking_payments_not_ready</c>; 422 <c>direct_booking_invalid_stay</c>,
    /// <c>booking_too_many_guests</c>, <c>direct_booking_consent_outdated</c>, <c>direct_booking_invalid_payment_option</c>,
    /// <c>direct_booking_deferred_payment_unavailable</c>, <c>tourist_tax_child_ages_required</c>,
    /// <c>onsite_request_too_many_nights</c>; 503 <c>payment_provider_error</c>. The answer carries the
    /// <c>checkoutToken</c> of the outcome page (BK-07): the only time it is given.
    /// </summary>
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
                DirectBookingErrorCodes.ConsentRequired,
                "DirectBookingConsentRequired");
        }

        var consentIp = ClientIp.GetString(HttpContext) ?? string.Empty;
        var guest = request.Guest;

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
            CheckoutToken = result.CheckoutToken,
        });
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
}
