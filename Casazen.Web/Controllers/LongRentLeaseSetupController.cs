using Casazen.Core.Authorization;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/long-rent")]
public class LongRentLeaseSetupController(
    ILongRentLandlordDefaultsService landlordDefaults,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    [HttpGet("rules")]
    [Authorize(Policy = CasazenPolicies.LeaseRead)]
    [ProducesResponseType(typeof(LongRentRulesDto), StatusCodes.Status200OK)]
    public ActionResult<LongRentRulesDto> GetRules() => Ok(LongRentRules.Current);

    [HttpGet("landlord-defaults")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    [ProducesResponseType(typeof(LandlordDefaultsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<LandlordDefaultsDto>> GetLandlordDefaults(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (userId is null
            || orgId is null
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
        {
            return Unauthorized();
        }

        var defaults = await landlordDefaults.GetAsync(scope, userId, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(defaults);
    }
}
