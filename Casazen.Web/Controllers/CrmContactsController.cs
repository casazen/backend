using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// The CRM contacts of the caller's org (E07/F04/US01/T01, NF-90): what the host can do with one contact. Today it asks for the double opt-in
/// e-mail of a consent that was never confirmed (for example a consent collected before the e-mail existed). Behind
/// <c>Features:CrmMarketing</c> (404 while it is off, before authentication) and the policy <see cref="CasazenPolicies.GuestWrite"/>; the
/// contact is the one of the caller's org (another org's is a 404) and, for a collaborator limited to some properties, one of a profile
/// that has stays at a property the collaborator reaches (HostScope). Runbook <c>docs/runbooks/crm-consent.md</c>.
/// </summary>
[ApiController]
[Route("api/crm/contacts")]
[FeatureGate(FeatureFlags.CrmMarketing)]
[Authorize(Policy = CasazenPolicies.GuestWrite)]
public class CrmContactsController(
    IMarketingConfirmationService confirmation,
    AppDbContext db,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<CrmContactsController> logger) : ControllerBase
{
    /// <summary>
    /// Asks for the double opt-in e-mail of a contact to be sent now (or again). 202 <c>{ sent }</c>: <c>true</c> when the e-mail was queued,
    /// <c>false</c> when the last one went out less than ten minutes ago and nothing was sent. 404 <c>crm_contact_not_found</c> (not a contact of
    /// the caller's org, or out of the caller's reach); 422 <c>crm_confirmation_not_required</c> (not an e-mail address with a consent, or
    /// already confirmed); 409 <c>crm_confirmation_limit_reached</c> (the maximum number of messages was sent), <c>crm_contact_suppressed</c>
    /// (the person opted out: nothing is ever sent); 503 <c>marketing_confirmation_unavailable</c> (the e-mail could not be queued).
    /// </summary>
    [HttpPost("{id:guid}/send-confirmation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SendConfirmation(Guid id, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Forbid();

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return Forbid();

        if (!await IsInReachAsync(scope, id, cancellationToken))
            throw ContactNotFound();

        switch (await confirmation.SendConfirmationAsync(orgId.Value, id, cancellationToken))
        {
            case MarketingConfirmationSendOutcome.Queued:
                logger.LogInformation("Marketing confirmation of contact {ContactId} requested by the host of org {OrgId}", id, orgId.Value);
                return Accepted(new SendConfirmationResponse(true));
            case MarketingConfirmationSendOutcome.TooSoon:
                return Accepted(new SendConfirmationResponse(false));
            case MarketingConfirmationSendOutcome.ContactNotFound:
                throw ContactNotFound();
            case MarketingConfirmationSendOutcome.NotRequired:
                throw new DomainRuleException("crm_confirmation_not_required", "CrmConfirmationNotRequired");
            case MarketingConfirmationSendOutcome.LimitReached:
                throw new DomainConflictException("crm_confirmation_limit_reached", "CrmConfirmationLimitReached");
            case MarketingConfirmationSendOutcome.Suppressed:
                throw new DomainConflictException("crm_contact_suppressed", "CrmContactSuppressed");
            default:
                return this.ApiProblem(
                    StatusCodes.Status503ServiceUnavailable, "marketing_confirmation_unavailable", "MarketingConfirmationUnavailable");
        }
    }

    /// <summary>
    /// Whether the caller reaches the contact: every contact of the org for a caller whose reach is the whole org; for a collaborator limited
    /// to some properties, a contact of a profile that has stays at one of them (the rule of the list of the profiles, <see cref="CrmController"/>),
    /// and never a lead with no profile. The same 404 as a contact that does not exist, so the answer tells nothing about the other contacts.
    /// </summary>
    private async Task<bool> IsInReachAsync(HostScope scope, Guid contactId, CancellationToken cancellationToken)
    {
        var contact = await db.GuestContacts.AsNoTracking()
            .Where(c => c.OrgId == scope.OrgId && c.Id == contactId)
            .Select(c => new { c.GuestCrmProfileId })
            .FirstOrDefaultAsync(cancellationToken);
        if (contact is null)
            return false;

        if (scope.IsOrgWide)
            return true;

        if (contact.GuestCrmProfileId is not { } profileId)
            return false;

        var accessiblePropertyIds = await db.Properties
            .Where(property => property.OrgId == scope.OrgId)
            .InScope(scope)
            .Select(property => property.Id.ToString())
            .ToListAsync(cancellationToken);
        if (accessiblePropertyIds.Count == 0)
            return false;

        return await db.GuestCrmProfiles.AsNoTracking()
            .Where(profile => profile.OrgId == scope.OrgId && profile.Id == profileId)
            .AnyAsync(profile => accessiblePropertyIds.Any(propertyId => profile.PropertyIdsJson.Contains(propertyId)), cancellationToken);
    }

    private static NotFoundException ContactNotFound() =>
        new("CRM contact not found") { Code = "crm_contact_not_found", MessageKey = "CrmContactNotFound" };

    /// <summary>The answer of <see cref="SendConfirmation"/>: whether an e-mail was queued by this call.</summary>
    /// <param name="Sent"><c>true</c> when the e-mail was queued, <c>false</c> when one was queued less than ten minutes ago.</param>
    public sealed record SendConfirmationResponse(bool Sent);
}
