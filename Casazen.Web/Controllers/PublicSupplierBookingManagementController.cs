using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The customer's own area of a booking made from a supplier's public showcase (SP-11, decision D34 revised), for the customer who
/// booked with no account: with the code of the booking and the e-mail address it was made with, find the booking again, cancel it,
/// move it to another time while the supplier has not answered, and answer a time the supplier proposed. Anonymous, behind the
/// feature flag <c>SupplierShowcaseBooking</c> (404 while it is off), never indexable and never cached. Runbook
/// <c>docs/runbooks/suppliers.md</c> section 24.
/// </summary>
/// <remarks>
/// <para><b>The code and the address are in the body, never in the URL</b> (so they are in no access log), as the slug of the
/// supplier. Nothing a customer typed is written to a log.</para>
/// <para><b>One 404 for everything that does not identify a booking</b> — <c>supplier_booking_not_found</c>, same status, same body:
/// a supplier that does not exist, a code that does not exist or is not a code, the code of another supplier's booking, an address
/// that is not the one of the booking. Every attempt runs the same statements, so the time it takes tells nothing either. The
/// limits are the same 429 for all: per IP (<see cref="RateLimitPolicies.PublicGuestBookingLookup"/>) and per address and supplier
/// (<see cref="SupplierBookingManageEmailRateLimiter"/>), which always names the whole window.</para>
/// <para><b>Concurrency.</b> Every change is saved only if nobody changed the request since it was read (<c>xmin</c>): the customer and
/// the supplier acting at the same moment, one wins and the other gets 409 <c>service_request_state_changed</c>; a time that is not
/// free is 409 <c>supplier_slot_unavailable</c>. Never a 500.</para>
/// </remarks>
[ApiController]
[Route("api/public/supplier-bookings")]
[AllowAnonymous]
[PrivateAnswer]
public class PublicSupplierBookingManagementController(IShowcaseBookingManager bookings) : ControllerBase
{
    /// <summary>
    /// Finds the booking again: its status, service, time, price and lines, place (the comune; the exact address only after the
    /// supplier took the request), supplier, the other time the supplier proposed (with the deadline to answer it), and what the
    /// customer can do (<c>canCancel</c>, <c>canReschedule</c>, <c>canRespondToProposal</c>). 404 <c>supplier_booking_not_found</c>;
    /// 400 <c>validation_error</c> for a body without slug, code or address; 429 <c>rate_limited</c>.
    /// </summary>
    [HttpPost("lookup")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> Lookup(
        [FromBody] SupplierBookingAccessRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.LookupAsync(request.ToCredentials(), ct)));
    }

    /// <summary>
    /// Cancels the booking (a new request any time; a taken one until its time), with an optional <c>reason</c> of at most 500
    /// characters. Free until <c>Suppliers:Showcase:FreeCancellationHours</c> (24) before the work, and without charge after it
    /// (decision D6). The time is free again; the supplier is told and the customer gets a receipt. Answers the booking as it is now;
    /// a second call of a booking the customer already cancelled answers the same and does nothing. 404
    /// <c>supplier_booking_not_found</c>; 422 <c>supplier_booking_cannot_cancel</c> (in progress, done, refused, cancelled by another
    /// party, or its time has passed), <c>supplier_booking_invalid</c> (<c>fields</c>: <c>reason</c>); 409
    /// <c>service_request_state_changed</c> (the supplier acted at the same moment); 429.
    /// </summary>
    [HttpPost("cancel")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> Cancel(
        [FromBody] SupplierBookingCancelRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.CancelAsync(request.ToCredentials(), request.Reason, ct)));
        }
        catch (ShowcaseBookingRuleException ex)
        {
            return RuleProblem(ex.Code, ex.MessageKey, ex.MessageArgs, ex.Fields);
        }
    }

    /// <summary>
    /// Moves a new request to another slot of the same service (<c>startUtc</c> exactly as <c>GET …/slots</c> gave it): the old time
    /// is free again, a time the supplier had proposed is dropped, the supplier has its whole time to answer again and is told. Only
    /// while the request is <c>Richiesto</c>. The time it already has changes nothing. 404 <c>supplier_booking_not_found</c>; 422
    /// <c>supplier_booking_cannot_reschedule</c>, <c>supplier_booking_supplier_unavailable</c>, <c>supplier_booking_invalid</c>
    /// (<c>fields</c>: <c>startUtc</c>); 409 <c>supplier_slot_unavailable</c> (not free, or no slot can start then),
    /// <c>service_request_state_changed</c>; 429.
    /// </summary>
    [HttpPost("reschedule")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> Reschedule(
        [FromBody] SupplierBookingRescheduleRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(PublicSupplierBookingViewMapper.ToDto(
                await bookings.RescheduleAsync(request.ToCredentials(), request.StartUtc!.Value, ct)));
        }
        catch (ShowcaseBookingRuleException ex)
        {
            return RuleProblem(ex.Code, ex.MessageKey, ex.MessageArgs, ex.Fields);
        }
    }

    /// <summary>
    /// Accepts the time the supplier proposed: the request is taken on the supplier's behalf at that time, the slot is checked
    /// again, and both are told. 404 <c>supplier_booking_not_found</c>; 422 <c>supplier_booking_no_proposal</c>,
    /// <c>supplier_booking_proposal_expired</c>, <c>supplier_booking_supplier_unavailable</c>; 409 <c>supplier_slot_unavailable</c>
    /// (the proposed time is gone: the proposal stays, the customer can turn it down or cancel), <c>service_request_state_changed</c>;
    /// 429.
    /// </summary>
    [HttpPost("proposal/accept")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> AcceptProposal(
        [FromBody] SupplierBookingAccessRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.AcceptProposalAsync(request.ToCredentials(), ct)));
    }

    /// <summary>
    /// Turns the proposed time down: the proposal goes, the request stays <c>Richiesto</c> at its time, the supplier has its whole
    /// time to answer again and is told. Errors as <c>proposal/accept</c>, but for the slot.
    /// </summary>
    [HttpPost("proposal/reject")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> RejectProposal(
        [FromBody] SupplierBookingAccessRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.RejectProposalAsync(request.ToCredentials(), ct)));
    }

    /// <summary>
    /// Accepts the quote the supplier sent (SP-04b): the request is taken on the supplier's behalf at the time of the quote (the booking's
    /// own unless the quote offered another), with the total of the quote as the price agreed; the time is checked again, and both are
    /// told. The body carries <c>quoteSentAt</c> as <c>quote.sentAt</c> of the booking gave it. 404 <c>supplier_booking_not_found</c>; 422
    /// <c>supplier_booking_no_quote</c>, <c>supplier_booking_quote_expired</c>, <c>supplier_booking_supplier_unavailable</c>; 409
    /// <c>service_request_quote_changed</c> (the supplier replaced the quote: read it again), <c>supplier_slot_unavailable</c> (the time is
    /// gone: the quote stays, the customer can turn it down or cancel), <c>service_request_state_changed</c>; 429.
    /// </summary>
    [HttpPost("quote/accept")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> AcceptQuote(
        [FromBody] SupplierBookingQuoteAnswerRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.AcceptQuoteAsync(request.ToCredentials(), request.QuoteSentAt!.Value, ct)));
    }

    /// <summary>
    /// Turns the quote down (SP-04b): the quote goes, the request stays <c>Richiesto</c> at its time, the supplier has its whole time to
    /// answer again and is told. Errors as <c>quote/accept</c>, but for the time.
    /// </summary>
    [HttpPost("quote/reject")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> RejectQuote(
        [FromBody] SupplierBookingQuoteAnswerRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.RejectQuoteAsync(request.ToCredentials(), request.QuoteSentAt!.Value, ct)));
    }

    /// <summary>
    /// Accepts the offer the supplier sent for the customer's request on measure (SP-10b-b): the request is taken on the supplier's behalf at the time
    /// and the total of the offer, the time is checked again under the supplier's calendar lock, and both are told. The body carries
    /// <c>offerSentAt</c> as <c>offer.sentAt</c> of the booking gave it. Any booking that is not a request on measure is the same 404 as one that does not
    /// exist. <b>When the offer is paid inside CasaZen</b> (<c>offer.paymentRequired</c>; the payments flag on, the supplier able to receive them) the
    /// request is <b>not</b> taken by this call: the answer is the booking still <c>Richiesto</c> with <c>negotiation: "AwaitingPayment"</c>, <c>payment.status:
    /// "awaiting_card"</c> and <c>paymentSession</c> (<c>clientSecret</c>, <c>publishableKey</c>, <c>amountCents</c>) — the Payment Element blocks the card
    /// for the total, then the request is taken (the server does it when Stripe reports the block; this call made again also does it, and answers
    /// <c>PresoInCarico</c>). The page polls <c>lookup</c> or calls this again until <c>status</c> is <c>PresoInCarico</c>; asked again before the card
    /// is blocked it returns the same session. The card is charged only when the request is taken, never for a time that is gone: then nothing is
    /// charged and the block is released. 404 <c>supplier_booking_not_found</c>; 422 <c>supplier_booking_no_offer</c>, <c>supplier_booking_offer_expired</c>,
    /// <c>supplier_booking_supplier_unavailable</c>; 409 <c>service_request_offer_changed</c> (the supplier replaced or withdrew the offer: read it
    /// again), <c>supplier_slot_unavailable</c> / <c>supplier_commitment_conflict</c> (the time is gone: the offer stays, the customer can turn it down,
    /// write to the supplier or cancel), <c>payment_authorization_expired</c> / <c>payment_capture_failed</c> (the block lapsed or the card could not be
    /// charged: nothing was charged, ask for the card again), <c>service_payment_in_flight</c> (money of an earlier attempt is being looked at),
    /// <c>service_request_state_changed</c> (two acceptances, or the supplier acted at the same moment: one wins and nothing is done twice); 503
    /// <c>payment_provider_error</c> (the card could not be asked for now; nothing was saved); 429.
    /// </summary>
    [HttpPost("offer/accept")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> AcceptOffer(
        [FromBody] SupplierBookingOfferAnswerRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.AcceptOfferAsync(request.ToCredentials(), request.OfferSentAt!.Value, ct)));
    }

    /// <summary>
    /// Turns the offer down (SP-10b-b): the offer goes, the request stays <c>Richiesto</c> and the conversation goes on (<c>negotiation</c> is
    /// <c>OfferDeclined</c> until the supplier sends another; no clock runs on the supplier), and the supplier is told. Errors as <c>offer/accept</c>,
    /// but for the time.
    /// </summary>
    [HttpPost("offer/decline")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> DeclineOffer(
        [FromBody] SupplierBookingOfferAnswerRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.DeclineOfferAsync(request.ToCredentials(), request.OfferSentAt!.Value, ct)));
    }

    /// <summary>
    /// SP-15c-a, "Tutto a posto": the customer says the work is fine, and the money CasaZen holds for a booking paid inside CasaZen goes to the
    /// supplier at once, without waiting for the hour after the work. Same proof of who is asking as the other actions of this area, same limits,
    /// same uniform 404. Answers the booking as it is now (<c>payment</c> and <c>actions.canConfirmOk</c> included); asking again once the supplier was
    /// paid answers the same and does nothing. If the transfer cannot be made right now the customer's word stands and the job makes it. 404
    /// <c>supplier_booking_not_found</c>; 422 <c>service_payment_not_confirmable</c> (the booking is not paid inside CasaZen, the work is not done, or
    /// the money is closed or stopped); 429.
    /// </summary>
    [HttpPost("confirm-ok")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> ConfirmOk(
        [FromBody] SupplierBookingAccessRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.ConfirmOkAsync(request.ToCredentials(), ct)));
    }

    /// <summary>
    /// SP-15c-c, "Segnala un problema": the customer reports a problem with a job paid inside CasaZen and the money CasaZen holds for it stops until a
    /// person decides (decisions VT-D1 and VT-D16). Possible from the moment the work is completed until the money is due to the supplier
    /// (<c>payment.releaseAtUtc</c>), never after the customer said "all fine" or the supplier was paid; for a job nobody marked completed, from the end
    /// of the time agreed for it. The body carries <c>reason</c> (<c>not_done</c>, <c>incomplete</c>, <c>poor_quality</c>, <c>not_as_described</c> or
    /// <c>other</c>) and <c>text</c> (at most 1000 characters, required for <c>other</c>; no photos). Same proof of who is asking as the other actions,
    /// same limits, same uniform 404. Answers the booking as it is now (<c>payment.dispute</c> and <c>actions.canWithdrawProblem</c> included); a complaint
    /// already open answers the same booking and writes nothing. 404 <c>supplier_booking_not_found</c>; 422 <c>service_dispute_not_reportable</c> (the
    /// booking is not paid inside CasaZen, or not a job that was done), <c>service_dispute_window_closed</c> (the time is over; <c>windowEndedAt</c>),
    /// <c>service_dispute_too_early</c> (the work is not completed and its time has not ended), <c>service_dispute_invalid</c> (<c>fields</c>: <c>reason</c>,
    /// <c>text</c>); 429.
    /// </summary>
    [HttpPost("report-problem")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> ReportProblem(
        [FromBody] SupplierBookingReportProblemRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(
            await bookings.ReportProblemAsync(request.ToCredentials(), request.Reason, request.Text, ct)));
    }

    /// <summary>
    /// SP-15c-c: the customer takes its complaint back — the money is no longer stopped and the supplier is paid at the next run of the funds job. Same
    /// proof of who is asking and same limits as the other actions. Taking back a complaint that was taken back answers the same booking. 404
    /// <c>supplier_booking_not_found</c> or <c>service_dispute_not_found</c> (no complaint); 409 <c>service_dispute_not_open</c> (it was decided already);
    /// 429.
    /// </summary>
    [HttpPost("withdraw-problem")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> WithdrawProblem(
        [FromBody] SupplierBookingAccessRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.WithdrawProblemAsync(request.ToCredentials(), ct)));
    }

    /// <summary>
    /// Writes in the conversation of a request on measure (SP-10b): <c>body</c>, 1 to 1000 characters of plain text, at most 200 messages a
    /// request. The supplier is told by e-mail (the comune and "Nome C.", never the words), one e-mail for the messages of ten minutes.
    /// Answers the booking as it is now, thread included. 404 <c>supplier_booking_not_found</c> (also for a booking that is not a request on
    /// measure); 422 <c>service_request_message_invalid</c>, <c>service_request_thread_full</c>, <c>supplier_booking_supplier_unavailable</c>; 409
    /// <c>service_request_thread_closed</c> (refused, cancelled or done), <c>service_request_state_changed</c>; 429.
    /// </summary>
    [HttpPost("messages")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingViewResponse>> PostMessage(
        [FromBody] SupplierBookingMessageRequest request,
        CancellationToken ct)
    {
        return Ok(PublicSupplierBookingViewMapper.ToDto(await bookings.PostMessageAsync(request.ToCredentials(), request.Body, ct)));
    }

    /// <summary>
    /// The other suppliers the customer can turn to when <b>CasaZen closed its request because the supplier did not answer in time</b> (SP-10c): at
    /// most three, that cover the comune and have the category of the request, whose showcase is published, not paused and takes bookings, best first by
    /// the one ranking of the platform; for each its name, slug, cover, «Verificato», the typical time to answer and the «da €» of the service that
    /// corresponds. Never the supplier of the request, never a personal datum, and an empty list for a booking in any other state: they are the way out
    /// of a silence, not a list of suppliers. The same proof of who is asking and the same limits as <c>lookup</c> (one uniform 404, one 429 for both
    /// limits). The same list is in <c>lookup</c> (<c>alternatives</c>) and in the e-mail of the lapsed request. 404 <c>supplier_booking_not_found</c>;
    /// 400 <c>validation_error</c>; 429 <c>rate_limited</c>.
    /// </summary>
    [HttpPost("alternatives")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestBookingLookup)]
    [SupplierBookingManageRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.ManageMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingAlternativesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingAlternativesResponse>> Alternatives(
        [FromBody] SupplierBookingAccessRequest request,
        CancellationToken ct)
    {
        return Ok(new PublicSupplierBookingAlternativesResponse
        {
            Alternatives = PublicSupplierAlternativeMapper.ToDtos(await bookings.ListAlternativesAsync(request.ToCredentials(), ct)),
        });
    }

    /// <summary>A 422 that names the fields at fault, as the booking does.</summary>
    private ObjectResult RuleProblem(string code, string messageKey, IReadOnlyList<object> messageArgs, IReadOnlyList<string> fields)
    {
        var problem = ApiProblemDetails.Create(HttpContext, StatusCodes.Status422UnprocessableEntity, code, messageKey, messageArgs);
        problem.Extensions["fields"] = fields;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            ContentTypes = { ApiProblemDetails.ContentType },
        };
    }
}
