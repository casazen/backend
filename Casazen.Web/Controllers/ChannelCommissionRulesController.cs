using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Owners;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The commission rules of the channels of an agency (E13/F01/US01/T01, ⚖ R2): what a portal keeps on a stay whose reservation brings no commission. Read
/// with <c>agency.statement.view</c> (the defaults of the configuration marked), replaced as a whole list with <c>agency.owner.financial.manage</c>.
/// </summary>
/// <remarks>
/// The rules are of the whole org, like the owner accounting they feed: a caller restricted to some properties (a collaborator «Solo alcuni», an account
/// outside the org team) is refused (403) on both actions, even with the permission, before anything is read or written.
/// </remarks>
[ApiController]
[Route("api/owner-accounting/channel-commission-rules")]
[Authorize(Policy = CasazenPolicies.AgencyStatementView)]
public class ChannelCommissionRulesController(
    IOwnerMandateService mandates,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>The rules of the agency and, for each portal, what applies today: the rule in force, else the configured default (<c>isDefault</c>).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ChannelCommissionRuleSetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ChannelCommissionRuleSetDto>> Get()
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        return Ok(ChannelCommissionRuleSetDto.From(await mandates.GetChannelRulesAsync(orgId, HttpContext.RequestAborted)));
    }

    /// <summary>
    /// Replaces the rules with the whole list (<c>[{ channel, percent, base, validFrom, validTo }]</c>, the <c>percent</c> of every rule required; an empty list
    /// leaves the defaults). A rule that posted lines of the owner ledger name can be closed but not removed nor given another percent or base: 409
    /// <c>channel_commission_rule_in_use</c> (with the <c>ruleId</c>). 422 <c>channel_commission_rules_invalid</c> (with the <c>index</c> and the <c>field</c>).
    /// </summary>
    [HttpPut]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(ChannelCommissionRuleSetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ChannelCommissionRuleSetDto>> Put([FromBody] List<ChannelCommissionRuleCommand>? rules)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        var saved = await mandates.SaveChannelRulesAsync(orgId, rules, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(ChannelCommissionRuleSetDto.From(saved));
    }

    /// <summary>The caller's org, only when its reach is the whole org.</summary>
    private async Task<Guid?> OrgAsync()
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return null;

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct);
        return scope is { IsOrgWide: true } ? orgId : null;
    }
}
