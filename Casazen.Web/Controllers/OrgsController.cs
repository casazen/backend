using System.ComponentModel.DataAnnotations;
using Casazen.Core.Options;
using Casazen.Core.OrgTeam;
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
    IOrgSeatService seatService,
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
    /// to the org (shared links), see <c>IOrgService.UpdateSettingsAsync</c>. Since DB-03b-a it also writes the operator data
    /// that the footer of the booking site shows (<c>legalName</c>, <c>registeredOffice</c>; left out = unchanged, blank = cleared,
    /// 422 <c>org_operator_*_too_long</c> above the limit), under the same policy as the rest of the body. Since AM-08b-b it also
    /// writes the phone of the account (<c>contactPhone</c>, never published; 422 <c>org_contact_phone_invalid</c>) and its language
    /// (<c>defaultLanguage</c>, <c>it</c> or <c>en</c>; 422 <c>org_default_language_invalid</c>), left out = unchanged.
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
            actorUserId: User.GetUserId(),
            cancellationToken: cancellationToken,
            // DB-03b-a: the operator data of the booking-site footer changes only when the body carries it.
            legalName: dto.LegalNameSent ? FieldUpdate<string>.Set(dto.LegalName) : FieldUpdate<string>.Unchanged,
            registeredOffice: dto.RegisteredOfficeSent
                ? FieldUpdate<string>.Set(dto.RegisteredOffice)
                : FieldUpdate<string>.Unchanged,
            // AM-08b-b: the phone (never published) and the language of the account change only when the body carries them.
            contactPhone: dto.ContactPhoneSent ? FieldUpdate<string>.Set(dto.ContactPhone) : FieldUpdate<string>.Unchanged,
            defaultLanguage: dto.DefaultLanguageSent ? FieldUpdate<string>.Set(dto.DefaultLanguage) : FieldUpdate<string>.Unchanged);
        if (updated is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        if (previousSlug is not null && !string.Equals(previousSlug, updated.Slug, StringComparison.Ordinal))
            publicHostResolver.InvalidateOrgHosts(updated, publicHostOptions.Value.NormalizedBaseDomain, previousSlug);

        return Ok(OrgSettingsDto.FromOrg(updated));
    }

    /// <summary>
    /// The caller org's rules for the requests it sends to suppliers (SP-BKP): whether a request also goes to up to three backup
    /// suppliers (on by default). Org policy <see cref="CasazenPolicies.OrgBillingAdmin"/>, like the rest of the org's settings.
    /// </summary>
    [HttpGet("me/service-request-settings")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(OrgServiceRequestSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgServiceRequestSettingsDto>> GetMyServiceRequestSettings(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var org = await orgService.GetByIdAsync(orgId.Value, cancellationToken);
        if (org is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        return Ok(new OrgServiceRequestSettingsDto { UseBackupSuppliers = org.SupplierBackupsEnabled });
    }

    /// <summary>
    /// Turns on or off the org's default for sending its requests to backup suppliers too (SP-BKP). A request already sent is not touched:
    /// the choice is read when a request is created.
    /// </summary>
    [HttpPut("me/service-request-settings")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(OrgServiceRequestSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgServiceRequestSettingsDto>> UpdateMyServiceRequestSettings(
        [FromBody] OrgServiceRequestSettingsDto dto,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var updated = await orgService.SetSupplierBackupsEnabledAsync(orgId.Value, dto.UseBackupSuppliers, User.GetUserId(), cancellationToken);
        if (updated is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        return Ok(new OrgServiceRequestSettingsDto { UseBackupSuppliers = updated.SupplierBackupsEnabled });
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
    /// endpoints: the org's owner reads it from the short-rent or the long-rent context alike (PL-16, A1-36). The tier is
    /// the effective one: with the open access on (<c>Entitlement:OpenAccess</c>, BL-01) it can be higher than the plan the
    /// org pays for, and <c>openAccess</c> says so.
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
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        return Ok(await BuildEntitlementAsync(orgId.Value, cancellationToken));
    }

    /// <summary>
    /// Moves the caller's org to a lower plan tier, or back to Starter (#274). Paid tiers are granted only by a
    /// Stripe subscription (checkout + webhooks): an upgrade without one is refused with 403
    /// <c>subscription_required</c>, and a plan driven by a live subscription is refused with 409
    /// <c>managed_by_stripe</c> (use the billing portal). Only the org's billing admin may call it.
    /// Downgrades are allowed even when usage exceeds the new limit; existing properties remain,
    /// but new creates are blocked until usage is under the limit. The open access (BL-01) changes none of these rules: they
    /// compare with the plan the org pays for, not with the tier the open access raises it to.
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
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "BillingPlanTierUnknown");

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var org = await orgService.GetByIdAsync(orgId.Value, cancellationToken);
        if (org is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        // The plan change rules compare with the tier the subscription pays for, not with the one raised by the open access
        // (BL-01): without a subscription a paid tier is still refused, and the stored tier stays what the org pays for.
        switch (PlanChangePolicy.EvaluateManualChange(org, entitlementService.ResolvePaidTier(org), planTier))
        {
            case ManualPlanChangeOutcome.ManagedByStripe:
                return this.ApiProblem(StatusCodes.Status409Conflict, PlanProblemCodes.ManagedByStripe, "ManagedByStripe");
            case ManualPlanChangeOutcome.SubscriptionRequired:
                return this.ApiProblem(StatusCodes.Status403Forbidden, PlanProblemCodes.SubscriptionRequired, "SubscriptionRequired");
        }

        var updated = await orgService.UpdatePlanTierAsync(
            orgId.Value, planTier, User.GetUserId(), PlanChangeSource.Org, cancellationToken);
        if (updated is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        return Ok(await BuildEntitlementAsync(orgId.Value, cancellationToken));
    }

    /// <summary>The entitlement of the org: tier, limits and usage of properties and, since AM-02, of seats (people).</summary>
    private async Task<EntitlementDto> BuildEntitlementAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var entitlement = await entitlementService.GetEntitlementAsync(orgId, cancellationToken);
        var canUseCustomDomain = await entitlementService.CanUseCustomDomainAsync(orgId, cancellationToken);
        var seats = await seatService.GetUsageAsync(orgId, cancellationToken);
        var org = await orgService.GetByIdAsync(orgId, cancellationToken);

        return new EntitlementDto
        {
            OrgId = entitlement.OrgId,
            PlanTier = entitlement.PlanTier,
            OpenAccess = entitlement.OpenAccess,
            Limits = new EntitlementLimitsDto { MaxProperties = entitlement.MaxProperties, MaxSeats = seats.Max },
            Usage = new EntitlementUsageDto { Properties = entitlement.PropertyCount, Seats = seats.Used },
            CanAddProperty = entitlement.CanAddProperty,
            CanUseCustomDomain = canUseCustomDomain,
            CanInviteMember = seats.CanInvite,
            IsAgency = org?.PlanTier == Core.Entities.Enums.PlanTier.Agency,
        };
    }
}
