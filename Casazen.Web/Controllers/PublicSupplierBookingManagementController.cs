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
