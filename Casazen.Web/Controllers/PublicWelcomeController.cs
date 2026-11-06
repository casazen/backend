using Casazen.Core.Services;
using Casazen.Web.DTOs.Crm;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The welcome page of a property, behind the QR code in the house (E07/F04/US01/T03, NF-90, CRM-03, AC-NF90-3): a guest reads what the page is
/// about and, if they want, leaves an e-mail address and/or a phone number with a per-channel consent. Anonymous: the random token of the
/// link is the only proof, and an unknown token and a revoked one are the same 404 <c>welcome_link_invalid</c>. <b>Not behind
/// <c>Features:CrmMarketing</c></b>: a code already printed keeps working whatever the flag says. Limited per client IP
/// (<see cref="RateLimitPolicies.PublicMarketingConsent"/>); never indexable and never cached, so a revoked code stops at once; no address and
/// no number is ever logged. Runbook <c>docs/runbooks/crm-consent.md</c>.
/// </summary>
[ApiController]
[Route("api/public/welcome/{token}")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicMarketingConsent)]
[PrivateAnswer]
public class PublicWelcomeController(IWelcomeConsentService welcome) : ControllerBase
{
    /// <summary>
    /// What the page shows: <c>{ orgName, propertyName, marketingConsentVersion }</c>, no personal data. <c>marketingConsentVersion</c> is
    /// <c>null</c> while the consent text has no version: the page then offers no consent. 404 <c>welcome_link_invalid</c> for an unknown or
    /// revoked token; 429.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(WelcomePageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<WelcomePageDto>> Get(string token, CancellationToken cancellationToken)
    {
        var page = await welcome.GetPageAsync(token, cancellationToken);
        if (page is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, WelcomeConsentErrorCodes.LinkInvalid, "WelcomeLinkInvalid");

        return Ok(WelcomePageDto.From(page));
    }

    /// <summary>
    /// The consent of the guest: <c>{ email, phone, consentEmail, consentWhatsApp, adultConfirmed, consentVersion, language }</c>. A lead profile
    /// is found or made and each ticked channel becomes a consent contact (origin and source <c>WelcomePage</c>, the version, the IP and the
    /// time); the double opt-in e-mail follows. <b>Always <c>202 { confirmationSent: true }</c></b> when the call is valid, whether the address
    /// is new, known, confirmed or suppressed: it never reveals what is stored and never lifts an opt-out. 404 <c>welcome_link_invalid</c>; 422
    /// <c>welcome_adult_required</c> (the guest must declare to be of age), <c>welcome_channel_required</c>, <c>welcome_email_invalid</c>,
    /// <c>welcome_phone_invalid</c>, <c>welcome_consent_unavailable</c> (the text has no configured version); 409
    /// <c>welcome_consent_outdated</c> (the version sent is not the current one); 429.
    /// </summary>
    [HttpPost("consent")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType(typeof(WelcomeConsentResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Consent(string token, [FromBody] WelcomeConsentRequest request, CancellationToken cancellationToken)
    {
        await welcome.SubmitAsync(token, request.ToInput(), ClientIp.GetString(HttpContext), cancellationToken);
        return Accepted(new WelcomeConsentResponse(true));
    }
}
