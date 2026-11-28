using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Owners;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The two checks of the Italian short-let withholding that look at the owner and at the tenant (E18/F01/US03/T01, NF-117, ⚖ I7 and I8): the stays of a company tenant in the
/// property of a private owner with the decision about their regime (<c>GET</c> and <c>POST api/owner-tax-decisions</c>), and the limit of units of an owner with the
/// apartments it declares outside the agency (<c>GET api/owners/{ownerId}/tax-checks</c>, <c>PUT api/owners/{ownerId}/external-short-let-units</c>). Behind
/// <c>Features:TaxReportingExports</c>: 404 before authentication while it is off. The reach of the caller is resolved here and applied by the service: a booking or an owner
/// outside it is the 404 of one that does not exist.
/// </summary>
/// <remarks>
/// Reading the decisions needs <c>agency.owner.financial.read</c>, recording one <c>agency.owner.financial.manage</c> (like the mandates and the issuer profiles); the limit of
/// an owner is read with <c>agency.owner.view</c>, as its readiness is. Nothing here carries a name of the tenant or a value of its invoice data.
/// </remarks>
[ApiController]
[FeatureGate(FeatureFlags.TaxReportingExports)]
public class OwnerTaxDecisionsController(
    IOwnerTaxChecks checks,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    [HttpGet("api/owner-tax-decisions")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialRead)]
    [ProducesResponseType(typeof(IReadOnlyList<CompanyTenantCase>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CompanyTenantCase>>> List([FromQuery] CompanyTenantCaseState? state)
    {
        if (await ScopeAsync() is not { } scope)
            return Forbid();

        Response.Headers.CacheControl = "private, no-store";
        return Ok(await checks.ListCompanyTenantCasesAsync(scope, state, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Records the decision about the regime of the stay of a company tenant: <c>{ bookingId, regime, reason }</c>, the regime <c>ShortLet</c> or <c>NotShortLet</c>, the reason a
    /// note of the agency. It closes the «Da fare» of the stay and answers the decision in force: the same decision again is the same decision, nothing is written twice. The
    /// withholding facts follow it from then on. 404 <c>booking_not_found</c> (also a booking outside the reach); 422 <c>owner_tax_decision_invalid</c> (with the
    /// <c>field</c>), <c>owner_tax_decision_not_applicable</c> (the stay needs no decision).
    /// </summary>
    [HttpPost("api/owner-tax-decisions")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(ShortLetTaxDecisionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ShortLetTaxDecisionDto>> Decide([FromBody] DecideShortLetTaxCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await ScopeAsync() is not { } scope || User.GetUserId() is not { Length: > 0 } userId)
            return Forbid();

        var decision = await checks.DecideAsync(scope, command, userId, HttpContext.RequestAborted);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(ShortLetTaxDecisionDto.From(decision));
    }

    [HttpGet("api/owners/{ownerId:guid}/tax-checks")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
    [ProducesResponseType(typeof(OwnerUnitsCheckDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerUnitsCheckDto>> GetUnitsCheck(Guid ownerId)
    {
        if (await ScopeAsync() is not { } scope)
            return Forbid();

        Response.Headers.CacheControl = "private, no-store";
        return Ok(OwnerUnitsCheckDto.From(await checks.GetUnitsCheckAsync(scope, ownerId, HttpContext.RequestAborted)));
    }

    [HttpPut("api/owners/{ownerId:guid}/external-short-let-units")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerUnitsCheckDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerUnitsCheckDto>> DeclareExternalUnits(Guid ownerId, [FromBody] DeclareExternalUnitsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await ScopeAsync() is not { } scope)
            return Forbid();

        var check = await checks.DeclareExternalUnitsAsync(scope, ownerId, request.Units, User.GetUserId(), HttpContext.RequestAborted);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(OwnerUnitsCheckDto.From(check));
    }

    private async Task<HostScope?> ScopeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct);
    }
}
