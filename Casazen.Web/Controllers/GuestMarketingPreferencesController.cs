using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Crm;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The marketing choices of the guest of a booking, on the guest page (E07/F04/US01/T04, NF-90, CRM-03, AC-NF90-2): the guest who proved a
/// booking (<c>api/public/guest/session</c>) reads what they agreed to for e-mail and WhatsApp, gives a consent, withdraws it and asks for the
/// confirmation e-mail again. Behind the policy <see cref="CasazenPolicies.GuestSession"/>; the booking is the one
/// <see cref="IGuestSessionService.FindBookingAsync"/> finds for the guest of the session, so a booking of another guest or another site answers
/// the same uniform 404 <c>guest_booking_not_found</c> as everywhere in the area, whatever the body says. <b>Not behind
/// <c>Features:CrmMarketing</c></b>: a legal record is made whatever the flag says. Never indexable and never cached; no address and no number
/// is ever logged. Runbook <c>docs/runbooks/crm-consent.md</c>.
/// </summary>
[ApiController]
[Route("api/public/guest/bookings/{code}/marketing")]
[Authorize(Policy = CasazenPolicies.GuestSession)]
[PrivateAnswer]
public class GuestMarketingPreferencesController(IGuestMarketingPreferencesService preferences) : ControllerBase
{
    /// <summary>
    /// The choices of the guest: <c>{ marketingConsentVersion, softOptInNoticeVersion, email: { state, maskedAddress, canResend }, whatsapp: { state,
    /// maskedPhone } }</c> with <c>state</c> one of <c>none</c>, <c>pending</c>, <c>confirmed</c>, <c>soft_opt_in</c>, <c>service_only</c>,
    /// <c>withdrawn</c>. Nothing is shown as ticked unless the guest ticked it. 401 without a valid session; 404 <c>guest_booking_not_found</c>; 429.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(GuestMarketingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestMarketingDto>> Get(string code, CancellationToken cancellationToken)
    {
        var ticket = User.GetGuestSessionTicket();
        if (ticket is null)
            return Unauthorized();

        return Ok(GuestMarketingDto.From(await preferences.GetAsync(ticket, code, cancellationToken)));
    }

    /// <summary>
    /// Changes the choices: <c>{ email: { enabled, address }, whatsapp: { enabled, phone }, consentVersion, language }</c>; a channel left out stays
    /// as it is. Turning a channel on records a consent contact (origin and source <c>GuestPage</c>, the version, the IP, the time) and, for e-mail,
    /// queues the double opt-in message; the address is the one of the booking when it is a real address, and must be typed when the booking
    /// has an OTA forward or none. Turning a channel off withdraws it: the contact is suppressed and the guest is never written to again, and
    /// turning it on afterwards lifts nothing. A channel that is named must say <c>enabled</c>: without it the body is a 400 <c>validation_error</c>
    /// and nothing changes (a forgotten member must never be taken for an opt-out, which is final). The answer is the choices as they are
    /// then. 400; 401; 404 <c>guest_booking_not_found</c>; 409
    /// <c>guest_marketing_consent_outdated</c> (the version sent is not the current one); 422 <c>guest_marketing_consent_unavailable</c> (the
    /// consent text has no configured version, or the CRM cannot store the choice here), <c>guest_marketing_email_invalid</c>,
    /// <c>guest_marketing_phone_invalid</c>; 429.
    /// </summary>
    [HttpPut]
    [EnableRateLimiting(RateLimitPolicies.PublicMarketingConsent)]
    [RequestSizeLimit(4096)]
    [ProducesResponseType(typeof(GuestMarketingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestMarketingDto>> Put(
        string code,
        [FromBody] UpdateGuestMarketingRequest request,
        CancellationToken cancellationToken)
    {
        var ticket = User.GetGuestSessionTicket();
        if (ticket is null)
            return Unauthorized();

        var result = await preferences.SetAsync(ticket, code, request.ToChange(), ClientIp.GetString(HttpContext), cancellationToken);
        return Ok(GuestMarketingDto.From(result));
    }

    /// <summary>
    /// Asks for the confirmation e-mail of the pending e-mail consent again, with the limits of the confirmation (three messages, one every ten
    /// minutes). <c>202 { sent }</c>: <c>true</c> when the message was queued, <c>false</c> when nothing was waiting for a confirmation or the limits
    /// did not allow another one. 401; 404 <c>guest_booking_not_found</c>; 503 <c>marketing_confirmation_unavailable</c> (the message could not be
    /// queued); 429.
    /// </summary>
    [HttpPost("resend-confirmation")]
    [EnableRateLimiting(RateLimitPolicies.PublicMarketingConsent)]
    [ProducesResponseType(typeof(GuestMarketingResendResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResendConfirmation(string code, CancellationToken cancellationToken)
    {
        var ticket = User.GetGuestSessionTicket();
        if (ticket is null)
            return Unauthorized();

        var outcome = await preferences.ResendConfirmationAsync(ticket, code, cancellationToken);
        if (outcome == MarketingConfirmationSendOutcome.Unavailable)
        {
            return this.ApiProblem(
                StatusCodes.Status503ServiceUnavailable, "marketing_confirmation_unavailable", "MarketingConfirmationUnavailable");
        }

        return Accepted(new GuestMarketingResendResponse(outcome == MarketingConfirmationSendOutcome.Queued));
    }
}
