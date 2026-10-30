using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// What the host reads of a supplier to ask it for an intervention (SR-08a): its published services and the free slots of one of them,
/// for a property of the host (<c>property.read</c> in short-rent, the property authorized as a <see cref="HostResource"/>: org,
/// permission, scope). Reads only, no flag: nothing changes. Both answer for a supplier that is active and covers the comune of the
/// property, with the codes the creation of a request uses.
/// </summary>
[ApiController]
[Route("api/suppliers/{supplierOrgId:guid}/services")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class HostSupplierCatalogController(
    IHostSupplierCatalogService catalog,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// The <c>Active</c> services of the supplier in its catalog order, with the price a request for each starts with. <c>propertyId</c>
    /// is required (400 <c>validation_error</c> without it, 404 <c>property_not_found</c> outside the caller's org, 403 for a property the
    /// caller may not read); <c>category</c> narrows the list to a code of <c>GET api/service-categories</c> (422
    /// <c>invalid_service_category</c> otherwise). 404 <c>supplier_not_found</c>; 422 <c>service_request_supplier_inactive</c> or
    /// <c>service_request_supplier_outside_comune</c>. No public text, photo, supplement or slug of the service comes out.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(HostSupplierServicesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HostSupplierServicesResponse>> ListServices(
        Guid supplierOrgId,
        [FromQuery] Guid propertyId,
        [FromQuery] string? category,
        CancellationToken cancellationToken)
    {
        var (failure, hostOrgId) = await AuthorizeAsync(propertyId, cancellationToken);
        if (failure is not null)
            return failure;

        var services = await catalog.ListServicesAsync(hostOrgId, propertyId, supplierOrgId, category, cancellationToken);
        return Ok(new HostSupplierServicesResponse { Items = services.Select(HostSupplierServiceDto.From).ToList() });
    }

    /// <summary>
    /// The free slots of a service of the supplier, for the property: the planner's slots (hours, time off, closed days, notice, daily
    /// maximum, buffer, what is already booked) that the creation of a request accepts for the service, from <c>date</c> (<c>yyyy-MM-dd</c>,
    /// Europe/Rome; never before today, today when missing) for 14 days at most, cut at the supplier's horizon. A first day past the horizon,
    /// or a service without a duration, is an empty list. A slot can be taken by someone else before the request is sent: 409
    /// <c>supplier_slot_unavailable</c> on the <c>POST</c>. Rate limited per person. Same errors as <see cref="ListServices"/>, and 404
    /// <c>supplier_service_not_found</c> for a service that is not the supplier's, 422 <c>service_request_service_unavailable</c> for one that
    /// is not published.
    /// </summary>
    [HttpGet("{serviceId:guid}/slots")]
    [EnableRateLimiting(RateLimitPolicies.HostSupplierSlots)]
    [ProducesResponseType(typeof(HostSupplierSlotsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<HostSupplierSlotsResponse>> GetSlots(
        Guid supplierOrgId,
        Guid serviceId,
        [FromQuery] Guid propertyId,
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        var (failure, hostOrgId) = await AuthorizeAsync(propertyId, cancellationToken);
        if (failure is not null)
            return failure;

        var slots = await catalog.GetSlotsAsync(hostOrgId, propertyId, supplierOrgId, serviceId, date, cancellationToken);
        return Ok(HostSupplierSlotsResponse.Of(slots));
    }

    /// <summary>
    /// The property of the read: required (400), in the caller's org (404) and readable by the caller (403), as the supplier picker
    /// authorizes it. Returns the failure, or the caller's org.
    /// </summary>
    private async Task<(ActionResult? Failure, Guid OrgId)> AuthorizeAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        if (propertyId == Guid.Empty)
        {
            return (this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "ServiceRequestPropertyRequired"), Guid.Empty);
        }

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return (Unauthorized(), Guid.Empty);

        var property = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (property is null)
            return (this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound"), Guid.Empty);

        return await authorizationService.IsAuthorizedAsync(User, property, PropertyOperations.Read)
            ? (null, orgId.Value)
            : (Forbid(), Guid.Empty);
    }
}
