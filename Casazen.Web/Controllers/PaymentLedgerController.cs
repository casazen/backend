using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/payment-ledger")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class PaymentLedgerController(
    IBookingPaymentLedgerService ledger,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    public const string InvalidRangeCode = "payment_ledger_invalid_range";

    /// <summary>
    /// The ledger of one booking: its lines and, when the collections of its Octorate reservation could not be read, <c>degraded: true</c> with the stored
    /// OTA amounts (never an error).
    /// </summary>
    [HttpGet("bookings/{bookingId:guid}")]
    [ProducesResponseType(typeof(PaymentLedgerBookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentLedgerBookingDto>> ForBooking(Guid bookingId)
    {
        var ct = HttpContext.RequestAborted;
        var booking = await hostResources.ForBookingAsync(bookingId, ct);
        if (booking is null || !await authorizationService.IsAuthorizedAsync(User, booking, PaymentOperations.Read))
            return NotFound();

        if (await ScopeAsync(ct) is not { } scope)
            return Unauthorized();

        var result = await ledger.GetForBookingAsync(scope, bookingId, ct);
        return result is null
            ? NotFound()
            : Ok(new PaymentLedgerBookingDto(
                result.BookingId,
                BookingCodes.Format(result.BookingCode),
                result.Channel,
                result.Currency,
                result.Lines.Select(PaymentLedgerLineDto.From).ToList(),
                result.Degraded));
    }

    [HttpGet]
    [ProducesResponseType(typeof(PaymentLedgerPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentLedgerPageDto>> List(
        [FromQuery] Guid? propertyId = null,
        [FromQuery] Guid? bookingId = null,
        [FromQuery(Name = "from")] DateOnly? fromDay = null,
        [FromQuery(Name = "to")] DateOnly? toDay = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = PaymentListPage.DefaultPageSize)
    {
        var ct = HttpContext.RequestAborted;
        if (fromDay is { } first && toDay is { } last && first > last)
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidRangeCode, "PaymentListInvalidRange");

        if (propertyId is { } property
            && (await hostResources.ForPropertyAsync(property, ct) is not { } resource
                || !await authorizationService.IsAuthorizedAsync(User, resource, PaymentOperations.Read)))
        {
            return NotFound();
        }

        if (bookingId is { } id
            && (await hostResources.ForBookingAsync(id, ct) is not { } booking
                || !await authorizationService.IsAuthorizedAsync(User, booking, PaymentOperations.Read)))
        {
            return NotFound();
        }

        if (await ScopeAsync(ct) is not { } scope)
            return Unauthorized();

        var result = await ledger.ListAsync(scope, new PaymentListCriteria(propertyId, bookingId, fromDay, toDay), page, pageSize, ct);
        return Ok(new PaymentLedgerPageDto(result.Items.Select(PaymentLedgerLineDto.From).ToList(), result.TotalCount, result.Page, result.PageSize));
    }

    private async Task<HostScope?> ScopeAsync(CancellationToken ct)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct);
    }
}
