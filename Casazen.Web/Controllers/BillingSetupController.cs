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
/// The settings of the billing setup that the owner invoice needs (E14/F01/US01/T01, the «Fatturazione» wizard of P5): the <c>channels</c> section (who holds the
/// accounts of the portals by default, ⚖ I10, and whether their commissions are recharged to the owners, ⚖ I11) and the holder of each listing and portal, in
/// bulk. Reading needs <c>property.read</c> and <c>payment.read</c> (as the issuer setting), writing adds <c>property.write</c>. No new permission keys.
/// </summary>
/// <remarks>
/// The settings belong to the whole organization: a caller who reaches only some of its properties (a collaborator «Solo alcuni») is refused (403
/// <c>fiscal_settings_need_whole_org</c>) on every action, before anything is read or written. Behind <c>Features:OwnerInvoicing</c>: 404 while it is off. The
/// rest of the billing setup (E11/F02) adds its sections to this route.
/// </remarks>
[ApiController]
[Route("api/billing-setup")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
[FeatureGate(FeatureFlags.OwnerInvoicing)]
public class BillingSetupController(
    IBillingSetupService setup,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<BillingSetupController> logger) : ControllerBase
{
    /// <summary>The <c>channels</c> section: <c>portalAccountHolder</c> (<c>Owner</c> by default, or <c>Agency</c>) and <c>rechargePortalCommissions</c>.</summary>
    [HttpGet("channels")]
    [ProducesResponseType(typeof(BillingChannelsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BillingChannelsDto>> GetChannels()
    {
        if (await OrgAsync() is not { } orgId)
            return NeedsWholeOrg();

        return Ok(BillingChannelsDto.From(await setup.GetChannelsAsync(orgId, HttpContext.RequestAborted)));
    }

    /// <summary>
    /// Saves the <c>channels</c> section (<c>{ portalAccountHolder?, rechargePortalCommissions? }</c>, a member left out keeps its value) and answers it as saved.
    /// 422 <c>billing_channels_invalid</c>.
    /// </summary>
    [HttpPut("channels")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(BillingChannelsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BillingChannelsDto>> PutChannels([FromBody] BillingChannelsCommand command)
    {
        if (await OrgAsync() is not { } orgId)
            return NeedsWholeOrg();

        return Ok(BillingChannelsDto.From(await setup.SaveChannelsAsync(orgId, command, User.GetUserId(), HttpContext.RequestAborted)));
    }

    /// <summary>The holders of the listings on the portals where they differ from the default of the organization, by property then portal.</summary>
    [HttpGet("channel-account-holders")]
    [ProducesResponseType(typeof(List<ChannelAccountHolderDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<ChannelAccountHolderDto>>> GetAccountHolders()
    {
        if (await OrgAsync() is not { } orgId)
            return NeedsWholeOrg();

        var holders = await setup.ListAccountHoldersAsync(orgId, HttpContext.RequestAborted);
        return Ok(holders.Select(ChannelAccountHolderDto.From).ToList());
    }

    /// <summary>
    /// Writes the holders of the listings named, in bulk: <c>[{ propertyId, channel, holder }]</c>, <c>holder</c> <c>Owner</c> or <c>Agency</c>, or <c>null</c> to
    /// bring the listing back to the default; the listings not named keep theirs. Answers the whole list as saved. 422 <c>channel_account_holders_invalid</c>
    /// (with the <c>index</c> and the <c>field</c>: an empty list, a property of another org, a channel that is not a portal, a listing named twice); 409
    /// <c>channel_account_holders_conflict</c>. A refusal saves nothing.
    /// </summary>
    [HttpPut("channel-account-holders")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(List<ChannelAccountHolderDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<List<ChannelAccountHolderDto>>> PutAccountHolders([FromBody] List<ChannelAccountHolderCommand>? holders)
    {
        if (await OrgAsync() is not { } orgId)
            return NeedsWholeOrg();

        var saved = await setup.SaveAccountHoldersAsync(orgId, holders, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(saved.Select(ChannelAccountHolderDto.From).ToList());
    }

    /// <summary>The caller's org, only when its reach is the whole org.</summary>
    private async Task<Guid?> OrgAsync()
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return null;

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct);
        if (scope is { IsOrgWide: true })
            return orgId;

        logger.LogWarning("User {UserId} may not use the billing setup of org {OrgId}: its scope is not the whole org", User.GetUserId(), orgId);
        return null;
    }

    private ActionResult NeedsWholeOrg() =>
        this.ApiProblem(
            StatusCodes.Status403Forbidden,
            DocumentIssuerErrorCodes.NeedsWholeOrg,
            DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.NeedsWholeOrg));
}
