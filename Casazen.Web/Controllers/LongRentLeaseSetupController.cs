using Casazen.Core.Authorization;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// What the wizard of a lease needs besides the draft itself (LR-12): the rules of the area, so no figure of law is written in the
/// interface (decision LR-D29), and the data of the landlord, so the person who creates a lease does not type their own again.
/// </summary>
[ApiController]
[Route("api/long-rent")]
public class LongRentLeaseSetupController(
    ILongRentLandlordDefaultsService landlordDefaults,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The rules of the long-term area the backend applies, read from the same constants it checks against:
    /// <c>termMonths</c> and <c>renewalMonths</c> per contract type, <c>noticeMonths</c>, <c>registrationDays</c>,
    /// <c>depositMaxRents</c> and the <c>availableTypes</c> that can be created. Needs <c>lease.read</c>. The figures are product
    /// values taken from the demo and the interview, to be confirmed by a lawyer before the area is turned on (LR-D29).
    /// </summary>
    [HttpGet("rules")]
    [Authorize(Policy = CasazenPolicies.LeaseRead)]
    [ProducesResponseType(typeof(LongRentRulesDto), StatusCodes.Status200OK)]
    public ActionResult<LongRentRulesDto> GetRules() => Ok(LongRentRules.Current);

    /// <summary>
    /// The landlord the wizard suggests: the first landlord of the most recent lease the caller reaches (<c>firstName</c>,
    /// <c>lastName</c>, <c>fiscalCode</c>, <c>residence</c>, <c>contactEmail</c>, not masked), or else the fiscal data of the org and
    /// the name and e-mail of the caller. Any field may be <c>null</c>. Needs <c>lease.create</c>: it is the data of a landlord in
    /// clear. A collaborator limited to some properties only gets landlords of the leases of those properties.
    /// </summary>
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
