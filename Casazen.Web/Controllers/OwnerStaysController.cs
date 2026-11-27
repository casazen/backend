using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The owner stays as the agency governs them (E07/F07/US01/T01, NF-111): the requests to approve or reject, the cancellation, the block to send again when
/// the channel manager refused it, and the rules of the owner stays of each property and owner.
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> <see cref="CasazenPolicies.AgencyOwnerView"/> to read, <see cref="CasazenPolicies.AgencyOwnerManage"/> to decide and to write the
/// rules. The owner stays are a whole-org function, as the owner accounting: a caller restricted to some properties (a collaborator «Solo alcuni») is
/// refused (403) even with the permission, since an owner and its rules span properties it does not reach. A stay or a rule of another org is the same 404
/// as a missing one.</para>
/// <para>Behind <c>Features:AgencyTier</c>: 404 while it is off. Every answer is private and never cached.</para>
/// </remarks>
[ApiController]
[Route("api/owner-stays")]
[Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
[FeatureGate(FeatureFlags.AgencyTier)]
[PrivateAnswer]
[EnableRateLimiting(RateLimitPolicies.OwnerPortalRead)]
public sealed class OwnerStaysController(
    IOwnerStayService stays,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>The owner stays of the org, newest arrival first, narrowed by <c>status</c> (<c>Requested</c>, <c>Approved</c>, ...) and <c>propertyId</c>.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerStayAdminView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerStayAdminView>>> List(
        [FromQuery] OwnerStayStatus? status,
        [FromQuery] Guid? propertyId,
        CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await stays.ListForAgencyAsync(orgId, status, propertyId, cancellationToken));
    }

    /// <summary>Approves a request: its block is created and sent to the channel manager. 409 <c>owner_stay_not_pending</c>, <c>owner_stay_dates_unavailable</c>.</summary>
    [HttpPost("{stayId:guid}/approve")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [ProducesResponseType(typeof(OwnerStayAdminView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerStayAdminView>> Approve(Guid stayId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        return Ok(await stays.ApproveAsync(orgId, stayId, userId, cancellationToken));
    }

    /// <summary>Rejects a request. 409 <c>owner_stay_not_pending</c>.</summary>
    [HttpPost("{stayId:guid}/reject")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [ProducesResponseType(typeof(OwnerStayAdminView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnerStayAdminView>> Reject(Guid stayId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        return Ok(await stays.RejectAsync(orgId, stayId, userId, cancellationToken));
    }

    /// <summary>Cancels a stay that has not ended; an approved one gives its own block back. 409 <c>owner_stay_not_cancellable</c>.</summary>
    [HttpPost("{stayId:guid}/cancel")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [ProducesResponseType(typeof(OwnerStayAdminView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnerStayAdminView>> Cancel(Guid stayId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        return Ok(await stays.CancelByAgencyAsync(orgId, stayId, userId, cancellationToken));
    }

    /// <summary>Sends again the block (or its release) the channel manager refused; a stay with nothing failed answers as it is.</summary>
    [HttpPost("{stayId:guid}/retry-sync")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [ProducesResponseType(typeof(OwnerStayAdminView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnerStayAdminView>> RetrySync(Guid stayId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        return Ok(await stays.RetrySyncAsync(orgId, stayId, userId, cancellationToken));
    }

    /// <summary>The rules of the owner stays of the org, or of one property.</summary>
    [HttpGet("rules")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerStayRuleView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerStayRuleView>>> ListRules([FromQuery] Guid? propertyId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await stays.ListRulesAsync(orgId, propertyId, cancellationToken));
    }

    /// <summary>
    /// Writes the rule of a property (<c>propertyOwnerId</c> null) or of one of its owners; a second save replaces the first. 422 <c>owner_stay_rule_invalid</c>
    /// with the field; 404 <c>owner_stay_property_not_found</c>, <c>owner_stay_owner_not_found</c>.
    /// </summary>
    [HttpPut("rules")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [ProducesResponseType(typeof(OwnerStayRuleView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerStayRuleView>> SaveRule([FromBody] OwnerStayRuleInput body, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        return Ok(await stays.SaveRuleAsync(orgId, body, userId, cancellationToken));
    }

    /// <summary>Removes a rule: the owner or the property falls back to the next one. 404 <c>owner_stay_rule_not_found</c>.</summary>
    [HttpDelete("rules/{ruleId:guid}")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRule(Guid ruleId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        await stays.DeleteRuleAsync(orgId, ruleId, userId, cancellationToken);
        return NoContent();
    }

    /// <summary>The org of the caller when it reaches the whole org; <c>null</c> for a caller restricted to some properties (or with no org).</summary>
    private async Task<Guid?> OrgAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return null;

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        return scope is { IsOrgWide: true } ? orgId : null;
    }

    private ObjectResult Forbidden() => this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");
}
