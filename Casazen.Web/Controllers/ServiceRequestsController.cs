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
/// Service requests (TN-3). Host side: <c>property.read</c> / <c>property.write</c> (both rental contexts), then the
/// property is authorized as a <see cref="HostResource"/> (org, permission, ownership). Supplier side:
/// <see cref="CasazenPolicies.Supplier"/> plus the linked supplier org. <c>GET</c> list and detail serve both sides and
/// evaluate the policy of the branch they take.
/// </summary>
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

    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
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

        try
        {
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
                    request.ChargeToGuest),
                cancellationToken);

            return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapDto(created));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Host list (<c>property.read</c>), or the supplier inbox with <c>view=supplier</c> (<see cref="CasazenPolicies.Supplier"/>).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ServiceRequestListResponse), StatusCodes.Status200OK)]
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

        var (hostItems, hostTotal) = await serviceRequestService.ListForHostAsync(
            scope, statusFilter, propertyId, bookingId, page, pageSize, cancellationToken);

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
            return isSupplier ? NotFound() : Forbid();

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        var hostRequest = await serviceRequestService.GetByIdForHostAsync(id, scope, cancellationToken);
        if (hostRequest is null) return NotFound();

        return Ok(MapDto(hostRequest));
    }

    [HttpPost("{id:guid}/take")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServiceRequestDto>> Take(Guid id, CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (supplierOrgId is null || userId is null) return NotFound();

        try
        {
            var updated = await serviceRequestService.TakeAsync(id, supplierOrgId.Value, userId, cancellationToken);
            return Ok(MapDto(updated));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServiceRequestDto>> Complete(
        Guid id,
        [FromBody] CompleteServiceRequestRequest? request,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (supplierOrgId is null) return NotFound();

        try
        {
            var updated = await serviceRequestService.CompleteAsync(
                id, supplierOrgId.Value, request?.Notes, cancellationToken);
            return Ok(MapDto(updated));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServiceRequestDto>> Reject(
        Guid id,
        [FromBody] RejectServiceRequestRequest request,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (supplierOrgId is null) return NotFound();

        try
        {
            var updated = await serviceRequestService.RejectAsync(
                id, supplierOrgId.Value, request.Reason, cancellationToken);
            return Ok(MapDto(updated));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Host marks a completed request as paid (manual flag, no Stripe transfer): <c>property.write</c> on the request's
    /// property. A request outside the caller's host scope is 404.
    /// </summary>
    [HttpPost("{id:guid}/mark-paid")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServiceRequestDto>> MarkPaid(Guid id, CancellationToken cancellationToken)
    {
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        var existing = await serviceRequestService.GetByIdForHostAsync(id, scope, cancellationToken);
        if (existing is null) return NotFound();

        var resource = new HostResource(existing.OrgId, existing.Property?.OwnerId);
        if (existing.Property is null ||
            !await authorizationService.IsAuthorizedAsync(User, resource, PropertyOperations.Write))
            return Forbid();

        try
        {
            var updated = await serviceRequestService.MarkPaidAsync(id, scope.OrgId, cancellationToken);
            return Ok(MapDto(updated));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
    }

    private async Task<bool> SatisfiesAsync(string policy) =>
        (await authorizationService.AuthorizeAsync(User, policy)).Succeeded;

    private async Task<HostScope?> GetHostScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : User.GetHostScope(orgId.Value);
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

    internal static ServiceRequestSummaryDto MapSummary(ServiceRequest r) => new()
    {
        Id = r.Id,
        PropertyId = r.PropertyId,
        PropertyName = r.Property?.Name ?? string.Empty,
        Category = r.Category,
        Urgency = r.Urgency.ToString(),
        Status = r.Status.ToString(),
        Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes,
        CreatedAt = r.CreatedAt,
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
