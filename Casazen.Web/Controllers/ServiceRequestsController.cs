using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Service requests (TN-3). Host side, short-rent context: <c>property.read</c> / <c>property.write</c>, then the
/// property is authorized as a <see cref="HostResource"/> (org, permission, ownership). Supplier side:
/// <see cref="CasazenPolicies.Supplier"/> plus the linked supplier org. <c>GET</c> list and detail serve both sides and
/// evaluate the policy of the branch they take.
/// </summary>
/// <remarks>
/// Decision D2 (SU-07): the host side here is the short-rent one, where a request is for one stay: <c>POST</c> needs a
/// <c>bookingId</c> of the property, and the host endpoints reach only <see cref="ServiceRequestRentalContext.ShortRent"/>
/// requests. Web and app list them the same way: <c>?bookingId=</c> for a stay, <c>?propertyId=</c> for a property.
/// Long-rent requests (per property) live in <see cref="LongRentServiceRequestsController"/>.
/// </remarks>
[ApiController]
[Route("api/service-requests")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
public class ServiceRequestsController(
    IServiceRequestService serviceRequestService,
    ISupplierMatchService supplierMatchService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    /// <summary>
    /// AI-assisted supplier match (D11: behind <see cref="FeatureFlags.AiSupplierDiscovery"/>, off by default, 404 while
    /// off). Rate limited per user and org; the category must be one of <see cref="ServiceCategories.All"/>; the host's
    /// notes are not accepted. The manual request (<c>POST api/service-requests</c>) does not depend on it.
    /// </summary>
    [HttpPost("match-supplier")]
    [FeatureGate(FeatureFlags.AiSupplierDiscovery)]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [AiRateLimit]
    [ProducesResponseType(typeof(SupplierMatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SupplierMatchResponse>> MatchSupplier(
        [FromBody] MatchSupplierRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null) return Unauthorized();

        if (await AuthorizePropertyAsync(request.PropertyId, PropertyOperations.Write, cancellationToken) is { } denied)
            return denied;

        var result = await supplierMatchService.MatchAsync(
            orgId.Value,
            request.PropertyId,
            request.Category,
            request.Urgency,
            cancellationToken);
        return Ok(MapMatchResult(result));
    }

    /// <summary>
    /// Sends a short-rent request for a stay of the property to a supplier. 400 <c>validation_error</c> for a malformed
    /// body (empty ids, notes over 1000 characters, no category); 404 <c>property_not_found</c> /
    /// <c>supplier_not_found</c>; 422 for the rules (category code, stay of the property, supplier active and covering
    /// the comune, <c>chargeToGuest</c>), with the codes of <see cref="ServiceRequestErrorCodes"/>.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Create(
        [FromBody] CreateServiceRequestRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (orgId is null || userId is null) return Unauthorized();

        if (await AuthorizePropertyAsync(request.PropertyId, PropertyOperations.Write, cancellationToken) is { } denied)
            return denied;

        var created = await serviceRequestService.CreateAsync(
            new CreateServiceRequestCommand(
                orgId.Value,
                userId,
                request.PropertyId,
                request.BookingId,
                request.SupplierOrgId,
                request.Category,
                request.Urgency,
                request.Notes,
                request.ChargeToGuest,
                ServiceRequestRentalContext.ShortRent),
            cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapDto(created));
    }

    /// <summary>
    /// Host list of short-rent requests (<c>property.read</c>), or the supplier inbox with <c>view=supplier</c>
    /// (<see cref="CasazenPolicies.Supplier"/>). <c>bookingId</c> lists one stay's requests, <c>propertyId</c> a
    /// property's (every stay, plus the older requests not traced to a stay): a booking or property that is not in the
    /// caller's org is 404, one the caller may not read is 403.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ServiceRequestListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ServiceRequestListResponse>> List(
        [FromQuery] string? status,
        [FromQuery] Guid? propertyId,
        [FromQuery] Guid? bookingId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? view = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        ServiceRequestStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ServiceRequestStatus>(status, true, out var parsed))
            statusFilter = parsed;

        if (string.Equals(view, "supplier", StringComparison.OrdinalIgnoreCase))
        {
            if (!await SatisfiesAsync(CasazenPolicies.Supplier))
                return Forbid();

            var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
            if (supplierOrgId is null) return NotFound();

            var openOnly = string.Equals(status, "open", StringComparison.OrdinalIgnoreCase);
            var (items, total) = await serviceRequestService.ListForSupplierAsync(
                supplierOrgId.Value, openOnly, page, pageSize, cancellationToken);

            return Ok(new ServiceRequestListResponse
            {
                Items = items.Select(MapDto),
                Total = total,
                Page = page,
                PageSize = pageSize,
            });
        }

        if (!await SatisfiesAsync(CasazenPolicies.PropertyRead))
            return Forbid();

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        if (await AuthorizeListFiltersAsync(propertyId, bookingId, cancellationToken) is { } denied)
            return denied;

        var (hostItems, hostTotal) = await serviceRequestService.ListForHostAsync(
            scope, ServiceRequestRentalContext.ShortRent, statusFilter, propertyId, bookingId, page, pageSize, cancellationToken);

        return Ok(new ServiceRequestListResponse
        {
            Items = hostItems.Select(MapDto),
            Total = hostTotal,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>
    /// A request of the caller's supplier org (<see cref="CasazenPolicies.Supplier"/>) or of the caller's host scope
    /// (<c>property.read</c>); 404 when it is in neither.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ServiceRequestDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var isSupplier = await SatisfiesAsync(CasazenPolicies.Supplier);
        if (isSupplier)
        {
            var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
            if (supplierOrgId is not null)
            {
                var supplierRequest = await serviceRequestService.GetByIdForSupplierAsync(id, supplierOrgId.Value, cancellationToken);
                if (supplierRequest is not null)
                    return Ok(MapDto(supplierRequest));
            }
        }

        if (!await SatisfiesAsync(CasazenPolicies.PropertyRead))
            return isSupplier ? ServiceRequestNotFound() : Forbid();

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        var hostRequest = await serviceRequestService.GetByIdForHostAsync(
            id, scope, ServiceRequestRentalContext.ShortRent, cancellationToken);
        if (hostRequest is null) return ServiceRequestNotFound();

        return Ok(MapDto(hostRequest));
    }

    /// <summary>
    /// Supplier transitions (<see cref="CasazenPolicies.Supplier"/>, linked supplier org), following
    /// <see cref="Casazen.Core.Suppliers.ServiceRequestStateMachine"/>: 404 <c>service_request_not_found</c>, 403 for a
    /// request sent to another supplier, 422 <c>service_request_invalid_transition</c> from a status that does not allow
    /// it, 409 <c>service_request_state_changed</c> when a concurrent operation changed the request first (A4-19).
    /// </summary>
    [HttpPost("{id:guid}/take")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Take(Guid id, CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (supplierOrgId is null || userId is null) return NotFound();

        var updated = await serviceRequestService.TakeAsync(id, supplierOrgId.Value, userId, cancellationToken);
        return Ok(MapDto(updated));
    }

    /// <summary>
    /// The supplier completes a request it took; notes sent replace the request's notes (at most 1000 characters,
    /// 400 otherwise). Same errors as <see cref="Take"/>.
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Complete(
        Guid id,
        [FromBody] CompleteServiceRequestRequest? request,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (supplierOrgId is null) return NotFound();

        var updated = await serviceRequestService.CompleteAsync(
            id, supplierOrgId.Value, request?.Notes, cancellationToken);
        return Ok(MapDto(updated));
    }

    /// <summary>
    /// The supplier refuses a new request: the reason is required, at most 500 characters (400 otherwise, A4-18). Same
    /// errors as <see cref="Take"/>.
    /// </summary>
    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Reject(
        Guid id,
        [FromBody] RejectServiceRequestRequest request,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (supplierOrgId is null) return NotFound();

        var updated = await serviceRequestService.RejectAsync(
            id, supplierOrgId.Value, request.Reason, cancellationToken);
        return Ok(MapDto(updated));
    }

    /// <summary>
    /// Host marks a completed short-rent request as paid (manual flag, no Stripe transfer): <c>property.write</c> on the
    /// request's property. A request outside the caller's host scope, or a long-rent one, is 404
    /// <c>service_request_not_found</c>; a request that is not completed is 422
    /// <c>service_request_invalid_transition</c>; 409 <c>service_request_state_changed</c> on a concurrent change.
    /// </summary>
    [HttpPost("{id:guid}/mark-paid")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> MarkPaid(Guid id, CancellationToken cancellationToken)
    {
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        var existing = await serviceRequestService.GetByIdForHostAsync(
            id, scope, ServiceRequestRentalContext.ShortRent, cancellationToken);
        if (existing is null) return ServiceRequestNotFound();

        var resource = new HostResource(existing.OrgId, existing.Property?.OwnerId);
        if (existing.Property is null ||
            !await authorizationService.IsAuthorizedAsync(User, resource, PropertyOperations.Write))
            return Forbid();

        var updated = await serviceRequestService.MarkPaidAsync(id, scope.OrgId, cancellationToken);
        return Ok(MapDto(updated));
    }

    /// <summary>404 <c>service_request_not_found</c> (FD-05), also for a request outside the caller's scope.</summary>
    internal static ObjectResult ServiceRequestNotFound(ControllerBase controller) =>
        controller.ApiProblem(
            StatusCodes.Status404NotFound, ServiceRequestErrorCodes.NotFound, ServiceRequestErrorCodes.NotFoundMessageKey);

    private ObjectResult ServiceRequestNotFound() => ServiceRequestNotFound(this);

    private async Task<bool> SatisfiesAsync(string policy) =>
        (await authorizationService.AuthorizeAsync(User, policy)).Succeeded;

    private async Task<HostScope?> GetHostScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : User.GetHostScope(orgId.Value);
    }

    /// <summary>
    /// The stay and the property a host list filters on (SU-07): 404 when not in the caller's org, 403 when the caller
    /// may not read them.
    /// </summary>
    private async Task<ActionResult?> AuthorizeListFiltersAsync(
        Guid? propertyId,
        Guid? bookingId,
        CancellationToken cancellationToken)
    {
        if (bookingId is { } stayId)
        {
            var booking = await hostResources.ForBookingAsync(stayId, cancellationToken);
            if (booking is null)
                return this.ApiProblem(StatusCodes.Status404NotFound, "booking_not_found", "BookingNotFound");

            if (!await authorizationService.IsAuthorizedAsync(User, booking, PropertyOperations.Read))
                return Forbid();
        }

        return propertyId is { } id
            ? await AuthorizePropertyAsync(id, PropertyOperations.Read, cancellationToken)
            : null;
    }

    /// <summary>404 when the property is not in the caller's org, 403 when the operation is not allowed on it.</summary>
    private async Task<ActionResult?> AuthorizePropertyAsync(
        Guid propertyId,
        HostOperationRequirement operation,
        CancellationToken cancellationToken)
    {
        var property = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (property is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        return await authorizationService.IsAuthorizedAsync(User, property, operation) ? null : Forbid();
    }

    internal static ServiceRequestDto MapDto(ServiceRequest r) => new()
    {
        Id = r.Id,
        OrgId = r.OrgId,
        BookingId = r.BookingId,
        RentalContext = r.RentalContext.ToString(),
        PropertyId = r.PropertyId,
        PropertyName = r.Property?.Name,
        SupplierOrgId = r.SupplierOrgId,
        SupplierName = r.SupplierOrg?.DisplayName ?? r.SupplierOrg?.Name,
        Category = r.Category,
        Urgency = r.Urgency.ToString(),
        Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes,
        Status = r.Status.ToString(),
        TakenAt = r.TakenAt,
        TakenByUserId = r.TakenByUserId,
        CompletedAt = r.CompletedAt,
        PaidAt = r.PaidAt,
        ChargeToGuest = r.ChargeToGuest,
        RejectionReason = r.RejectionReason,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    private static SupplierMatchResponse MapMatchResult(SupplierMatchResult result) => new()
    {
        Recommended = result.Recommended is null ? null : MapCandidate(result.Recommended),
        Alternatives = result.Alternatives.Select(MapCandidate),
        ExternalSuggestions = result.ExternalSuggestions.Select(e => new ExternalSupplierSuggestionDto
        {
            Name = e.Name,
            Address = e.Address,
            Phone = e.Phone,
            Email = e.Email,
            Rating = e.Rating,
            ReviewCount = e.ReviewCount,
            GoogleMapsUrl = e.GoogleMapsUrl,
            WebsiteUrl = e.WebsiteUrl,
            Source = e.Source,
        }),
        UsedExternalFallback = result.UsedExternalFallback,
    };

    private static SupplierMatchCandidateDto MapCandidate(SupplierMatchCandidate c) => new()
    {
        OrgId = c.OrgId,
        LegalName = c.LegalName,
        Phone = c.Phone,
        Email = c.Email,
        Bio = c.Bio,
        MatchScore = c.MatchScore,
        MatchReason = c.MatchReason,
        Source = c.Source,
    };
}
