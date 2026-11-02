using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The host's side of the complaint about a job paid through CasaZen (SP-15c-c, decisions VT-D1 and VT-D16): "Segnala un problema" for a completed job
/// whose money CasaZen holds, and taking the complaint back. The host proves who it is by owning the request (another org's request, and a showcase one,
/// are not found) and holding the same permission as for "all fine" (<c>servicerequest.write</c> in short-rent, <c>property.write</c> in long-rent, on
/// the request's property). The complaint itself, its window, its money and its e-mails are <see cref="IServiceDisputeService"/>'s.
/// </summary>
/// <remarks>
/// The request DTO of the host has no <c>payment</c> block yet (follow-up SP-15c-b2): until it does, the host's page learns that a complaint is open from
/// <c>GET …/problem</c>, which answers 404 <c>service_dispute_not_found</c> when there is none. Errors: 404 <c>service_request_not_found</c> outside the
/// caller's scope; 422 <c>service_dispute_not_reportable</c>, <c>service_dispute_window_closed</c> (<c>windowEndedAt</c>), <c>service_dispute_too_early</c>,
/// <c>service_dispute_invalid</c> (<c>fields</c>); 409 <c>service_dispute_not_open</c>.
/// </remarks>
public abstract class HostServiceRequestDisputesControllerBase(
    IServiceRequestService serviceRequests,
    IServiceDisputeService disputes,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>The rental context whose requests this door reaches.</summary>
    protected abstract ServiceRequestRentalContext Context { get; }

    /// <summary>The permission the host needs on the request's property.</summary>
    protected abstract HostOperationRequirement WriteOperation { get; }

    [NonAction]
    protected async Task<ActionResult<ServiceDisputePayerDto>> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var (scope, failure) = await AuthorizeAsync(id, cancellationToken);
        if (scope is null)
            return failure!;

        var dispute = await disputes.FindDisputeForPayerAsync(id, cancellationToken) ?? throw ServiceDisputeErrors.DisputeNotFound();
        SetNoStore();
        return Ok(ServiceDisputePayerDto.From(dispute));
    }

    [NonAction]
    protected async Task<ActionResult<ServiceDisputePayerDto>> ReportAsync(
        Guid id,
        ReportServiceProblemRequest request,
        CancellationToken cancellationToken)
    {
        var (scope, failure) = await AuthorizeAsync(id, cancellationToken);
        if (scope is null)
            return failure!;

        if (!ServiceDisputeRules.TryParseReason(request.Reason, out var reason))
            throw ServiceDisputeErrors.InvalidFields(ServiceDisputeLimits.ReasonField);

        var dispute = await disputes.OpenDisputeAsync(
            new OpenServiceDisputeRequest(id, ServiceDisputeOpener.Host, reason, request.Text, scope.OrgId, User.GetUserId()),
            cancellationToken);
        SetNoStore();
        return Ok(ServiceDisputePayerDto.From(dispute));
    }

    [NonAction]
    protected async Task<ActionResult<ServiceDisputePayerDto>> WithdrawAsync(Guid id, CancellationToken cancellationToken)
    {
        var (scope, failure) = await AuthorizeAsync(id, cancellationToken);
        if (scope is null)
            return failure!;

        var dispute = await disputes.WithdrawDisputeAsync(id, ServiceDisputeOpener.Host, scope.OrgId, cancellationToken);
        SetNoStore();
        return Ok(ServiceDisputePayerDto.From(dispute));
    }

    /// <summary>The host's org and the request it owns, with the permission to act on its property; otherwise the failure to answer with.</summary>
    private async Task<(HostScope? Scope, ActionResult? Failure)> AuthorizeAsync(Guid id, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return (null, Unauthorized());

        var existing = await serviceRequests.GetByIdForHostAsync(id, scope, Context, cancellationToken);
        if (existing?.Property is null)
            return (null, ServiceRequestsController.ServiceRequestNotFound(this));

        var resource = new HostResource(existing.OrgId, existing.Property.OwnerId, existing.PropertyId);
        return await authorizationService.IsAuthorizedAsync(User, resource, WriteOperation) ? (scope, null) : (null, Forbid());
    }

    /// <summary>The answers carry the words of the host: nothing is cached by the browser or a proxy.</summary>
    private void SetNoStore() => Response.Headers.CacheControl = "private, no-store";
}

/// <summary>The complaint about a job of a short-rent stay: <c>api/service-requests/{id}/…</c>.</summary>
[ApiController]
[Route("api/service-requests")]
[Authorize(Policy = CasazenPolicies.ServiceRequestWrite)]
public sealed class ServiceRequestDisputesController(
    IServiceRequestService serviceRequests,
    IServiceDisputeService disputes,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver)
    : HostServiceRequestDisputesControllerBase(serviceRequests, disputes, authorizationService, orgContextResolver, hostScopeResolver)
{
    protected override ServiceRequestRentalContext Context => ServiceRequestRentalContext.ShortRent;

    protected override HostOperationRequirement WriteOperation => ServiceRequestOperations.Write;

    /// <summary>The complaint about the job as the host reads it. 404 <c>service_dispute_not_found</c> when there is none.</summary>
    [HttpGet("{id:guid}/problem")]
    [ProducesResponseType(typeof(ServiceDisputePayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<ActionResult<ServiceDisputePayerDto>> Get(Guid id, CancellationToken cancellationToken) => ReadAsync(id, cancellationToken);

    /// <summary>
    /// "Segnala un problema": the host reports a problem with a completed job paid inside CasaZen and the money CasaZen holds for it stops until a person
    /// decides. Possible from the completion until the money is due to the supplier, never after "all fine" or the release; for a job nobody marked completed,
    /// from the end of the time agreed for it. A complaint already open answers the same dispute and writes nothing.
    /// </summary>
    [HttpPost("{id:guid}/report-problem")]
    [ProducesResponseType(typeof(ServiceDisputePayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<ServiceDisputePayerDto>> Report(
        Guid id,
        [FromBody] ReportServiceProblemRequest request,
        CancellationToken cancellationToken) => ReportAsync(id, request, cancellationToken);

    /// <summary>The host takes its complaint back: the money is no longer stopped and the supplier is paid. Asking again answers the same dispute.</summary>
    [HttpPost("{id:guid}/withdraw-problem")]
    [ProducesResponseType(typeof(ServiceDisputePayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<ActionResult<ServiceDisputePayerDto>> Withdraw(Guid id, CancellationToken cancellationToken) => WithdrawAsync(id, cancellationToken);
}

/// <summary>The complaint about a job of a long-rent property: <c>api/long-rent/service-requests/{id}/…</c>, the same as the short-rent door.</summary>
[ApiController]
[Route("api/long-rent/service-requests")]
[Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
public sealed class LongRentServiceRequestDisputesController(
    IServiceRequestService serviceRequests,
    IServiceDisputeService disputes,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver)
    : HostServiceRequestDisputesControllerBase(serviceRequests, disputes, authorizationService, orgContextResolver, hostScopeResolver)
{
    protected override ServiceRequestRentalContext Context => ServiceRequestRentalContext.LongRent;

    protected override HostOperationRequirement WriteOperation => LongRentPropertyOperations.Write;

    /// <summary>The complaint about the job as the landlord reads it. 404 <c>service_dispute_not_found</c> when there is none.</summary>
    [HttpGet("{id:guid}/problem")]
    [ProducesResponseType(typeof(ServiceDisputePayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<ActionResult<ServiceDisputePayerDto>> Get(Guid id, CancellationToken cancellationToken) => ReadAsync(id, cancellationToken);

    /// <summary>"Segnala un problema" for a long-rent request: the same rules and errors as <c>POST api/service-requests/{id}/report-problem</c>.</summary>
    [HttpPost("{id:guid}/report-problem")]
    [ProducesResponseType(typeof(ServiceDisputePayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<ServiceDisputePayerDto>> Report(
        Guid id,
        [FromBody] ReportServiceProblemRequest request,
        CancellationToken cancellationToken) => ReportAsync(id, request, cancellationToken);

    /// <summary>The landlord takes its complaint back: the same as <c>POST api/service-requests/{id}/withdraw-problem</c>.</summary>
    [HttpPost("{id:guid}/withdraw-problem")]
    [ProducesResponseType(typeof(ServiceDisputePayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<ActionResult<ServiceDisputePayerDto>> Withdraw(Guid id, CancellationToken cancellationToken) => WithdrawAsync(id, cancellationToken);
}
