using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The whole setup of the invoicing in one endpoint (NF-82, NF-104, NF-118, E11/F01/US01/T04): the sections of the wizard «Fatturazione» with their values and
/// their missing items, and the save of one section. It is about money: reading needs <c>payment.read</c> (as <see cref="FiscalDocumentSettingsController"/>),
/// saving adds <c>property.write</c>. No new permission keys. The whole controller is behind <c>Features:FiscalDocumentIssuance</c>: 404 while it is off.
/// </summary>
/// <remarks>
/// The setup belongs to the whole organization, not to some of its properties: a caller who reaches only some of them (a collaborator «Solo alcuni») may read
/// it and may not change it (403 <c>fiscal_settings_need_whole_org</c>, as the issuer setting). The Italian VAT number and the fiscal code are read from the
/// organization and saved in its tax profile, never here. Nothing of the bank accounts of the issuer is in the answer.
/// </remarks>
[ApiController]
[Route("api/billing-setup")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
[FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
public class BillingSetupController(
    IBillingSetupService billingSetup,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<BillingSetupController> logger) : ControllerBase
{
    /// <summary>
    /// Every section of the setup, in the order of the wizard: <c>{ complete, sections: [{ section, applies, complete, values, missing }] }</c>, a missing item being
    /// <c>{ key, section, reason, count? }</c>. The key of the first missing item is where the guide reopens the wizard.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(BillingSetupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BillingSetupDto>> Get(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        return Ok(BillingSetupDto.From(await billingSetup.GetAsync(orgId.Value, cancellationToken)));
    }

    /// <summary>
    /// Saves the fields of one section (<c>issuer</c>, <c>vat</c>, <c>numbering</c>, <c>touristTax</c>, <c>automation</c>, <c>italy</c>) and answers the section as
    /// saved. A partial payload is a draft: what it does not carry stays, and saving the same answers again changes nothing. 404 <c>billing_setup_section_unknown</c>;
    /// 422 <c>billing_setup_section_not_writable</c> (<c>channels</c> and <c>mandates</c> have their own endpoints), <c>billing_setup_field_not_in_section</c>,
    /// <c>issuer_field_invalid</c> (with the <c>field</c> and the list of <c>errors</c>), <c>issuer_regime_country_mismatch</c>; 403
    /// <c>fiscal_settings_need_whole_org</c> for a caller who reaches only some of the properties. A refusal saves nothing.
    /// </summary>
    [HttpPut("{section}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(BillingSetupSectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BillingSetupSectionDto>> PutSection(
        string section,
        [FromBody] SaveBillingSetupSectionRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        // The setup is the organization's: a caller who reaches only some of its properties cannot decide how all of them are invoiced.
        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is not { IsOrgWide: true })
        {
            logger.LogWarning("User {UserId} may not change the billing setup of org {OrgId}: its scope is not the whole org", User.GetUserId(), orgId);
            return this.ApiProblem(
                StatusCodes.Status403Forbidden,
                DocumentIssuerErrorCodes.NeedsWholeOrg,
                DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.NeedsWholeOrg));
        }

        var saved = await billingSetup.PutSectionAsync(orgId.Value, section, request.ToPayload(), User.GetUserId(), cancellationToken);
        return Ok(BillingSetupSectionDto.From(saved));
    }
}
