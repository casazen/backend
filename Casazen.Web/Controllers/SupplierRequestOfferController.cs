using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The offer of the supplier on a request on measure, in the supplier console (SP-10b-b): after the conversation of
/// <see cref="SupplierRequestThreadController"/>, the supplier says what it would do, for how much and when, and the customer accepts it or turns
/// it down inside CasaZen. It is the quote of SP-04b under the vocabulary of the conversation (decision SP-D30: one engine, not a second
/// mechanism). Every route is scoped to the caller's own supplier org (its supplier link, never a value of the request), for the requests of the kind
/// <c>Custom</c> only: any other id — a host's request, a booking, one of another supplier, one that does not exist — is the same 404. Not behind a
/// feature flag (only the showcase flag can make such a request exist). Runbook <c>docs/runbooks/suppliers.md</c> section 34.
/// </summary>
[ApiController]
[Route("api/supplier/requests")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierRequestOfferController(
    IServiceRequestOfferActions offers,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    /// <summary>
    /// Answers a new request on measure with an offer: <c>lines</c> (1 to 20, each a <c>label</c>, a <c>quantity</c> and a <c>unitAmountCents</c>; the
    /// total is what they add up to), the time (<c>startUtc</c>, <c>endUtc</c>) and an optional <c>note</c>. The request stays <c>Richiesto</c> with
    /// the offer waiting for the customer, who is e-mailed and has <c>Suppliers:Showcase:ProposalResponseMinutes</c> (24 hours) to answer, then the
    /// request is cancelled. A new offer replaces the one that waits (the customer who read the old one gets 409 on its answer). The time is judged by
    /// the supplier's agenda under its calendar lock but not held. 404 <c>service_request_not_found</c>; 400 <c>validation_error</c> for a malformed
    /// body; 422 <c>service_request_offer_invalid</c> (lines, quantities, prices or total outside the limits), <c>service_request_cannot_offer</c>
    /// (the request is not new), <c>service_request_time_invalid</c> (no end for a request that names no service, or an end not after the start),
    /// <c>service_request_supplier_not_active</c>; 409 <c>supplier_slot_unavailable</c> / <c>supplier_commitment_conflict</c> (a time the supplier
    /// cannot do), <c>service_request_state_changed</c>.
    /// </summary>
    [HttpPost("{id:guid}/offer")]
    [ProducesResponseType(typeof(SupplierRequestOfferResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierRequestOfferResponse>> SendOffer(
        Guid id,
        [FromBody] SendSupplierRequestOfferRequest request,
        CancellationToken cancellationToken)
    {
        // A write: the org comes only from the caller's own supplier link, like take and reject.
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (orgId is null || userId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var updated = await offers.SendOfferAsync(
            id,
            orgId.Value,
            userId,
            new SendServiceRequestOfferCommand(
                (request.Lines ?? []).Select(line => new ServiceRequestOfferLine(line.Label, line.Quantity, line.UnitAmountCents)).ToList(),
                request.StartUtc!.Value,
                request.EndUtc,
                request.Note),
            cancellationToken);
        return Ok(ServiceRequestOfferMapper.ToResponse(updated));
    }

    /// <summary>
    /// Takes the offer back: the request stays new and the conversation goes on (no clock runs on the supplier). 404 <c>service_request_not_found</c>;
    /// 422 <c>service_request_no_offer</c> (none waits), <c>service_request_supplier_not_active</c>; 409 <c>service_request_state_changed</c>.
    /// </summary>
    [HttpPost("{id:guid}/offer/withdraw")]
    [ProducesResponseType(typeof(SupplierRequestOfferResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierRequestOfferResponse>> WithdrawOffer(Guid id, CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var updated = await offers.WithdrawOfferAsync(id, orgId.Value, cancellationToken);
        return Ok(ServiceRequestOfferMapper.ToResponse(updated));
    }
}
