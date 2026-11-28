using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Owners;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/owner-accounting/channel-commission-rules")]
[Authorize(Policy = CasazenPolicies.AgencyStatementView)]
public class ChannelCommissionRulesController(
    IOwnerMandateService mandates,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ChannelCommissionRuleSetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ChannelCommissionRuleSetDto>> Get()
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        return Ok(ChannelCommissionRuleSetDto.From(await mandates.GetChannelRulesAsync(orgId, HttpContext.RequestAborted)));
    }

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

    private Task<Guid?> OrgAsync() =>
        HostCallerResolver.OrgWideAsync(User, orgContextResolver, hostScopeResolver, HttpContext.RequestAborted);
}
