using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The question of the VAT of a cleaning recharged to the owner (FIX/BE-CLEANING-VAT, decision 4 of 20/11/2026): «La ditta di pulizie ha applicato l'IVA su questa
/// fattura?», the action <c>StateCleaningVat</c> of the «Da fare» <c>CleaningVatToState</c>, whose entity id is the cleaning request.
/// </summary>
/// <remarks>
/// <para><b>Who.</b> The same people as the other host actions on a request: <c>property.read</c> to read the question and <c>property.write</c> to answer it, on the
/// property of the request, in the context of the request (short-rent or long-rent: the policies of the shared property core let both in, the request's own context decides),
/// within the caller's scope (a collaborator «Solo alcuni» reaches only the requests of the properties it was given). «Ricorda per questa ditta» decides the cleanings of every
/// property of the organization, so only a caller of the whole organization may tick it (403 <c>cleaning_vat_remember_forbidden</c>).</para>
/// <para><b>Whose.</b> A request of another organization, of a property out of reach, of the supplier's showcase or of another category is the 404 <c>service_request_not_found</c>
/// of one that does not exist.</para>
/// </remarks>
[ApiController]
[Route("api/service-requests/{id:guid}/cleaning-vat")]
public class ServiceRequestCleaningVatController(
    ICleaningVatService cleaningVat,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The question of a completed cleaning: the country and the rate of «Sì» (the standard rate of the country of the cleaning on its day; <c>null</c> when the table has
    /// none), the answer given, what the organization remembers for the company, what the invoice of the agency uses, and whether the caller may remember.
    /// </summary>
    /// <response code="200">The question.</response>
    /// <response code="403">The caller may not read the property of the request.</response>
    /// <response code="404"><c>service_request_not_found</c>.</response>
    [HttpGet]
    [Authorize(Policy = CasazenPolicies.SharedPropertyRead)]
    [ProducesResponseType(typeof(CleaningVatDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CleaningVatDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var scope = await ScopeOfCallerAsync(cancellationToken);
        if (scope is null)
            return RequestNotFound();

        var question = await cleaningVat.GetAsync(scope, id, cancellationToken);
        if (!await MayAsync(scope, question, write: false))
            return Forbid();

        return Ok(CleaningVatDto.From(question, scope.IsOrgWide));
    }

    /// <summary>
    /// The answer for a completed cleaning, <c>{ answer, remember }</c>: stored on the request, kept for the company with <c>remember</c>, and the «Da fare» of the question
    /// closed; the same answer again changes nothing, another one replaces it. Answers the question as it is now.
    /// </summary>
    /// <response code="200">The question with the answer.</response>
    /// <response code="403">The caller may not write the property of the request; <c>cleaning_vat_remember_forbidden</c> for a caller restricted to some properties that asks to remember.</response>
    /// <response code="404"><c>service_request_not_found</c>.</response>
    /// <response code="409"><c>service_request_state_changed</c>: another operation changed the request meanwhile.</response>
    /// <response code="422"><c>cleaning_vat_answer_invalid</c>, <c>cleaning_vat_not_completed</c>, <c>cleaning_vat_rate_unknown</c>, <c>cleaning_vat_remember_unknown</c>.</response>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
    [ProducesResponseType(typeof(CleaningVatDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CleaningVatDto>> Answer(Guid id, [FromBody] CleaningVatAnswerRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var scope = await ScopeOfCallerAsync(cancellationToken);
        if (scope is null)
            return RequestNotFound();

        var question = await cleaningVat.GetAsync(scope, id, cancellationToken);
        if (!await MayAsync(scope, question, write: true))
            return Forbid();

        // The remembered answer decides the cleanings of every property of the organization: never a caller restricted to some of them.
        if (body.Remember && !scope.IsOrgWide)
            return this.ApiProblem(StatusCodes.Status403Forbidden, CleaningVatErrors.RememberForbidden, CleaningVatErrors.RememberForbiddenMessageKey);

        var answered = await cleaningVat.AnswerAsync(scope, id, body.Answer, body.Remember, userId, cancellationToken);
        return Ok(CleaningVatDto.From(answered, scope.IsOrgWide));
    }

    /// <summary>The caller's scope on its org, or <c>null</c> when it has no org or reaches nothing (never widened).</summary>
    private async Task<HostScope?> ScopeOfCallerAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    /// <summary>
    /// The permission on the property of the request, in the context of the request: short-rent or long-rent. The request was read in the caller's org and scope, so its org is
    /// the scope's.
    /// </summary>
    private Task<bool> MayAsync(HostScope scope, CleaningVatQuestion question, bool write)
    {
        var resource = new HostResource(scope.OrgId, question.PropertyOwnerUserId, question.PropertyId);
        var operation = question.RentalContext == ServiceRequestRentalContext.LongRent
            ? write ? LongRentPropertyOperations.Write : LongRentPropertyOperations.Read
            : write ? PropertyOperations.Write : PropertyOperations.Read;
        return authorizationService.IsAuthorizedAsync(User, resource, operation);
    }

    private ObjectResult RequestNotFound() => ServiceRequestsController.ServiceRequestNotFound(this);
}
