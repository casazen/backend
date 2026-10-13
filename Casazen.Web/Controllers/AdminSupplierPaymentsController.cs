using System.Globalization;
using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The platform admin's tools for the payment of the suppliers' services (SP-15b, decisions D2 and D3): the list and the detail of the
/// payments (the work queue is the ones that <c>NeedsReview</c>), the refund (<c>refund_application_fee=true</c>: the commission goes
/// back in full for a refund in full, in proportion for a partial one), the commission of a supplier (its own percentage, or a free
/// period) and the monthly commission export for the manual invoice. <c>AdminOnly</c>.
/// </summary>
/// <remarks>
/// These are the only admin writes on the money of the services; the payers and the suppliers never reach them. Answers carry ids,
/// names, amounts and Stripe's codes, never an email, a phone or an address. Runbook <c>docs/runbooks/stripe.md</c> §
/// "Services of the suppliers (SP-15)".
/// </remarks>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminSupplierPaymentsController(
    ISupplierPaymentAdminService adminPayments,
    ISupplierPaymentRefundService refunds) : ControllerBase
{
    /// <summary>
    /// The payments, newest first, paginated in SQL. <c>status</c> is a payment state (anything else leaves the filter off),
    /// <c>supplierOrgId</c> one supplier, <c>late</c> the late ones (true) or the ones on time (false), <c>from</c>/<c>to</c> the
    /// creation time.
    /// </summary>
    [HttpGet("supplier-payments")]
    [ProducesResponseType(typeof(PagedResultDto<AdminServicePaymentDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<AdminServicePaymentDto>>> List(
        [FromQuery] string? status,
        [FromQuery] Guid? supplierOrgId,
        [FromQuery] bool? late,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // Out-of-range values would become a negative OFFSET/LIMIT in SQL, i.e. a 500: clamp them.
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, ServicePaymentLimits.AdminMaxPageSize);

        // Enum.TryParse alone also accepts a numeric string with no declared member: it would filter by nothing.
        ServicePaymentStatus? statusFilter = EnumNames.TryParseDefined<ServicePaymentStatus>(status, out var parsed) ? parsed : null;

        var (items, total) = await adminPayments.ListAsync(
            new AdminServicePaymentQuery(statusFilter, supplierOrgId, late, from, to, page, pageSize), cancellationToken);

        SetNoStore();
        return Ok(new PagedResultDto<AdminServicePaymentDto>
        {
            Items = items.Select(AdminServicePaymentDto.From).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>One payment with its refunds. 404 <c>service_payment_not_found</c>.</summary>
    [HttpGet("service-payments/{paymentId:guid}")]
    [ProducesResponseType(typeof(AdminServicePaymentDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminServicePaymentDetailDto>> Get(Guid paymentId, CancellationToken cancellationToken)
    {
        SetNoStore();
        return Ok(AdminServicePaymentDetailDto.From(await adminPayments.GetAsync(paymentId, cancellationToken)));
    }

    /// <summary>
    /// Refunds a payment paid online, in full or in part. The refund is created on the supplier's Stripe account (a direct charge:
    /// it is paid from the supplier's balance) with <c>refund_application_fee=true</c>, under the idempotency key
    /// <c>service-charge-refund:{payment}:{n}</c>; the amounts of the refunds being made are reserved. The answer is the refund as it
    /// stands: <c>Succeeded</c>, <c>Pending</c> (Stripe has not answered yet: the sync job completes it) or <c>Failed</c> with its
    /// code (Stripe refused it and nothing was refunded). 404 <c>service_payment_not_found</c>; 422
    /// <c>service_payment_not_refundable</c>, <c>service_payment_refund_offline</c>, <c>service_payment_nothing_to_refund</c>,
    /// <c>service_payment_refund_amount_invalid</c>, <c>service_payment_refund_amount_exceeds</c>.
    /// </summary>
    [HttpPost("service-payments/{paymentId:guid}/refund")]
    [ProducesResponseType(typeof(AdminServicePaymentRefundDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AdminServicePaymentRefundDto>> Refund(
        Guid paymentId,
        [FromBody] RefundServicePaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var refund = await refunds.RefundAsync(paymentId, request.AmountCents, request.Reason, ActorUserId(), cancellationToken);
        SetNoStore();
        return Ok(AdminServicePaymentRefundDto.From(refund));
    }

    /// <summary>
    /// The commission applied to a supplier: the platform's percentage, its own override (and when it ends) and what a payment
    /// created now is charged. 404 <c>supplier_not_found</c>.
    /// </summary>
    [HttpGet("suppliers/{orgId:guid}/commission")]
    [ProducesResponseType(typeof(SupplierCommissionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierCommissionDto>> GetCommission(Guid orgId, CancellationToken cancellationToken)
    {
        SetNoStore();
        return Ok(SupplierCommissionDto.From(await adminPayments.GetCommissionAsync(orgId, cancellationToken)));
    }

    /// <summary>
    /// Sets the supplier's own commission (0 to the highest the configuration allows, two decimals at most; 0 is a free period),
    /// optionally until a date, or removes it (<c>percent</c> null). It applies to the payments created afterwards: a payment keeps
    /// the percentage it was created with. The reason is required; the change is recorded in the audit trail with the admin, the old
    /// and the new value. 404 <c>supplier_not_found</c>; 422 <c>supplier_commission_invalid</c>, <c>supplier_commission_until_invalid</c>.
    /// </summary>
    [HttpPut("suppliers/{orgId:guid}/commission")]
    [ProducesResponseType(typeof(SupplierCommissionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierCommissionDto>> SetCommission(
        Guid orgId,
        [FromBody] SetSupplierCommissionRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var setting = await adminPayments.SetCommissionAsync(
            orgId, request.Percent, request.Until, request.Reason, ActorUserId(), cancellationToken);
        SetNoStore();
        return Ok(SupplierCommissionDto.From(setting));
    }

    /// <summary>
    /// The commission of a month (<c>month=yyyy-MM</c>, Europe/Rome) as CSV for the manual invoice: one line per payment collected
    /// through Stripe in the month and one per refund that succeeded in it (negative amounts), with the supplier's P.IVA and the gross
    /// amount, money in integer cents. The VAT on the commission is open (<c>[CONSULENTE FISCALE]</c>): the file repeats the
    /// configured rate, empty until it is decided, and computes no VAT amount. 422 <c>service_payment_export_month_invalid</c> for a
    /// month that is not <c>yyyy-MM</c> or is in the future.
    /// </summary>
    [HttpGet("supplier-payments/export")]
    [Produces("text/csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Export([FromQuery] string? month, CancellationToken cancellationToken)
    {
        if (!TryParseMonth(month, out var year, out var monthNumber))
            throw new DomainRuleException(ServicePaymentErrors.ExportMonthInvalid, ServicePaymentErrors.ExportMonthInvalidMessageKey);

        var export = await adminPayments.ExportCommissionsAsync(year, monthNumber, cancellationToken);

        // UTF-8 with a byte order mark: a spreadsheet opens the accents of a supplier's name right.
        var csv = Encoding.UTF8.GetBytes(CommissionExportCsv.Write(export));
        var body = new byte[Encoding.UTF8.Preamble.Length + csv.Length];
        Encoding.UTF8.Preamble.CopyTo(body);
        csv.CopyTo(body, Encoding.UTF8.Preamble.Length);

        SetNoStore();
        return File(body, CommissionExportCsv.ContentType + "; charset=utf-8", CommissionExportCsv.FileName(year, monthNumber));
    }

    /// <summary><c>yyyy-MM</c> and nothing else.</summary>
    internal static bool TryParseMonth(string? value, out int year, out int month)
    {
        year = 0;
        month = 0;
        if (value is not { Length: 7 } || value[4] != '-')
            return false;

        return int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year)
               && int.TryParse(value.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month)
               && month is >= 1 and <= 12
               && year >= 2000;
    }

    /// <summary>Auth0 subject of the admin: AdminOnly guarantees an authenticated user, whose token always has one.</summary>
    private string ActorUserId() =>
        User.GetUserId() ?? throw new UnauthorizedAccessException("Admin without subject claim");

    /// <summary>The answers describe money and name suppliers: nothing is cached by the browser or a proxy.</summary>
    private void SetNoStore() => Response.Headers.CacheControl = "private, no-store";
}
