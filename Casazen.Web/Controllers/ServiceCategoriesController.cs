using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Catalog of the service categories of the supplier marketplace (SU-03, A4-05 / A6-03). Web and app build their
/// category pickers and filters from this list instead of their own hardcoded ones, and translate each code
/// themselves. The same codes are the only values accepted by the supplier profile, admin invites, service requests
/// and the supplier search (<see cref="ServiceCategories"/>).
/// </summary>
[ApiController]
[Route("api/service-categories")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
public class ServiceCategoriesController : ControllerBase
{
    /// <summary>Every service category code, in display order.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ServiceCategoriesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<ServiceCategoriesResponse> GetAll() =>
        Ok(new ServiceCategoriesResponse
        {
            Items = ServiceCategories.All.Select(code => new ServiceCategoryDto { Code = code }).ToList(),
        });
}
