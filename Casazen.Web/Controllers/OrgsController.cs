using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// Org surfaces for the caller's organization (US-004): plan catalogue, entitlement and downgrades.
/// Paid upgrades go through Stripe Checkout (<see cref="BillingController"/>), never through this controller (#274).
/// </summary>
[ApiController]
[Route("api/orgs")]
public class OrgsController(
    IOrgContextResolver orgContextResolver,
    IEntitlementService entitlementService,
    IOrgService orgService,
    IPublicHostResolver publicHostResolver,
    IOptions<PublicHostOptions> publicHostOptions) : ControllerBase
{
    /// <summary>
    /// Returns the caller org's editable identity: name, public slug and contact email with its publication
    /// opt-in (A1-22, A1-23). Org policy <see cref="CasazenPolicies.OrgBillingAdmin"/>: unlike
    /// <c>OrgSummaryDto</c> (any member, via <c>/api/users/me</c>) this carries the contact email.
    /// </summary>
    [HttpGet("me/settings")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(OrgSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgSettingsDto>> GetMySettings(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var org = await orgService.GetByIdAsync(orgId.Value, cancellationToken);
        if (org is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        return Ok(OrgSettingsDto.FromOrg(org));
    }

    /// <summary>
    /// Whether a slug can become the caller org's public slug (A1-23): its normalized form and, when it cannot, the
    /// reason code (<c>org_slug_invalid</c>, <c>org_slug_reserved</c>, <c>org_slug_taken</c>). Advisory only: the
    /// PUT checks again under lock.
    /// </summary>
    [HttpGet("me/settings/slug-availability")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(OrgSlugAvailabilityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgSlugAvailabilityDto>> GetSlugAvailability(
        [FromQuery, Required, MaxLength(100)] string slug,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var availability = await orgService.CheckSlugAvailabilityAsync(orgId.Value, slug, cancellationToken);
        if (availability is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        return Ok(new OrgSlugAvailabilityDto
        {
            Slug = availability.Slug,
            Available = availability.Available,
            Code = availability.Code,
        });
    }

    /// <summary>
    /// Updates the caller org's name, public slug and contact email, including whether the contact email is
    /// published on the public booking site (A1-22, A1-23). 422 <c>org_slug_invalid</c> / <c>org_slug_reserved</c>
    /// for an unusable slug, 409 <c>org_slug_taken</c> when another org uses it. The previous slug keeps resolving
    /// to the org (shared links), see <c>IOrgService.UpdateSettingsAsync</c>.
    /// </summary>
    [HttpPut("me/settings")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(OrgSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OrgSettingsDto>> UpdateMySettings(
        [FromBody] UpdateOrgSettingsDto dto,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var previousSlug = (await orgService.GetByIdAsync(orgId.Value, cancellationToken))?.Slug;
        var updated = await orgService.UpdateSettingsAsync(
            orgId.Value,
            dto.Name,
            dto.Slug,
            dto.ContactEmail,
            dto.ContactEmailPublic,
            cancellationToken);
        if (updated is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        if (previousSlug is not null && !string.Equals(previousSlug, updated.Slug, StringComparison.Ordinal))
            InvalidatePublicHostCache(updated, previousSlug);

        return Ok(OrgSettingsDto.FromOrg(updated));
    }

    /// <summary>
    /// The resolve-host cache carries the org slug: drop the entries of the org's hosts (custom domain, subdomain and
    /// slug-as-subdomain fallback, old and new) so the public site does not use the old slug until the cache expires.
    /// </summary>
    private void InvalidatePublicHostCache(Org org, string previousSlug)
    {
        if (!string.IsNullOrWhiteSpace(org.CustomDomain))
            publicHostResolver.InvalidateCacheForHost(org.CustomDomain);

        if (publicHostOptions.Value.NormalizedBaseDomain is not { } baseDomain)
            return;

        foreach (var label in new[] { org.Subdomain, previousSlug, org.Slug })
        {
            if (!string.IsNullOrWhiteSpace(label))
                publicHostResolver.InvalidateCacheForHost($"{label}.{baseDomain}");
        }
    }

    /// <summary>Returns available plan tiers and property limits.</summary>
    [HttpGet("plans")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<PlanDto>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<PlanDto>> GetPlans() =>
        Ok(PlanCatalog.All.Select(p => new PlanDto
        {
            Tier = p.Tier.ToString(),
            DisplayName = p.DisplayName,
            MaxProperties = p.MaxProperties == int.MaxValue ? -1 : p.MaxProperties,
            Description = p.Description,
        }));

    /// <summary>
    /// Returns the caller org's plan entitlement: tier, limits, current usage, and whether
    /// another property may be created (AC8). Org policy <see cref="CasazenPolicies.OrgBillingAdmin"/>, like the billing
    /// endpoints: the org's owner reads it from the short-rent or the long-rent context alike (PL-16, A1-36).
    /// </summary>
    [HttpGet("me/entitlement")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(EntitlementDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EntitlementDto>> GetMyEntitlement(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NotFound(new { error = "No organization assigned to the current user" });

        var entitlement = await entitlementService.GetEntitlementAsync(orgId.Value, cancellationToken);
        var canUseCustomDomain = await entitlementService.CanUseCustomDomainAsync(orgId.Value, cancellationToken);

        return Ok(new EntitlementDto
        {
            OrgId = entitlement.OrgId,
            PlanTier = entitlement.PlanTier,
            Limits = new EntitlementLimitsDto { MaxProperties = entitlement.MaxProperties },
            Usage = new EntitlementUsageDto { Properties = entitlement.PropertyCount },
            CanAddProperty = entitlement.CanAddProperty,
            CanUseCustomDomain = canUseCustomDomain
        });
    }

    /// <summary>
    /// Moves the caller's org to a lower plan tier, or back to Starter (#274). Paid tiers are granted only by a
    /// Stripe subscription (checkout + webhooks): an upgrade without one is refused with 403
    /// <c>subscription_required</c>, and a plan driven by a live subscription is refused with 409
    /// <c>managed_by_stripe</c> (use the billing portal). Only the org's billing admin may call it.
    /// Downgrades are allowed even when usage exceeds the new limit; existing properties remain,
    /// but new creates are blocked until usage is under the limit.
    /// </summary>
    [HttpPut("me/plan")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(EntitlementDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EntitlementDto>> UpdateMyPlan(
        [FromBody] UpdateOrgPlanDto dto,
        CancellationToken cancellationToken)
    {
        if (!PlanCatalog.TryParseTier(dto.PlanTier, out var planTier))
            return BadRequest(new { error = $"Unknown planTier: {dto.PlanTier}" });

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var org = await orgService.GetByIdAsync(orgId.Value, cancellationToken);
        if (org is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        switch (PlanChangePolicy.EvaluateManualChange(org, entitlementService.ResolveEffectiveTier(org), planTier))
        {
            case ManualPlanChangeOutcome.ManagedByStripe:
                return this.ApiProblem(StatusCodes.Status409Conflict, PlanProblemCodes.ManagedByStripe, "ManagedByStripe");
            case ManualPlanChangeOutcome.SubscriptionRequired:
                return this.ApiProblem(StatusCodes.Status403Forbidden, PlanProblemCodes.SubscriptionRequired, "SubscriptionRequired");
        }

        var updated = await orgService.UpdatePlanTierAsync(orgId.Value, planTier, cancellationToken);
        if (updated is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        var entitlement = await entitlementService.GetEntitlementAsync(orgId.Value, cancellationToken);
        var canUseCustomDomain = await entitlementService.CanUseCustomDomainAsync(orgId.Value, cancellationToken);
        return Ok(new EntitlementDto
        {
            OrgId = entitlement.OrgId,
            PlanTier = entitlement.PlanTier,
            Limits = new EntitlementLimitsDto { MaxProperties = entitlement.MaxProperties },
            Usage = new EntitlementUsageDto { Properties = entitlement.PropertyCount },
            CanAddProperty = entitlement.CanAddProperty,
            CanUseCustomDomain = canUseCustomDomain
        });
    }
}
