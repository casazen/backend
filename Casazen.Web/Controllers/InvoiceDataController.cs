using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The invoice data of a booking (FIS-01, NF-81, E06/F02/US01/T02): who an invoice is made out to, read and corrected by the host in the booking.
/// </summary>
/// <remarks>
/// TN-3: the booking is authorized as a <see cref="Casazen.Core.Authorization.HostResource"/> of its property (<see cref="BookingOperations"/>):
/// a booking of another org is invisible and answers 404 like a missing one, and one the caller can see but may not use answers 403 (a
/// collaborator «Solo alcuni» on a property it was not given, or a role without the right). Reading needs <c>payment.read</c> and writing
/// <c>payment.write</c> besides: the data is what an invoice is made of, never shown to a role that does not see the money. The host's save always
/// has the source <see cref="InvoiceDataSource.Host"/>, which no import and no guest can overwrite.
/// </remarks>
[ApiController]
[Route("api/bookings/{bookingId:guid}/invoice-data")]
public class InvoiceDataController(
    IInvoiceDataService invoiceData,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<InvoiceDataController> logger) : ControllerBase
{
    /// <summary>
    /// Who the invoice of the booking is for: the kind of recipient, the name, the fiscal code, the VAT number, the address, the code of the
    /// recipient on the SDI and the PEC, the source of the data and <c>invoiceRequested</c>. A booking with no data answers the empty object. 404 for
    /// a booking of another org, 403 for one the caller may not read.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = CasazenPolicies.PaymentRead)]
    [ProducesResponseType(typeof(InvoiceDataDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InvoiceDataDto>> Get(Guid bookingId, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "booking_not_found", "BookingNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Read))
        {
            logger.LogWarning("User {UserId} denied booking.read on the invoice data of booking {BookingId}", User.GetUserId(), bookingId);
            return Forbid();
        }

        var view = await invoiceData.GetAsync(bookingId, cancellationToken);
        return Ok(view is null ? InvoiceDataDto.Empty(bookingId) : InvoiceDataDto.From(view));
    }

    /// <summary>
    /// Saves who the invoice is for, with the source <c>Host</c>: what the host sends replaces what is stored (a member left out keeps its value, one
    /// sent empty clears it) and no import and no guest overwrites it afterwards. Answers the data as saved. 422 <c>invoice_data_fiscal_code_invalid</c>,
    /// <c>invoice_data_vat_number_invalid</c>, <c>invoice_data_recipient_code_invalid</c>, <c>invoice_data_pec_invalid</c>,
    /// <c>invoice_data_recipient_required</c> or <c>invoice_data_field_invalid</c>, each with the <c>field</c> and the list of <c>errors</c>. 404 for a
    /// booking of another org, 403 for one the caller may not change.
    /// </summary>
    [HttpPut]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [ProducesResponseType(typeof(InvoiceDataDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<InvoiceDataDto>> Put(
        Guid bookingId,
        [FromBody] SaveInvoiceDataRequest request,
        CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "booking_not_found", "BookingNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Write))
        {
            logger.LogWarning("User {UserId} denied booking.write on the invoice data of booking {BookingId}", User.GetUserId(), bookingId);
            return Forbid();
        }

        var saved = await invoiceData.SaveAsync(
            bookingId, request.ToCommand(), InvoiceDataSource.Host, User.GetUserId(), save: true, cancellationToken);

        logger.LogInformation("Invoice data of booking {BookingId} saved by user {UserId}", bookingId, User.GetUserId());
        return Ok(InvoiceDataDto.From(saved));
    }
}
