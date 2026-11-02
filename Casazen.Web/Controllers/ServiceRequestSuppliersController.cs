using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The suppliers the host already asked for a job (MKT, SR-09c): «Le mie app» of the marketplace of Affitti brevi. A view derived from
/// the short-rent requests of the caller's org (<see cref="ServiceRequestsController"/> lists the requests themselves), in its own
/// controller so that the address <c>api/service-requests/suppliers</c> is not mistaken for the id of a request.
/// </summary>
[ApiController]
[Route("api/service-requests/suppliers")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class ServiceRequestSuppliersController(
    IHostSupplierUsageReader usage,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The suppliers with at least one short-rent request of the caller's org, the one asked most recently first: <c>supplierOrgId</c>,
    /// <c>supplierName</c>, <c>requestsCount</c>, <c>openCount</c>, <c>lastRequestAt</c>, <c>lastCategory</c> and nothing more (no
    /// contact, no price, no property). <c>property.read</c>, and the scope of the caller (a collaborator «Solo alcuni» counts only the
    /// requests of the properties it was given); another org's requests are never read. One page with all of them, as the picker.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<UsedSupplierDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResultDto<UsedSupplierDto>>> List(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return Unauthorized();

        var suppliers = (await usage.ListAsync(scope, cancellationToken)).Select(UsedSupplierDto.From).ToList();

        return Ok(new PagedResultDto<UsedSupplierDto>
        {
            Items = suppliers,
            TotalCount = suppliers.Count,
            Page = 1,
            PageSize = suppliers.Count,
        });
    }
}
