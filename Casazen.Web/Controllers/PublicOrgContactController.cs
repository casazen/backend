using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// «Scrivi a {host}» on the public booking site (DB-03b): a visitor, who has no booking yet, writes a few lines to the host, who gets them
/// by email and answers from their mailbox. Anonymous, rate limited per client IP and per address and org, honeypot, no attachment, a bounded
/// body. The answer is the same <c>202</c> whatever the org publishes or has: it never says whether the message reached anybody.
/// </summary>
[ApiController]
[Route("api/public/orgs/{slug}/contact")]
[AllowAnonymous]
public class PublicOrgContactController(
    IOrgService orgService,
    IHostContactService contacts,
    HostContactEmailRateLimiter emailLimiter,
    ILogger<PublicOrgContactController> logger) : ControllerBase
{
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary>
    /// Sends the message of a visitor to the host of the site <c>{slug}</c> (a former slug too, PL-04). 202 with no body once it is taken in,
    /// <b>the same for every org</b> that exists: whether the org publishes its contact email on the site, has one or not, and whether the
    /// honeypot caught the call (then nothing is sent). 400 for a body that breaks the rules (<c>validation_error</c>; the consent not given:
    /// <c>host_contact_consent_required</c>), 404 for a site that does not exist, 429 over the limits (per client IP, then 3 messages a day
    /// per address and org). The email goes to the org's contact address with the visitor's address as its <c>Reply-To</c>; the text is not
    /// stored and not logged.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.PublicContact)]
    [RequestSizeLimit(MaxBodyBytes)]
    [PrivateAnswer]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Send(
        [StringLength(100)] string slug,
        [FromBody] HostContactRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (!request.Consent)
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest,
                HostContactRules.ConsentRequiredCode,
                "HostContactConsentRequired");
        }

        // A plain address, nothing around it: no display name, no angle brackets, no spaces.
        var email = request.Email.Trim();
        if (!MailAddress.TryCreate(email, out var address) || !string.Equals(address.Address, email, StringComparison.Ordinal))
            return this.ApiProblem(StatusCodes.Status400BadRequest, HostContactRules.EmailInvalidCode, "HostContactEmailInvalid");

        var text = HostContactRules.NormalizeMessage(request.Message);
        if (text is null)
            return this.ApiProblem(StatusCodes.Status400BadRequest, HostContactRules.MessageInvalidCode, "HostContactMessageInvalid");

        // The honeypot: a form field no person sees. A call that fills it gets the answer of a real one, and nothing is sent.
        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            logger.LogInformation("Contact message to a site dropped: the honeypot was filled");
            return Accepted();
        }

        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        // Counted by the org, not by the slug it was reached with: the former slugs of an org are one budget with the current one.
        if (!emailLimiter.TryAcquire(org.Id.ToString("N"), email, out _))
        {
            logger.LogWarning("Contact message to org {OrgId} refused: the address wrote too often", org.Id);
            return SupplierBookingEmailRateLimitFilter.RateLimitedResult(HttpContext, emailLimiter.Window);
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? null : string.Join(' ', request.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        await contacts.SendAsync(
            org,
            new HostContactMessage(email, text, name, request.PropertyId, HostContactRules.NormalizeLang(request.Lang)),
            cancellationToken);

        return Accepted();
    }
}
