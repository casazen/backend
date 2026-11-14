using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Models;
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
    ILogger<BillingSetupController> logger,
    IPortalAccountSettingsService? portalAccounts = null) : ControllerBase
{
    /// <summary>The settings of the portal accounts (E14/F01/US01/T01), registered with the fiscal documents.</summary>
    private IPortalAccountSettingsService PortalAccounts =>
        portalAccounts ?? throw new InvalidOperationException("The settings of the portal accounts are not registered.");

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

    /// <summary>
    /// The endpoint of the section <c>channels</c> (E14/F01/US01/T01, ⚖ I10, I11): who holds the accounts of the portals by default (<c>portalAccountHolder</c>,
    /// <c>Owner</c> or <c>Agency</c>) and whether their commissions are recharged to the owners (<c>rechargePortalCommissions</c>); a member left out keeps its
    /// value. Answers the section as saved. 422 <c>billing_channels_invalid</c>; 403 <c>fiscal_settings_need_whole_org</c>. Behind <c>Features:OwnerInvoicing</c>
    /// too, like the invoices that use it: 404 while it is off.
    /// </summary>
    [HttpPut(BillingSetupSections.Channels)]
    [FeatureGate(FeatureFlags.OwnerInvoicing)]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(BillingSetupSectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BillingSetupSectionDto>> PutChannels([FromBody] PortalAccountSettingsCommand command, CancellationToken cancellationToken)
    {
        if (await WholeOrgAsync(cancellationToken) is not { } orgId)
            return NeedsWholeOrg();

        await PortalAccounts.SaveAsync(orgId, command, User.GetUserId(), cancellationToken);
        var setup = await billingSetup.GetAsync(orgId, cancellationToken);
        return Ok(BillingSetupSectionDto.From(setup.Sections.Single(s => s.Section == BillingSetupSections.Channels)));
    }

    /// <summary>
    /// The holders of the portal accounts of the listings where they differ from the default (E14/F01/US01/T01): <c>[{ propertyId, propertyName, channel, holder,
    /// updatedAt }]</c>, by property then portal. The whole org only (403 <c>fiscal_settings_need_whole_org</c>): the list names properties a caller restricted to
    /// some of them does not reach. Behind <c>Features:OwnerInvoicing</c> too: 404 while it is off.
    /// </summary>
    [HttpGet("channel-account-holders")]
    [FeatureGate(FeatureFlags.OwnerInvoicing)]
    [ProducesResponseType(typeof(List<ChannelAccountHolderDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<ChannelAccountHolderDto>>> GetAccountHolders(CancellationToken cancellationToken)
    {
        if (await WholeOrgAsync(cancellationToken) is not { } orgId)
            return NeedsWholeOrg();

        var holders = await PortalAccounts.ListAccountHoldersAsync(orgId, cancellationToken);
        return Ok(holders.Select(ChannelAccountHolderDto.From).ToList());
    }

    /// <summary>
    /// Writes the holders of the listings named, in bulk for many flats: <c>[{ propertyId, channel, holder }]</c>, <c>holder</c> <c>Owner</c> or <c>Agency</c>, or
    /// <c>null</c> to bring the listing back to the default; the listings not named keep theirs. Answers the whole list as saved. 422
    /// <c>channel_account_holders_invalid</c> (with the <c>index</c> and the <c>field</c>: an empty list, a property of another org, a channel that is not a portal,
    /// a listing named twice); 409 <c>channel_account_holders_conflict</c>; 403 <c>fiscal_settings_need_whole_org</c>. A refusal saves nothing. Behind
    /// <c>Features:OwnerInvoicing</c> too: 404 while it is off.
    /// </summary>
    [HttpPut("channel-account-holders")]
    [FeatureGate(FeatureFlags.OwnerInvoicing)]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(List<ChannelAccountHolderDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<List<ChannelAccountHolderDto>>> PutAccountHolders(
        [FromBody] List<ChannelAccountHolderCommand>? holders,
        CancellationToken cancellationToken)
    {
        if (await WholeOrgAsync(cancellationToken) is not { } orgId)
            return NeedsWholeOrg();

        var saved = await PortalAccounts.SaveAccountHoldersAsync(orgId, holders, User.GetUserId(), cancellationToken);
        return Ok(saved.Select(ChannelAccountHolderDto.From).ToList());
    }

    /// <summary>The caller's org, only when its reach is the whole org.</summary>
    private async Task<Guid?> WholeOrgAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return null;

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is { IsOrgWide: true })
            return orgId;

        logger.LogWarning("User {UserId} may not use the portal accounts of org {OrgId}: its scope is not the whole org", User.GetUserId(), orgId);
        return null;
    }

    private ActionResult NeedsWholeOrg() =>
        this.ApiProblem(
            StatusCodes.Status403Forbidden,
            DocumentIssuerErrorCodes.NeedsWholeOrg,
            DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.NeedsWholeOrg));
}
