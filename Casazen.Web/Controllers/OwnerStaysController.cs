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

    [HttpGet("rules")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerStayRuleView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerStayRuleView>>> ListRules([FromQuery] Guid? propertyId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await stays.ListRulesAsync(orgId, propertyId, cancellationToken));
    }

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

    private Task<Guid?> OrgAsync(CancellationToken cancellationToken) =>
        HostCallerResolver.OrgWideAsync(User, orgContextResolver, hostScopeResolver, cancellationToken);

    private ObjectResult Forbidden() => this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");
}
