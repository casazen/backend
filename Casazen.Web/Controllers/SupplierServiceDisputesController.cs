using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The supplier's side of the complaint about a job it did and is paid for through CasaZen (SP-15c-c, decision VT-D16): read the complaint against it and
/// reply to it, once, in its own words. Every route is scoped to the caller's own supplier org (its supplier link, never a value of the request): a request
/// of another supplier is a 404, as one without a complaint. The supplier has <c>SupplierPayments:DisputeSupplierReplyHours</c> (48) to reply; a reply that
/// comes later is still taken while the dispute is open. Not behind a feature flag: the money it concerns is already held. Runbook
/// <c>docs/runbooks/suppliers.md</c> section 38.10.
/// </summary>
/// <remarks>
/// What the supplier reads is the payer's text and its own reply, the state and the decision — never the admin's note (<see cref="ServiceDisputeSupplierDto"/>).
/// Errors: 404 <c>not_found</c> when the caller has no supplier org, <c>service_dispute_not_found</c> when the request has no complaint for it; 409
/// <c>service_dispute_not_open</c> (it was decided or taken back), <c>service_dispute_already_replied</c>; 422 <c>service_dispute_invalid</c> (<c>fields</c>: <c>text</c>).
/// </remarks>
[ApiController]
[Route("api/supplier/requests/{id:guid}/dispute")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public sealed class SupplierServiceDisputesController(
    IServiceDisputeService disputes,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ServiceDisputeSupplierDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ServiceDisputeSupplierDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var dispute = await disputes.FindDisputeForSupplierAsync(id, orgId.Value, cancellationToken) ?? throw ServiceDisputeErrors.DisputeNotFound();
        SetNoStore();
        return Ok(ServiceDisputeSupplierDto.From(dispute));
    }

    [HttpPost("reply")]
    [ProducesResponseType(typeof(ServiceDisputeSupplierDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceDisputeSupplierDto>> Reply(
        Guid id,
        [FromBody] ReplyToServiceDisputeRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var dispute = await disputes.ReplyToDisputeAsync(id, orgId.Value, request.Text, cancellationToken);
        SetNoStore();
        return Ok(ServiceDisputeSupplierDto.From(dispute));
    }

    private void SetNoStore() => Response.Headers.CacheControl = "private, no-store";
}
