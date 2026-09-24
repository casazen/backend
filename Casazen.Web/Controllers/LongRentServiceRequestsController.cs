using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// Service requests of long-term rentals (decision D2, SU-07): a request is for a property, never for a booking. Opened
/// explicitly in the long-rent context, which shares only the property core with short stays (LT-05): the class needs
/// <c>property.read</c> held in long-rent, the writes <c>property.write</c> held in long-rent, and each property is
/// authorized as a <see cref="HostResource"/> with <see cref="LongRentPropertyOperations"/> (org, permission,
/// ownership). Only what a landlord needs: find a supplier for the property, send the request, list the property's
/// requests, mark a completed one paid. These endpoints reach only <see cref="ServiceRequestRentalContext.LongRent"/>
/// requests; the supplier side (take, complete, reject) is the same as for short stays (<c>api/service-requests</c>).
/// </summary>
[ApiController]
[Route("api/long-rent/service-requests")]
[Authorize(Policy = CasazenPolicies.LongRentPropertyRead)]
public class LongRentServiceRequestsController(
    IServiceRequestService serviceRequestService,
    ISupplierService supplierService,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    AppDbContext db) : ControllerBase
{
    /// <summary>
    /// The long-rent requests in the caller's scope, newest first; with <c>propertyId</c> only that property's (404 when
    /// it is not in the caller's org, 403 when the caller may not read it).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ServiceRequestListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ServiceRequestListResponse>> List(
        [FromQuery] Guid? propertyId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        ServiceRequestStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ServiceRequestStatus>(status, true, out var parsed))
            statusFilter = parsed;

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        if (propertyId is { } id)
        {
            var (_, denied) = await AuthorizePropertyAsync(id, LongRentPropertyOperations.Read, cancellationToken);
            if (denied is not null) return denied;
        }

        var (items, total) = await serviceRequestService.ListForHostAsync(
            scope, ServiceRequestRentalContext.LongRent, statusFilter, propertyId, bookingId: null, page, pageSize, cancellationToken);

        return Ok(new ServiceRequestListResponse
        {
            Items = items.Select(ServiceRequestsController.MapDto),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>
    /// Active suppliers operating in the property's comune, optionally for one category code of
    /// <c>GET /api/service-categories</c> (422 <c>invalid_service_category</c> otherwise). Same answer as the
    /// short-rent <c>GET /api/suppliers?propertyId=</c>, authorized in the long-rent context.
    /// </summary>
    [HttpGet("suppliers")]
    [ProducesResponseType(typeof(PagedResultDto<SupplierPickerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResultDto<SupplierPickerDto>>> GetSuppliers(
        [FromQuery] Guid propertyId,
        [FromQuery] string? category,
        CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizePropertyAsync(propertyId, LongRentPropertyOperations.Read, cancellationToken);
        if (denied is not null) return denied;

        var suppliers = await supplierService.GetActiveByComune(property!.City, category, cancellationToken);

        return Ok(new PagedResultDto<SupplierPickerDto>
        {
            Items = suppliers.Select(SupplierPickerDto.From).ToList(),
            TotalCount = suppliers.Count,
            Page = 1,
            PageSize = suppliers.Count,
        });
    }

    /// <summary>
    /// Sends a long-rent request for the property to the supplier. A <c>bookingId</c> is refused (422
    /// <c>service_request_booking_not_allowed</c>): in long-term rental the request is for the property.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Create(
        [FromBody] CreateServiceRequestRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (orgId is null || userId is null) return Unauthorized();

        var (_, denied) = await AuthorizePropertyAsync(request.PropertyId, LongRentPropertyOperations.Write, cancellationToken);
        if (denied is not null) return denied;

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
                    request.ChargeToGuest,
                    ServiceRequestRentalContext.LongRent),
                cancellationToken);

            return Created($"/api/long-rent/service-requests/{created.Id}", ServiceRequestsController.MapDto(created));
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
    /// The landlord marks a completed long-rent request as paid (manual flag, no Stripe transfer): <c>property.write</c>
    /// in long-rent on the request's property. A request outside the caller's scope, or a short-rent one, is 404.
    /// </summary>
    [HttpPost("{id:guid}/mark-paid")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServiceRequestDto>> MarkPaid(Guid id, CancellationToken cancellationToken)
    {
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        var existing = await serviceRequestService.GetByIdForHostAsync(
            id, scope, ServiceRequestRentalContext.LongRent, cancellationToken);
        if (existing?.Property is null) return NotFound();

        var resource = new HostResource(existing.OrgId, existing.Property.OwnerId);
        if (!await authorizationService.IsAuthorizedAsync(User, resource, LongRentPropertyOperations.Write))
            return Forbid();

        try
        {
            var updated = await serviceRequestService.MarkPaidAsync(id, scope.OrgId, cancellationToken);
            return Ok(ServiceRequestsController.MapDto(updated));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
    }

    private async Task<HostScope?> GetHostScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : User.GetHostScope(orgId.Value);
    }

    /// <summary>
    /// The property (tenant filter: another org's is not found) and the outcome of <paramref name="operation"/> on it:
    /// 404 when it is not in the caller's org, 403 when the operation is not allowed.
    /// </summary>
    private async Task<(PropertyRef? Property, ActionResult? Denied)> AuthorizePropertyAsync(
        Guid propertyId,
        HostOperationRequirement operation,
        CancellationToken cancellationToken)
    {
        var property = await db.Properties
            .AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => new PropertyRef(p.OrgId, p.OwnerId, p.City))
            .FirstOrDefaultAsync(cancellationToken);

        if (property is null)
            return (null, this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound"));

        var resource = new HostResource(property.OrgId, property.OwnerId);
        return await authorizationService.IsAuthorizedAsync(User, resource, operation)
            ? (property, null)
            : (null, Forbid());
    }

    private sealed record PropertyRef(Guid OrgId, string OwnerId, string City);
}
