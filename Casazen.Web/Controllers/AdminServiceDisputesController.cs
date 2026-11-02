using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The platform admin's tools for the complaints about the jobs paid through CasaZen (SP-15c-c, decision VT-D16): the work queue, one dispute with the
/// words of both parties, and the decision — for the supplier (it is paid) or for the customer (the payer gets back what the admin decides, at most the
/// price minus the commission; CasaZen keeps its commission; the supplier is penalized). <c>AdminOnly</c>.
/// </summary>
/// <remarks>
/// The decision is audited (<c>SupplierAdminAuditAction.ServiceDisputeResolved</c>) and moves money only through the refund machinery of the payments
/// service, under the payment lock. Answers carry ids, names of the supplier, amounts and the words of the two parties (the detail only); never an e-mail,
/// a phone or an address. Runbook <c>docs/runbooks/suppliers.md</c> section 38.10.
/// </remarks>
[ApiController]
[Route("api/admin/service-payment-disputes")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public sealed class AdminServiceDisputesController(IServiceDisputeService disputes) : ControllerBase
{
    /// <summary>
    /// The disputes, paginated in SQL: with <c>openOnly</c> the work queue (the ones not decided yet), oldest first; otherwise the newest first.
    /// <c>status</c> is one of the dispute states (anything else leaves the filter off), <c>supplierOrgId</c> one supplier.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<AdminServiceDisputeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<AdminServiceDisputeDto>>> List(
        [FromQuery] string? status,
        [FromQuery] Guid? supplierOrgId,
        [FromQuery] bool openOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // Out-of-range values would become a negative OFFSET/LIMIT in SQL, i.e. a 500: clamp them.
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, ServicePaymentLimits.AdminMaxPageSize);

        // Enum.TryParse alone also accepts a numeric string with no declared member: it would filter by nothing.
        ServiceDisputeStatus? statusFilter = EnumNames.TryParseDefined<ServiceDisputeStatus>(status, out var parsed) ? parsed : null;

        var (items, total) = await disputes.ListDisputesAsync(
            new AdminServiceDisputeQuery(statusFilter, supplierOrgId, openOnly, page, pageSize), cancellationToken);

        SetNoStore();
        return Ok(new PagedResultDto<AdminServiceDisputeDto>
        {
            Items = items.Select(AdminServiceDisputeDto.From).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>One dispute with the words of both parties and the state of the money. 404 <c>service_dispute_not_found</c>.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AdminServiceDisputeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminServiceDisputeDetailDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        SetNoStore();
        return Ok(AdminServiceDisputeDetailDto.From(await disputes.GetDisputeAsync(id, cancellationToken)));
    }

    /// <summary>
    /// Decides a dispute that reached the admins. <c>for_supplier</c>: the payment goes back to held, due now, and the supplier is paid at the next run of
    /// the funds job. <c>for_customer</c>: the refund of <c>refundCents</c> (or of all the supplier's part that is left) is reserved in the same transaction
    /// and asked of Stripe after it; the payment stays disputed until the refund has succeeded, CasaZen keeps its commission and the supplier is penalized.
    /// The same decision twice answers the same and moves nothing. 404 <c>service_dispute_not_found</c>; 409 <c>service_dispute_awaiting_supplier</c>
    /// (the supplier still has time; <c>supplierReplyDueAt</c>), <c>service_dispute_state_changed</c> (another decision, or the payer withdrew),
    /// <c>service_dispute_payment_changed</c>; 422 <c>service_dispute_outcome_invalid</c>, <c>service_dispute_refund_invalid</c>,
    /// <c>service_dispute_refund_exceeds</c> (<c>maxRefundCents</c>), <c>service_dispute_invalid</c>.
    /// </summary>
    [HttpPost("{id:guid}/resolve")]
    [ProducesResponseType(typeof(AdminServiceDisputeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AdminServiceDisputeDetailDto>> Resolve(
        Guid id,
        [FromBody] ResolveServiceDisputeBody body,
        CancellationToken cancellationToken)
    {
        var detail = await disputes.ResolveDisputeAsync(id, body.ToRequest(), ActorUserId(), cancellationToken);
        SetNoStore();
        return Ok(AdminServiceDisputeDetailDto.From(detail));
    }

    private string ActorUserId() =>
        User.GetUserId() ?? throw new UnauthorizedAccessException("Admin without subject claim");

    /// <summary>The answers describe money and carry the words of two parties: nothing is cached by the browser or a proxy.</summary>
    private void SetNoStore() => Response.Headers.CacheControl = "private, no-store";
}
