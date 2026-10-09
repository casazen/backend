using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The supplier console's work on its requests (SP-04): take several new requests at once, the home of the console ("Oggi"), and
/// its activation checklist. Every route is scoped to the caller's own supplier org (its supplier link, never a value of the
/// request). Not behind a feature flag. The inbox (<c>GET api/supplier/inbox</c>) and the single transitions
/// (<c>api/service-requests/{id}/take|start|complete|reject|cancel|propose-time</c>) are in <see cref="SupplierProfileController"/>
/// and <see cref="ServiceRequestsController"/>. Runbook <c>docs/runbooks/suppliers.md</c> section 21.
/// </summary>
/// <remarks>
/// Errors: 404 <c>not_found</c> when the caller has no supplier org; 400 <c>validation_error</c> for a batch with no id or with
/// more than 20.
/// </remarks>
[ApiController]
[Route("api/supplier")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierRequestsController(
    IServiceRequestService serviceRequests,
    ISupplierTodayService today,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    /// <summary>
    /// Takes several new requests as they are (at most 20 ids; a repeated id counts once). Each request is taken on its own, as
    /// <c>POST api/service-requests/{id}/take</c> would: one that cannot be taken (no longer new, changed meanwhile, not the
    /// supplier's, the supplier suspended) does not stop the others and comes back with its <c>code</c> and localized
    /// <c>message</c>. The answer is 200 with one result per request, in the order received.
    /// </summary>
    [HttpPost("inbox/accept")]
    [ProducesResponseType(typeof(AcceptServiceRequestsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AcceptServiceRequestsResponse>> AcceptMany(
        [FromBody] AcceptServiceRequestsRequest request,
        CancellationToken cancellationToken)
    {
        // A write: the org comes only from the caller's own supplier link, like take and reject.
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (orgId is null || userId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var results = await serviceRequests.AcceptManyAsync(orgId.Value, userId, request.Ids!, cancellationToken);

        return Ok(new AcceptServiceRequestsResponse
        {
            Results = results.Select(result => new AcceptServiceRequestResultDto
            {
                Id = result.Id,
                Accepted = result.Accepted,
                Status = result.Status?.ToString(),
                Code = result.Code,
                Message = result.MessageKey is null
                    ? null
                    : ApiProblemDetails.Create(
                        HttpContext, StatusCodes.Status422UnprocessableEntity, result.Code, result.MessageKey, result.MessageArgs).Detail,
            }).ToList(),
            Accepted = results.Count(result => result.Accepted),
            Failed = results.Count(result => !result.Accepted),
        });
    }

    /// <summary>
    /// The home of the console (SP-04): the jobs of today (taken, in progress, completed or paid, the first in time first), the new
    /// requests to answer with the most urgent first (and how many there are), the earnings of the month from the <b>final amounts
    /// of the jobs completed</b> (<c>earnings.estimated</c> is true until the payments of SP-15 exist), what is left to collect,
    /// and the average time to answer (<c>takenAt − createdAt</c> over the last 90 days). The items follow the inbox's privacy
    /// rule (decision D9).
    /// </summary>
    [HttpGet("today")]
    [ProducesResponseType(typeof(SupplierTodayDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierTodayDto>> GetToday(CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        return Ok(SupplierServiceRequestMapper.ToTodayDto(await today.GetTodayAsync(orgId.Value, cancellationToken)));
    }

    /// <summary>
    /// The activation checklist (SP-04), each step from what the supplier really did: profile complete, published services, working
    /// hours saved (<c>hoursConfiguredAt</c>), showcase online, first request answered. <c>paymentsActive</c> is <b>always
    /// <c>null</c> for now</b>: payments through CasaZen are not available yet (SP-14, SP-15), which is not a "no".
    /// </summary>
    [HttpGet("checklist")]
    [ProducesResponseType(typeof(SupplierChecklistDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierChecklistDto>> GetChecklist(CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        var checklist = orgId is null ? null : await today.GetChecklistAsync(orgId.Value, cancellationToken);
        if (checklist is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        return Ok(SupplierServiceRequestMapper.ToChecklistDto(checklist));
    }
}
