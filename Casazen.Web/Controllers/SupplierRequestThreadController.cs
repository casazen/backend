using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The conversation of a request on measure, in the supplier console (SP-10b): the supplier reads what the customer wrote and answers, inside
/// CasaZen (no public contact, no attachment). Every route is scoped to the caller's own supplier org (its supplier link, never a value of the
/// request), for the requests of the kind <c>Custom</c> only: any other id — a host's request, a booking, one of another supplier, one that does
/// not exist — is the same 404. Not behind a feature flag (only the showcase flag can make such a request exist). Runbook
/// <c>docs/runbooks/suppliers.md</c> section 34.
/// </summary>
[ApiController]
[Route("api/supplier/requests")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierRequestThreadController(
    IServiceRequestThreadActions thread,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    /// <summary>
    /// The thread: the messages in the order they were written, whether the supplier can add one, and the time it has to answer by while it did
    /// not. Reading marks the customer's messages as read. 404 <c>not_found</c>.
    /// </summary>
    [HttpGet("{id:guid}/thread")]
    [ProducesResponseType(typeof(SupplierRequestThreadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierRequestThreadDto>> GetThread(Guid id, CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        return Ok(SupplierRequestThreadMapper.ToDto(await thread.GetThreadAsSupplierAsync(id, orgId.Value, cancellationToken)));
    }

    /// <summary>
    /// Adds a message of the supplier to the thread; the first one stops the clock of the hour the supplier has to answer, and the customer is
    /// told by e-mail (a link, never the words). 201 with the message. 404 <c>not_found</c>; 422 <c>service_request_message_invalid</c>,
    /// <c>service_request_thread_full</c>, <c>supplier_not_active</c>; 409 <c>service_request_thread_closed</c> (refused, cancelled or done) and
    /// <c>service_request_state_changed</c>.
    /// </summary>
    [HttpPost("{id:guid}/messages")]
    [ProducesResponseType(typeof(SupplierRequestMessageDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierRequestMessageDto>> PostMessage(
        Guid id,
        [FromBody] PostSupplierRequestMessageRequest request,
        CancellationToken cancellationToken)
    {
        // A write: the org comes only from the caller's own supplier link, like take and reject.
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var message = await thread.PostMessageAsSupplierAsync(id, orgId.Value, request.Body, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, SupplierRequestThreadMapper.ToDto(message));
    }
}
